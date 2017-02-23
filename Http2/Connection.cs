using System;
using System.Buffers;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Http2
{
    /// <summary>
    /// A HTTP/2 connection
    /// </summary>
    public class Connection
    {
        /// <summary>
        /// Optional configuration options, which are valid per connection
        /// </summary>
        public struct Options
        {
            /// <summary>
            /// Optional logger
            /// </summary>
            public ILogger Logger;
        }

        private struct SharedData
        {
            public object Mutex;
            public bool Closed;
            public Dictionary<uint, StreamImpl> streamMap;
            /// The last and maximum stream ID that was sent to the remote.
            /// 0 means we never sent anything
            public uint LastOutgoingStreamId;
            /// The last and maximum stream ID that was received from the remote.
            /// 0 means we never received anything
            public uint LastIncomingStreamId;
            /// Whether a GoAway message has been sent
            public bool GoAwaySent;
            /// Tracks active PINGs. Only initialized when needed
            public PingState PingState;
        }

        /// <summary>
        /// Contains information about active PINGs
        /// </summary>
        private class PingState
        {
            public ulong Counter = 0;
            public Dictionary<ulong, TaskCompletionSource<bool>> PingMap =
                new Dictionary<ulong, TaskCompletionSource<bool>>();
        }

        private SharedData shared;

        private static readonly ArrayPool<byte> _pool = ArrayPool<byte>.Shared;
        byte[] receiveBuffer;

        /// <summary>Whether the initial settings have been received from the remote</summary>
        bool settingsReceived = false;
        int nrUnackedSettings = 0;
        /// <summary>Flow control window for the connection</summary>
        private int connReceiveFlowWindow = Constants.InitialConnectionWindowSize;

        internal readonly ILogger logger;

        internal readonly ConnectionWriter writer;
        internal readonly IReadableByteStream inputStream;
        private readonly Task readerDone;

        internal readonly ConnectionConfiguration config;

        private readonly HeaderReader headerReader;
        internal readonly Settings localSettings;
        internal Settings remoteSettings = Settings.Default;

        private TaskCompletionSource<GoAwayReason> remoteGoAwayTcs =
            new TaskCompletionSource<GoAwayReason>();

        /// <summary>
        /// Whether the connection represents the client or server part of
        /// a HTTP/2 connection. True for servers.
        /// </summary>
        public bool IsServer => config.IsServer;

        /// <summary>
        /// Returns a Task that will be completed once the Connection has been
        /// fully closed.
        /// </summary>
        public Task Done => readerDone;

        /// <summary>
        /// Returns a Task that will be completed once a GoAway frame from the
        /// remote side was received.
        /// If the connection closes without a GoAway frame the Task will get
        /// completed with an EndOfStreamException
        /// </summary>
        public Task<GoAwayReason> RemoteGoAwayReason => remoteGoAwayTcs.Task;

        /// <summary>
        /// Creates a new HTTP/2 connection on top of the a bidirectional stream
        /// </summary>
        /// <param name="config">
        /// The configuration options that are applied to the connection and
        /// which are shared between multiple connections.
        /// </param>
        /// <param name="inputStream">
        /// The stream which is used for receiving data
        /// </param>
        /// <param name="outputStream">
        /// The stream which is used for writing data
        /// </param>
        /// <param name="options">
        /// Optional configuration options which are unique per connection
        /// </param>
        public Connection(
            ConnectionConfiguration config,
            IReadableByteStream inputStream,
            IWriteAndCloseableByteStream outputStream,
            Options? options = null)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            this.config = config;
            this.logger = options?.Logger;

            // TODO: As long as they are not changeable there's actually no need for the field
            localSettings = config.Settings;
            // Disable server push as it's not supported.
            // Disabling it here is easier than wanting a custom config from the
            // user which disables it.
            localSettings.EnablePush = false;
            // TODO: If the remote settings are not the default ones we will
            // also need to validate those

            if (inputStream == null) throw new ArgumentNullException(nameof(inputStream));
            if (outputStream == null) throw new ArgumentNullException(nameof(outputStream));
            this.inputStream = inputStream;

            // Allocate a receive buffer from the pool
            receiveBuffer = _pool.Rent(
                (int)localSettings.MaxFrameSize + FrameHeader.HeaderSize);

            // Initialize shared data
            shared.Mutex = new object();
            shared.streamMap = new Dictionary<uint, StreamImpl>();
            shared.LastOutgoingStreamId = 0u;
            shared.LastIncomingStreamId = 0u;
            shared.GoAwaySent = false;
            shared.Closed = false;
            shared.PingState = null;

            // Clamp the dynamic table size limit to int range.
            // We will use the dynamic table size limit that was set for
            // receiving headers also for sending headers, which means to limit
            // the header table size for header encoding.
            var dynTableSizeLimit = Math.Min(localSettings.HeaderTableSize, int.MaxValue);

            // Start the writing task
            writer = new ConnectionWriter(
                this, outputStream,
                new ConnectionWriter.Options
                {
                    MaxFrameSize = (int)remoteSettings.MaxFrameSize,
                    MaxHeaderListSize = (int)remoteSettings.MaxHeaderListSize,
                    DynamicTableSizeLimit = (int)dynTableSizeLimit,
                },
                new Hpack.Encoder.Options
                {
                    DynamicTableSize = (int)remoteSettings.HeaderTableSize,
                    HuffmanStrategy = config.HuffmanStrategy,
                }
            );

            headerReader = new HeaderReader(
                new Hpack.Decoder(new Hpack.Decoder.Options
                {
                    // Remark: The dynamic table size is set to the default
                    // value of 4096 if not configured here.
                    // Configuring it and setting it to something different
                    // makes no sense, as the remote expects the default size
                    // at start
                    DynamicTableSizeLimit = (int)dynTableSizeLimit,
                }),
                localSettings.MaxFrameSize,
                localSettings.MaxHeaderListSize,
                receiveBuffer,
                inputStream,
                logger
            );

            // Start the task that performs the actual reading.
            // The connection is closed once this task is fully finished.
            readerDone = Task.Run(() => this.RunReaderAsync());
        }

        /// <summary>
        /// This contains the main reading loop of the HTTP/2 connection
        /// </summary>
        private async Task RunReaderAsync()
        {
            try
            {
                // Enqueue writing the local settings
                // We do this before reading the preface since enqueuing these few
                // bytes should not block and is cheap, and we can reuse the
                // receiveBuffer for the write task.
                // On the client side the preface will still be written before
                // these settings, since it's handled by the ConnectionWriter.
                var encodedSettingsBuf = new ArraySegment<byte>(
                    receiveBuffer, 0, localSettings.RequiredSize);
                localSettings.EncodeInto(encodedSettingsBuf);
                await writer.WriteSettings(new FrameHeader{
                    Type = FrameType.Settings,
                    StreamId = 0,
                    Flags = 0,
                    Length = encodedSettingsBuf.Count,
                }, encodedSettingsBuf);
                nrUnackedSettings++;

                // If this is a server we need to read the preface first,
                // which is then followed by the remote SETTINGS
                if (IsServer)
                {
                    await ClientPreface.ReadAsync(inputStream, config.ClientPrefaceTimeout);
                    if (logger != null && logger.IsEnabled(LogLevel.Trace))
                    {
                        logger.LogTrace("rcvd ClientPreface");
                    }
                }

                var continueRead = true;
                while (continueRead)
                {
                    // Read and process a single HTTP/2 frame and it's data
                    var err = await ReadOneFrame();
                    if (err != null)
                    {
                        if (err.Value.StreamId == 0)
                        {
                            // Log the error
                            if (logger != null && logger.IsEnabled(LogLevel.Error))
                            {
                                logger.LogError("Handling connection error {0}", err.Value);
                            }

                            // The error is a connection error
                            // Write a suitable GOAWAY frame and stop the writer
                            await InitiateGoAway(err.Value.Code, true);
                            // We are not interested in the result of this.
                            // If the connection close couldn't be queued and
                            // performed then the the close was already initiated
                            // or performed before and the connection is in the
                            // process of shutting down.

                            // Stop to read
                            continueRead = false;
                        }
                        else
                        {
                            // Log the error
                            if (logger != null && logger.IsEnabled(LogLevel.Error))
                            {
                                logger.LogError("Handling stream error {0}", err.Value);
                            }

                            // The error is a stream error
                            // Check out if we know a stream with the given ID
                            StreamImpl stream = null;
                            lock (shared.Mutex)
                            {
                                shared.streamMap.TryGetValue(err.Value.StreamId, out stream);
                                // TODO: Does it make sense to remove the stream
                                // already here from the map or will that result
                                // in a race
                            }

                            if (stream != null)
                            {
                                // The stream is known
                                // Reset the stream locally, which will also
                                // enqueue a RST_STREAM frame and remove it from
                                // the map.
                                await stream.Reset(err.Value.Code, false);
                            }
                            else
                            {
                                // Send a reset frame with the given error code
                                var fh = new FrameHeader
                                {
                                    StreamId = err.Value.StreamId,
                                    Type = FrameType.ResetStream,
                                    Flags = 0,
                                };
                                var resetData = new ResetFrameData
                                {
                                    ErrorCode = err.Value.Code,
                                };
                                // Write the reset frame
                                // Not interested in the result.
                                // If the write fails the connection will get
                                // closed and we will get the error reported on
                                // the next read attempt.
                                await writer.WriteResetStream(fh, resetData);
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                // We will get here in all cases where the reading task encounters
                // an exception.
                // As most exceptions are gracefully handled the remaining ones
                // will only be cases where the reader fails.
                if (logger != null && logger.IsEnabled(LogLevel.Error))
                {
                    logger.LogError("Reader error: {0}", e.Message);
                }
            }

            // Shutdown the writer.
            // This is necessary if we get here through a read exception, e.g.
            // because the connection was closed. In this case no GOAWAY is
            // necessary.
            // Attempts to double-close the outgoing stream will be caugt and
            // avoid by the Writer, so calling this is always safe.
            await writer.CloseNow();

            // Wait until the Writer has been fully shut down.
            await writer.Done;

            // Cleanup all streams that are still open
            Dictionary<uint, StreamImpl> activeStreams = null;
            lock (shared.Mutex)
            {
                // Move the streamMap outside of the mutex. As it is no longer
                // accessed after cleanup besides the UnregisterStream stream
                // function which is guarded this is safe
                activeStreams = shared.streamMap;
                shared.streamMap = null;

                // Mark connection as closed
                shared.Closed = true;
            }
            foreach (var kvp in activeStreams)
            {
                // fromRemote is set to true since there's no need to send
                // a Reset frame and the stream will get removed from the
                // map here
                await kvp.Value.Reset(ErrorCode.ConnectError, fromRemote: true);
            }

            // Cleanup all pending Pings
            PingState pingState = null;
            lock (shared.Mutex)
            {
                // Extract the PingState
                // It will no longer be accessed by users, since the Closed flag
                // already has been set
                if (shared.PingState != null)
                {
                    pingState = shared.PingState;
                    shared.PingState = null;
                }
            }
            if (pingState != null)
            {
                var ex = new ConnectionClosedException();
                foreach (var kvp in pingState.PingMap)
                {
                    kvp.Value.SetException(ex);
                }
            }

            // If we haven't received a remote GoAway fail that Task
            if (!remoteGoAwayTcs.Task.IsCompleted)
            {
                remoteGoAwayTcs.TrySetException(new EndOfStreamException());
            }

            if (logger != null && logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace("Connection closed");
            }

            // Return the receiveBuffer back to the pool
            _pool.Return(receiveBuffer);
            receiveBuffer = null;

            // Dispose the hpack decoder
            headerReader.Dispose();

            // Once we got here the connection is fully closed and the Done
            // task will be fulfilled.
        }

        /// <summary>
        /// Forces closing the connection immediatly.
        /// This will not send a GOAWAY message.
        /// Pending streams will get reset.
        /// </summary>
        /// <returns>
        /// A task that will be completed once the connection has fully been
        /// shut down.
        /// </returns>
        public async Task CloseNow()
        {
            // Start by closing the writer, which will get the connection close
            // process (first writer, then reader) into progress.
            await writer.CloseNow();

            // And wait for the reader to be done
            await readerDone;
        }

        /// <summary>
        /// Sends a GOAWAY frame with the given error code to the remote.
        /// If closeConnection is set to true the connection will be closed
        /// after to frame has been sent, otherwise not.
        /// If the connection is also closed the returned task will wait until
        /// the connection has been fully shut down.
        /// </summary>
        public async Task GoAwayAsync(ErrorCode errorCode, bool closeConnection = false)
        {
            await InitiateGoAway(errorCode, closeConnection);
            if (closeConnection)
            {
                await Done;
            }
        }

        /// <summary>
        /// Sends a PING request to the remote and returns a Task.
        /// The task will be completed once the associated ping response had
        /// been received.
        /// </summary>
        public Task PingAsync()
        {
            return PingAsync(CancellationToken.None);
        }

        /// <summary>
        /// Sends a PING request to the remote and returns a Task.
        /// The task will be completed once the associated ping response had
        /// been received.
        /// </summary>
        public async Task PingAsync(CancellationToken ct)
        {
            ulong pingId = 0;
            Task waitTask = null;
            lock (shared.Mutex)
            {
                if (shared.Closed)
                {
                    // Connection is already closed
                    throw new ConnectionClosedException();
                }

                if (shared.PingState == null)
                {
                    shared.PingState = new PingState();
                }
                // TODO: Check if value is already in use
                pingId = shared.PingState.Counter;
                var tcs = new TaskCompletionSource<bool>();
                shared.PingState.PingMap[pingId] = tcs;
                shared.PingState.Counter++;
                waitTask = tcs.Task;
            }

            if (ct != CancellationToken.None)
            {
                ct.Register(() =>
                {
                    lock (shared.Mutex)
                    {
                        if (shared.PingState == null)
                        {
                            return;
                        }

                        TaskCompletionSource<bool> tcs = null;
                        if (shared.PingState.PingMap.TryGetValue(pingId, out tcs))
                        {
                            shared.PingState.PingMap.Remove(pingId);
                            tcs.TrySetCanceled();
                        }
                    }
                }, false);
            }

            // Serialize the counter
            var fh = new FrameHeader
            {
                Type = FrameType.Ping,
                Flags = 0,
                StreamId = 0u,
                Length = 8,
            };

            var pingBuffer = BitConverter.GetBytes(pingId);
            var writePingResult = await writer.WritePing(
                fh, new ArraySegment<byte>(pingBuffer));

            // Remark: There's no need to handle writePingResult
            // Sending a ping will only fail in case the connection closes
            // In this case the returned task will be failed by the cleanup
            // of the read task of the connection.

            // Wait for the response
            await waitTask;
        }

        /// <summary>
        /// Internal version of the GoAway function. This will not wait for the
        /// connection to close in order not to deadlock when it's u sed from
        /// inside the Reader routine.
        /// </summary>
        private async Task InitiateGoAway(ErrorCode errorCode, bool closeWriter)
        {
            uint lastProcessedStreamId = 0u;
            bool goAwaySent = false;
            lock (shared.Mutex)
            {
                lastProcessedStreamId = shared.LastIncomingStreamId;
                // Check if GoAway was already sent/queued
                goAwaySent = shared.GoAwaySent;
                shared.GoAwaySent = true;
            }

            if (goAwaySent)
            {
                // GoAway message has already been sent to remote.
                // Initiate only the close procedure here if required
                // If writer was not stopped do that now.
                // In this case a force close will be applied.
                if (closeWriter)
                {
                    await writer.CloseNow();
                }
            }
            else
            {
                var fh = new FrameHeader
                {
                    Type = FrameType.GoAway,
                    StreamId = 0,
                    Flags = 0,
                };

                var goAwayData = new GoAwayFrameData
                {
                    Reason = new GoAwayReason
                    {
                        LastStreamId = lastProcessedStreamId,
                        ErrorCode = errorCode,
                        DebugData = Constants.EmptyByteArray,
                    },
                };

                await writer.WriteGoAway(fh, goAwayData, closeWriter);
            }
        }

        private async ValueTask<Http2Error?> ReadOneFrame()
        {
            var fh = await FrameHeader.ReceiveAsync(inputStream, receiveBuffer);
            if (logger != null && logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace("recv " + FramePrinter.PrintFrameHeader(fh));
            }

            // The first thing that we need to receive after the preface
            // is a SETTINGS frame without ACK flag
            if (!settingsReceived)
            {
                if (fh.Type != FrameType.Settings || (fh.Flags & (byte)SettingsFrameFlags.Ack) != 0)
                {
                    return new Http2Error
                    {
                        StreamId = 0,
                        Code = ErrorCode.ProtocolError,
                        Message = "Expected SETTINGS frame as first frame",
                    };
                }
                // else handle settings normally
            }

            switch (fh.Type)
            {
                case FrameType.Settings:
                    return await HandleSettingsFrame(fh);
                case FrameType.Priority:
                    return await HandlePriorityFrame(fh);
                case FrameType.Ping:
                    return await HandlePingFrame(fh);
                case FrameType.WindowUpdate:
                    return await HandleWindowUpdateFrame(fh);
                case FrameType.PushPromise:
                    return await HandlePushPromiseFrame(fh);
                case FrameType.ResetStream:
                    return await HandleResetFrame(fh);
                case FrameType.GoAway:
                    return await HandleGoAwayFrame(fh);
                case FrameType.Continuation:
                    return new Http2Error
                    {
                        StreamId = 0,
                        Code = ErrorCode.ProtocolError,
                        Message = "Unexpected CONTINUATION frame",
                    };
                case FrameType.Data:
                    return await HandleDataFrame(fh);
                case FrameType.Headers:
                    // Use the header reader to get all headers combined
                    var headerRes = await headerReader.ReadHeaders(fh);
                    if (headerRes.Error != null) return headerRes.Error;
                    return await HandleHeaders(headerRes.HeaderData);
                default:
                    return await HandleUnknownFrame(fh);
            }
        }

        private async ValueTask<Http2Error?> HandleHeaders(CompleteHeadersFrameData headers)
        {
            // Headers with stream ID 0 are a connection error
            if (headers.StreamId == 0)
            {
                return new Http2Error
                {
                    StreamId = headers.StreamId,
                    Code = ErrorCode.ProtocolError,
                    Message = "Received HEADERS frame with stream ID 0",
                };
            }

            StreamImpl stream = null;
            uint lastOutgoingStream = 0u;
            uint lastIncomingStream = 0u;
            lock (shared.Mutex)
            {
                lastIncomingStream = shared.LastIncomingStreamId;
                lastOutgoingStream = shared.LastOutgoingStreamId;
                shared.streamMap.TryGetValue(headers.StreamId, out stream);
            }

            if (stream != null)
            {
                //Delegate processing of the HEADERS frame to the existing stream
                if (headers.Priority.HasValue)
                {
                    var handlePrioErr = HandlePriorityData(
                        headers.StreamId, headers.Priority.Value);
                    if (handlePrioErr != null)
                    {
                        return handlePrioErr;
                    }
                }
                return stream.ProcessHeaders(headers);
            }

            // This might be a new stream - or a protocol error
            var isServerInitiated = headers.StreamId % 2 == 0;
            var isRemoteInitiated =
                (IsServer && !isServerInitiated) || (!IsServer && isServerInitiated);

            var isValidNewStream =
                IsServer && // As a client don't accept HEADERS as a way to create a new stream
                isRemoteInitiated &&
                (headers.StreamId > lastIncomingStream);

            // Remark:
            // The HEADERS might also be trailers for a stream which has existed
            // in the past but which was resetted by us in between.
            // In this case receiving HEADERS for the the stream would be a reason
            // to send a stream error, but not a connection error.
            // As the Connection does not track the state of old streams we don't
            // have information about whether the incoming HEADERS are valid, and
            // can therefore not send a ProtocolError on the Connection.
            // Instead we always reset the stream.

            // TODO: If the stream is not remoteInitiated we might handle this
            // differently. E.g. we should at least check the stream ID against
            // the highest stream ID that we have used up to now.

            if (!isValidNewStream)
            {
                // Return an error, which will trigger sending RST_STREAM
                return new Http2Error
                {
                    StreamId = headers.StreamId,
                    Code = ErrorCode.StreamClosed,
                    Message = "Refusing HEADERS which don't open a new stream",
                };
            }

            lock (shared.Mutex)
            {
                shared.LastIncomingStreamId = headers.StreamId;

                if (shared.GoAwaySent)
                {
                    return new Http2Error
                    {
                        StreamId = headers.StreamId,
                        Code = ErrorCode.RefusedStream,
                        Message = "Going Away",
                    };
                }

                // Check max concurrent streams
                if (shared.streamMap.Count + 1 > localSettings.MaxConcurrentStreams)
                {
                    // Return an error which will trigger a reset frame for
                    // the new stream
                    return new Http2Error
                    {
                        StreamId = headers.StreamId,
                        Code = ErrorCode.RefusedStream,
                        Message = "Refusing stream due to max concurrent streams",
                    };
                }
            }

            if (logger != null && logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace("Accepted new stream with ID {0}", headers.StreamId);
            }

            // Create a new stream for that ID
            var newStream = new StreamImpl(
                this, headers.StreamId, StreamState.Idle,
                (int)localSettings.InitialWindowSize);

            // Add the stream to our map
            // The map might have changed between the check in this
            // But it only can shrink - because we add streams here and only
            // remove them from other tasks.
            lock (shared.Mutex)
            {
                shared.streamMap[headers.StreamId] = newStream;
            }

            // Register that stream at the writer
            if (!writer.RegisterStream(headers.StreamId, (int)remoteSettings.InitialWindowSize))
            {
                // We can't register the stream at the writer
                // This can happen if the writer is already closed
                // Return a connection error, since we can't proceed that way.
                // The stream will get properly reset, since it's registered in
                // the streamMap
                return new Http2Error
                {
                    StreamId = 0,
                    Code = ErrorCode.InternalError,
                    Message = "Can't register stream at writer",
                };
            }

            // Let the new stream process the headers
            // This will move it from Idle state to Open
            var err = newStream.ProcessHeaders(headers);
            if (err != null)
            {
                // Return the error - This will either reset the stream or
                // the connection. No need to pass that dead stream up to
                // the user
                return err;
            }

            // Apply the priority settings if received
            // TODO: This might also be moved into StreamImpl.ProcessHeaders
            if (headers.Priority.HasValue)
            {
                err = HandlePriorityData(
                    headers.StreamId,
                    headers.Priority.Value);
                if (err != null)
                {
                    return err;
                }
            }

            var handledByUser = config.StreamListener(newStream);
            if (!handledByUser)
            {
                // The user isn't interested in the stream.
                // Therefore we reset it
                await newStream.Reset(ErrorCode.RefusedStream, false);
            }

            return null;
        }

        private async ValueTask<Http2Error?> HandleDataFrame(FrameHeader fh)
        {
            if (fh.StreamId == 0)
            {
                return new Http2Error
                {
                    StreamId = 0,
                    Code = ErrorCode.ProtocolError,
                    Message = "Received invalid DATA frame header",
                };
            }
            if ((fh.Flags & (byte)DataFrameFlags.Padded) != 0 &&
                fh.Length < 1)
            {
                // Padded frames must have at least 1 byte
                return new Http2Error
                {
                    StreamId = 0,
                    Code = ErrorCode.ProtocolError,
                    Message = "Frame is too small to contain padding",
                };
            }
            if (fh.Length > localSettings.MaxFrameSize)
            {
                return new Http2Error
                {
                    StreamId = 0,
                    Code = ErrorCode.FrameSizeError,
                    Message = "Maximum frame size exceeded",
                };
            }

            StreamImpl stream = null;
            uint lastIncomingStreamId;
            uint lastOutgoingStreamId;
            lock (shared.Mutex)
            {
                lastIncomingStreamId = shared.LastIncomingStreamId;
                lastOutgoingStreamId = shared.LastOutgoingStreamId;
                shared.streamMap.TryGetValue(fh.StreamId, out stream);
            }

            Http2Error? processError = null;
            int realDataSize = fh.Length;

            if (stream != null)
            {
                //Delegate processing of the DATA frame to the stream
                var procResult = await stream.ProcessData(fh, inputStream, receiveBuffer);
                processError = procResult.Error;
                realDataSize = procResult.DataSize;
            }
            else
            {
                // The stream for which we received data does not exist :O
                // Maybe because we have reset it.
                // In this case we still need to read the data.
                // The stream might also have never been established at all.
                // This can't be exactly checked, since it's not tracked which
                // streams were alive.

                // Consume the data by reading it into our receiveBuffer
                await inputStream.ReadAll(
                    new ArraySegment<byte>(receiveBuffer, 0, fh.Length));

                // Check the most necessary things, e.g. if size is valid wrt padding
                // and what is the real data size (necessary for window update).
                if ((fh.Flags & (byte)DataFrameFlags.Padded) != 0)
                {
                    // Access is safe. Length 1 is checked before
                    var padLen = receiveBuffer[0];
                    realDataSize = fh.Length - 1 - padLen;
                    if (realDataSize < 0)
                    {
                        return new Http2Error
                        {
                            StreamId = 0,
                            Code = ErrorCode.ProtocolError,
                            Message = "Frame is too small after substracting padding",
                        };
                    }
                }

                // Issue a stream error with error type StreamClosed if the
                // streamId could be a valid one.
                // Otherwise use a connection error.
                var isIdleStreamId = IsIdleStreamId(
                    fh.StreamId, lastOutgoingStreamId, lastIncomingStreamId);
                processError = new Http2Error
                {
                    StreamId = isIdleStreamId ? 0u : fh.StreamId,
                    Code = ErrorCode.StreamClosed,
                    Message = "Received DATA for an unknown frame",
                };
            }

            // Check if we should send a window update for the connection
            // If we have encountered a connection error we will close anyway
            // and sending the update is not relevant
            if (processError.HasValue && processError.Value.StreamId == 0)
            {
                return processError;
            }

            // Check if the data frame exceeds the flow control window for the
            // connection.
            // This yields a possible connection error, therefore this would
            // overwrite an already happened stream error (that is irrelevant if
            // the connection gets closed).
            // This check executes AFTER the data has already been read.
            // However that most likely won't hurt as, as the critical checks
            // are for violations of the maximum frame size and the stream window.
            if (realDataSize > connReceiveFlowWindow)
            {
                return new Http2Error
                {
                    StreamId = 0,
                    Code = ErrorCode.FlowControlError,
                    Message = "Received window exceeded",
                };
            }
            // Decrement the flow control window of the connection
            connReceiveFlowWindow -= realDataSize;
            if (logger != null && logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace(
                    "Incoming flow control window update:\n" +
                    "  Connection window: {0} -> {1}\n",
                    connReceiveFlowWindow + realDataSize,
                    connReceiveFlowWindow);
            }

            var maxWindow = Constants.InitialConnectionWindowSize;
            var possibleWindowUpdate = maxWindow - connReceiveFlowWindow;
            var windowUpdateAmount = 0;
            if (possibleWindowUpdate >= (maxWindow/2))
            {
                windowUpdateAmount = possibleWindowUpdate;
                connReceiveFlowWindow += windowUpdateAmount;
                if (logger != null && logger.IsEnabled(LogLevel.Trace))
                {
                    logger.LogTrace(
                        "Incoming flow control window update:\n" +
                        "  Connection window: {0} -> {1}\n",
                        connReceiveFlowWindow - windowUpdateAmount,
                        connReceiveFlowWindow);
                }
            }

            if (windowUpdateAmount > 0)
            {
                // Send the window update frame for the connection
                var wfh = new FrameHeader {
                    StreamId = 0,
                    Type = FrameType.WindowUpdate,
                    Flags = 0,
                };

                var updateData = new WindowUpdateData
                {
                    WindowSizeIncrement = windowUpdateAmount,
                };

                try
                {
                    await writer.WriteWindowUpdate(wfh, updateData);
                }
                catch (Exception)
                {
                    // We ignore errors on sending window updates since they are
                    // not important to the reading process
                    // If the writer encounters an error it will close the connection,
                    // and we will observe that with the next read failing.
                }
            }

            return processError;
        }

        private ValueTask<Http2Error?> HandlePushPromiseFrame(FrameHeader fh)
        {
            // Push promises are not yet supported.
            // We are sending EnablePush = false to the remote
            // If we still receive a PUSH_PROMISE frame this is an error.
            return new ValueTask<Http2Error?>(
                new Http2Error
                {
                    StreamId = 0,
                    Code = ErrorCode.ProtocolError,
                    Message = "Received unsupported PUSH_PROMISE frame",
                });
        }

        /// <summary>
        /// Handles frames that are not known to this HTTP/2 implementation.
        /// From the specification:
        /// Implementations MUST ignore and discard any frame that has a type
        /// that is unknown.
        /// </summary>
        private async ValueTask<Http2Error?> HandleUnknownFrame(FrameHeader fh)
        {
            // Check frame size
            if (fh.Length > localSettings.MaxFrameSize)
            {
                return new Http2Error
                {
                    StreamId = 0,
                    Code = ErrorCode.FrameSizeError,
                    Message = "Maximum frame size exceeded",
                };
            }

            // Read data from the unknown frame into the receive buffer
            await inputStream.ReadAll(
                new ArraySegment<byte>(receiveBuffer, 0, fh.Length));

            // And discard it

            return null;
        }

        private async ValueTask<Http2Error?> HandleGoAwayFrame(FrameHeader fh)
        {
            if (fh.StreamId != 0 || fh.Length < 8)
            {
                return new Http2Error
                {
                    StreamId = 0,
                    Code = ErrorCode.ProtocolError,
                    Message = "Received invalid GOAWAY frame header",
                };
            }
            if (fh.Length > localSettings.MaxFrameSize)
            {
                return new Http2Error
                {
                    StreamId = 0,
                    Code = ErrorCode.FrameSizeError,
                    Message = "Maximum frame size exceeded",
                };
            }

            // Read data
            await inputStream.ReadAll(new ArraySegment<byte>(receiveBuffer, 0, fh.Length));

            // Deserialize it
            var goAwayData = GoAwayFrameData.DecodeFrom(
                new ArraySegment<byte>(receiveBuffer, 0, fh.Length));

            // Pass it to the GoAway task
            // If we receive multiple GoAways we will only pass the first reason
            if (!remoteGoAwayTcs.Task.IsCompleted)
            {
                // Copy the debug data, since this is not valid
                // outside of the scope (point to receive buffer)
                var reason = goAwayData.Reason;
                var data = reason.DebugData;
                var debugData = new byte[data.Count];
                Array.Copy(data.Array, data.Offset, debugData, 0, data.Count);
                reason.DebugData = new ArraySegment<byte>(debugData);
                // Complete task
                remoteGoAwayTcs.TrySetResult(reason);
            }

            return null;
        }

        private async ValueTask<Http2Error?> HandleResetFrame(FrameHeader fh)
        {
            if (fh.StreamId == 0 || fh.Length != ResetFrameData.Size)
            {
                var errc = ErrorCode.ProtocolError;
                if (fh.Length != ResetFrameData.Size) errc = ErrorCode.FrameSizeError;
                return new Http2Error
                {
                    StreamId = 0,
                    Code = errc,
                    Message = "Received invalid RST_STREAM frame header",
                };
            }

            // Read data
            await inputStream.ReadAll(
                new ArraySegment<byte>(receiveBuffer, 0, ResetFrameData.Size));

            // Deserialize it
            var resetData = ResetFrameData.DecodeFrom(
                new ArraySegment<byte>(receiveBuffer, 0, ResetFrameData.Size));

            // Handle the reset
            StreamImpl stream = null;
            uint lastOutgoingStream = 0u;
            uint lastIncomingStream = 0u;
            lock (shared.Mutex)
            {
                lastIncomingStream = shared.LastIncomingStreamId;
                lastOutgoingStream = shared.LastOutgoingStreamId;
                shared.streamMap.TryGetValue(fh.StreamId, out stream);
                if (stream != null)
                {
                    shared.streamMap.Remove(fh.StreamId);
                }
            }

            if (stream != null)
            {
                await stream.Reset(resetData.ErrorCode, true);
            }
            else
            {
                // Check if the reset frame was sent on an IDLE stream
                // In this case stream will always be null, as IDLE streams cant
                // be in the map
                if (IsIdleStreamId(fh.StreamId, lastOutgoingStream, lastIncomingStream))
                {
                    return new Http2Error
                    {
                        StreamId = 0u,
                        Code = ErrorCode.ProtocolError,
                        Message = "Received RST_STREAM for an IDLE stream",
                    };
                }
            }

            return null;
        }

        private bool IsIdleStreamId(
            uint streamId, uint lastOutgoingStreamId, uint lastIncomingStreamId)
        {
            var isServerInitiated = streamId % 2 == 0;
            var isRemoteInitiated =
                (IsServer && !isServerInitiated) ||
                (!IsServer && isServerInitiated);
            var isIdle = (isRemoteInitiated && streamId > lastIncomingStreamId ||
                          !isRemoteInitiated && streamId > lastOutgoingStreamId);
            return isIdle;
        }

        private async ValueTask<Http2Error?> HandleWindowUpdateFrame(FrameHeader fh)
        {
            if (fh.Length != WindowUpdateData.Size)
            {
                return new Http2Error
                {
                    StreamId = 0,
                    Code = ErrorCode.FrameSizeError,
                    Message = "Received invalid WINDOW_UPDATE frame header",
                };
            }

            // Read data
            await inputStream.ReadAll(
                new ArraySegment<byte>(receiveBuffer, 0, WindowUpdateData.Size));

            // Deserialize it
            var windowUpdateData = WindowUpdateData.DecodeFrom(
                new ArraySegment<byte>(receiveBuffer, 0, WindowUpdateData.Size));

            // Check if the window update is sent on an idle stream
            bool isIdleStream = false;
            lock (shared.Mutex)
            {
                isIdleStream = IsIdleStreamId(
                    fh.StreamId, shared.LastOutgoingStreamId, shared.LastIncomingStreamId);
            }

            // If we receive a window update for an idle stream it's a connection
            // error. This is mainly here to satisfy the h2spec test suite.
            // The protocol would work fine without it by ignoring the update.
            if (isIdleStream)
            {
                return new Http2Error
                {
                    StreamId = 0u,
                    Code = ErrorCode.ProtocolError,
                    Message = "Received window update for Idle stream",
                };
            }

            // Handle it - 0 size increments will be handled by the writer
            return writer.UpdateFlowControlWindow(
                fh.StreamId, windowUpdateData.WindowSizeIncrement);
        }

        private async ValueTask<Http2Error?> HandlePingFrame(FrameHeader fh)
        {
            if (fh.StreamId != 0 || fh.Length != 8)
            {
                var errc = ErrorCode.ProtocolError;
                if (fh.Length != 8) errc = ErrorCode.FrameSizeError;
                return new Http2Error
                {
                    StreamId = 0,
                    Code = errc,
                    Message = "Received invalid PING frame header",
                };
            }

            // Read ping data
            await inputStream.ReadAll(new ArraySegment<byte>(receiveBuffer, 0, 8));

            var hasAck = (fh.Flags & (byte)PingFrameFlags.Ack) != 0;
            if (hasAck)
            {
                // Lookup in our ping map if we have sent this ping
                TaskCompletionSource<bool> tcs = null;
                lock (shared.Mutex)
                {
                    if (shared.PingState != null)
                    {
                        var id = BitConverter.ToUInt64(receiveBuffer, 0);
                        if (shared.PingState.PingMap.TryGetValue(id, out tcs))
                        {
                            // We have sent a ping with that ID
                            shared.PingState.PingMap.Remove(id);
                        }
                    }
                }
                if (tcs != null)
                {
                    tcs.SetResult(true);
                }
            }
            else
            {
                // Respond to the ping
                var pongHeader = fh;
                pongHeader.Flags = (byte)PingFrameFlags.Ack;
                await writer.WritePing(
                    pongHeader, new ArraySegment<byte>(receiveBuffer, 0, 8));
            }

            return null;
        }

        private async ValueTask<Http2Error?> HandlePriorityFrame(FrameHeader fh)
        {
            if (fh.StreamId == 0 || fh.Length != PriorityData.Size)
            {
                var errc = ErrorCode.ProtocolError;
                if (fh.Length != PriorityData.Size) errc = ErrorCode.FrameSizeError;
                return new Http2Error
                {
                    StreamId = 0,
                    Code = errc,
                    Message = "Received invalid PRIORITY frame header",
                };
            }

            // Read frame data
            await inputStream.ReadAll(
                new ArraySegment<byte>(receiveBuffer, 0, PriorityData.Size));

            // Decode the priority data
            // We don't reuse the same ArraySegment to avoid capturing it in the closure
            var prioData = PriorityData.DecodeFrom(
                new ArraySegment<byte>(receiveBuffer, 0, PriorityData.Size));

            return HandlePriorityData(fh.StreamId, prioData);
        }

        private Http2Error? HandlePriorityData(
            uint streamId, PriorityData data)
        {
            // Check if stream depends on itself
            if (streamId == data.StreamDependency)
            {
                return new Http2Error
                {
                    Code = ErrorCode.ProtocolError,
                    StreamId = streamId,
                    Message = "Priority Error: A stream can not depend on itself",
                };
            }

            return null;
        }

        private async ValueTask<Http2Error?> HandleSettingsFrame(FrameHeader fh)
        {
            if (fh.StreamId != 0)
            {
                // SETTINGS frames must use StreamId 0
                return new Http2Error
                {
                    StreamId = 0,
                    Code = ErrorCode.ProtocolError,
                    Message = "Received SETTINGS frame with invalid stream ID",
                };
            }
            bool isAck = (fh.Flags & (byte)SettingsFrameFlags.Ack) != 0;

            if (isAck)
            {
                if (fh.Length != 0)
                {
                    return new Http2Error
                    {
                        StreamId = 0,
                        Code = ErrorCode.ProtocolError,
                        Message = "Received SETTINGS ACK with non-zero length",
                    };
                }
                // TODO: Stop potential timer that waits for SETTINGS ACKs
                // Might need to protect nrUnackedSettings with a mutex
                nrUnackedSettings--;
                if (nrUnackedSettings < 0)
                {
                    return new Http2Error
                    {
                        StreamId = 0,
                        Code = ErrorCode.ProtocolError,
                        Message = "Received unexpected SETTINGS ACK",
                    };
                }
            }
            else
            {
                // Received SETTINGS from the remote side
                // Validate frame length before reading the body
                if (fh.Length > localSettings.MaxFrameSize)
                {
                    return new Http2Error
                    {
                        StreamId = 0,
                        Code = ErrorCode.FrameSizeError,
                        Message = "Maximum frame size exceeded",
                    };
                }
                if (fh.Length % 6 != 0)
                {
                    return new Http2Error
                    {
                        StreamId = 0,
                        Code = ErrorCode.ProtocolError,
                        Message = "Invalid SETTINGS frame length",
                    };
                }

                // Receive the body of the SETTINGs frame
                await inputStream.ReadAll(
                    new ArraySegment<byte>(receiveBuffer, 0, fh.Length));

                // Save the old initial window size for streams
                // The maximum transitions are from int.MaxValue to 0
                // and the other way around.
                // This means the max delta is +/- int.MaxValue, which fits in int
                var oldInitialStreamWindowSize = (int)remoteSettings.InitialWindowSize;

                // Update the remote settings from that data
                // This will also validate the settings
                var err = remoteSettings.UpdateFromData(
                    new ArraySegment<byte>(receiveBuffer, 0, fh.Length));
                if (err != null)
                {
                    return err;
                }

                // Calculate the delta in window size
                // The output stream windows need to be adjusted by this value.
                var deltaInitialStreamWindowSize =
                    (int)remoteSettings.InitialWindowSize - oldInitialStreamWindowSize;

                // Set the settings received flag
                settingsReceived = true;

                // Update the writer with new values for the remote settings and
                // send an acknowledge.
                // As with the current UpdateSettings API we don't see what has
                // changed we need to overwrite everything.
                err = await writer.ApplyAndAckRemoteSettings(
                    remoteSettings, deltaInitialStreamWindowSize);
                if (err != null)
                {
                    return err;
                }
            }

            return null;
        }

        /// <summary>
        /// Unregisters a stream from the map of streams that are managed
        /// by this connection.
        /// </summary>
        /// <param name="stream">The stream to unregister</param>
        internal void UnregisterStream(StreamImpl stream)
        {
            lock (shared.Mutex)
            {
                if (shared.streamMap != null)
                {
                    shared.streamMap.Remove(stream.Id);
                }
            }
        }
    }

    /// <summary>
    /// Signals that the connection was closed
    /// </summary>
    public class ConnectionClosedException : Exception
    {
    }
}