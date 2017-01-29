using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

using Http2.Hpack;

namespace Http2
{
    /// <summary>
    /// A HTTP/2 connection
    /// </summary>
    public class Connection
    {
        /// <summary>
        /// Options for creating a HTTP/2 connection
        /// </summary>
        public struct Options
        {
            /// <summary>
            /// The stream which is used for receiving data
            /// </summary>
            public IReadableByteStream InputStream;

            /// <summary>
            /// The stream which is used for writing data
            /// </summary>
            public IWriteAndCloseableByteStream OutputStream;

            /// <summary>
            /// Whether the connection represents the client or server part of
            /// a HTTP/2 connection. True for servers.
            /// </summary>
            public bool IsServer;

            /// <summary>
            /// The function that should be called whenever a new stream is
            /// opened by the remote peer.
            /// The function should return true if it wants to handle the new
            /// stream and false otherwise.
            /// Applications should handle the stream in another Task.
            /// The Task from which this function is called may not be blocked.
            /// </summary>
            public Func<IStream, bool> StreamListener;

            /// <summary>
            /// Strategy for applying huffman encoding on outgoing headers
            /// </summary>
            public HuffmanStrategy? HuffmanStrategy;

            /// <summary>
            /// Allows to override settings for the connection
            /// </summary>
            public Settings Settings;

            /// <summary>
            /// Optional logger
            /// </summary>
            public ILogger Logger;
        }

        internal struct SharedData
        {
            public object Mutex;
            public Dictionary<uint, StreamImpl> streamMap;
            /// The last and maximum stream ID that was received from the remote.
            /// 0 means we never received anything
            public uint LastIncomingStreamId;
            /// Whether a GoAway message has been sent
            public bool GoAwaySent;
        }

        internal SharedData shared;
        byte[] receiveBuffer;
        /// <summary>Whether the initial settings have been received from the remote</summary>
        bool settingsReceived = false;
        int nrUnackedSettings = 0;
        /// <summary>Flow control window for the connection</summary>
        private int connReceiveFlowWindow = Constants.InitialConnectionWindowSize;

        /// The last and maximum stream ID that was sent to the remote.
        /// 0 means we never sent anything
        uint lastOutgoingStreamId = 0;

        private readonly Func<IStream, bool> StreamListener;
        internal readonly ILogger logger;

        internal readonly ConnectionWriter Writer;
        internal readonly IReadableByteStream InputStream;
        private readonly Task readerDone;

        private readonly HeaderReader HeaderReader;
        internal readonly Settings LocalSettings;
        internal Settings RemoteSettings = Settings.Default;
        
        /// <summary>
        /// The time time wait for a Client Preface on startup
        /// at server side.
        /// </summary>
        private static readonly int ClientPrefaceTimeout = 1000;

        /// <summary>
        /// Whether the connection represents the client or server part of
        /// a HTTP/2 connection. True for servers.
        /// </summary>
        public readonly bool IsServer;

        /// <summary>
        /// Returns a Task that will be completed once the Connection has been
        /// fully closed.
        /// </summary>
        public Task Done => readerDone;

        /// <summary>
        /// Creates a new HTTP/2 connection on top of the a bidirectional stream
        /// </summary>
        public Connection(Options options)
        {
            IsServer = options.IsServer;
            logger = options.Logger;

            if (!options.Settings.Valid) throw new ArgumentException(nameof(options.Settings));
            LocalSettings = options.Settings;
            // Disable server push as it's not supported.
            // Disabling it here is easier than wanting a custom config from the
            // user which disables it.
            LocalSettings.EnablePush = false;
            // TODO: If the remote settings are not the default ones we will
            // also need to validate those

            if (options.InputStream == null) throw new ArgumentNullException(nameof(options.InputStream));
            if (options.OutputStream == null) throw new ArgumentNullException(nameof(options.OutputStream));
            this.InputStream = options.InputStream;

            if (options.IsServer && options.StreamListener == null)
                throw new ArgumentNullException(nameof(options.StreamListener));
            StreamListener = options.StreamListener;

            receiveBuffer = new byte[LocalSettings.MaxFrameSize + FrameHeader.HeaderSize];

            // Initialize shared data
            shared.Mutex = new object();
            shared.streamMap = new Dictionary<uint, StreamImpl>();
            shared.LastIncomingStreamId = 0u;
            shared.GoAwaySent = false;

            // Start the writing task
            Writer = new ConnectionWriter(
                this, options.OutputStream,
                new ConnectionWriter.Options
                {
                    MaxFrameSize = (int)RemoteSettings.MaxFrameSize,
                    MaxHeaderListSize = (int)RemoteSettings.MaxHeaderListSize,
                },
                new Hpack.Encoder.Options
                {
                    DynamicTableSize = (int)RemoteSettings.HeaderTableSize,
                    HuffmanStrategy = options.HuffmanStrategy,
                }
            );

            // Clamp the dynamic table size limit to int range
            var dynTableSizeLimit = Math.Min(LocalSettings.HeaderTableSize, int.MaxValue);

            HeaderReader = new HeaderReader(
                new Hpack.Decoder(new Hpack.Decoder.Options
                {
                    // Remark: The dynamic table size is set to the default
                    // value of 4096 if not configured here.
                    // Configuring it and setting it to something different
                    // makes no sense, as the remote expects the default size
                    // at start
                    DynamicTableSizeLimit = (int)dynTableSizeLimit,
                }),
                LocalSettings.MaxFrameSize,
                LocalSettings.MaxHeaderListSize,
                receiveBuffer,
                options.InputStream,
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
                    receiveBuffer, 0, LocalSettings.RequiredSize);
                LocalSettings.EncodeInto(encodedSettingsBuf);
                await this.Writer.WriteSettings(new FrameHeader{
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
                    // TODO: Make the timeout configureable
                    await ClientPreface.ReadAsync(InputStream, ClientPrefaceTimeout);
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
                                await Writer.WriteResetStream(fh, resetData);
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

            // Shutdown the writer
            // If we get here through a read exception there's no need for GOAWAY
            // If we get here through GoAwayAndClose we double close
            // TODO: Review that
            await Writer.CloseNow();

            // Wait until the Writer has closed
            await Writer.Done;

            // Cleanup all streams that are still open
            Dictionary<uint, StreamImpl> activeStreams = null;
            lock (shared.Mutex)
            {
                // Move the streamMap outside of the mutex. As it is no longer
                // accessed after cleanup besides the UnregisterStream stream
                // function which is guarded this is safe
                activeStreams = shared.streamMap;
                shared.streamMap = null;
            }
            foreach (var kvp in activeStreams)
            {
                // fromRemote is set to true since there's no need to send
                // a Reset frame and the stream will get removed from the
                // map here
                await kvp.Value.Reset(ErrorCode.Cancel, fromRemote: true);
            }

            if (logger != null && logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace("Connection closed");
            }

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
            await Writer.CloseNow();

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
        /// Internal version of the GoAway function. This will not wait for the
        /// connection to close in order not to deadlock when it's u sed from
        /// inside the Reader routine.
        /// </summary>
        private async Task InitiateGoAway(ErrorCode errorCode, bool closeWriter)
        {
            uint lastProcessedStreamId = 0u;
            lock (shared.Mutex)
            {
                lastProcessedStreamId = shared.LastIncomingStreamId;
                shared.GoAwaySent = true;
                // TODO: Should we check here whether GoAwaySent was already true
                // However in the case where it was sent and and the connection
                // close was not initiated we still have to to this
            }

            var fh = new FrameHeader
            {
                Type = FrameType.GoAway,
                StreamId = 0,
                Flags = 0,
            };

            var goAwayData = new GoAwayFrameData
            {
                LastStreamId = lastProcessedStreamId,
                ErrorCode = errorCode,
                DebugData = Constants.EmptyByteArray,
            };

            await Writer.WriteGoAway(fh, goAwayData, closeWriter);
        }

        private async ValueTask<Http2Error?> ReadOneFrame()
        {
            var fh = await FrameHeader.ReceiveAsync(InputStream, receiveBuffer);
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
                    var headerRes = await HeaderReader.ReadHeaders(fh);
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
            uint lastStreamId = 0;
            lock (shared.Mutex)
            {
                lastStreamId = shared.LastIncomingStreamId;
                shared.streamMap.TryGetValue(headers.StreamId, out stream);
            }

            if (stream != null)
            {
                //Delegate processing of the HEADERS frame to the existing stream
                return stream.ProcessHeaders(headers);
            }

            // This might be a new stream - or a protocol error
            var isServerInitiated = headers.StreamId % 2 == 0;
            var isRemoteInitiated =
                (IsServer && !isServerInitiated) || (!IsServer && isServerInitiated);

            var isValidNewStream =
                IsServer && // As a client don't accept HEADERS as a way to create a new stream
                isRemoteInitiated &&
                (headers.StreamId > lastStreamId);

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
                    Code = ErrorCode.RefusedStream,
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
                if (shared.streamMap.Count + 1 > LocalSettings.MaxConcurrentStreams)
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
                (int)LocalSettings.InitialWindowSize);

            // Add the stream to our map
            // The map might have changed between the check in this
            // But it only can shrink - because we add streams here and only
            // remove them from other tasks.
            lock (shared.Mutex)
            {
                shared.streamMap[headers.StreamId] = newStream;
            }

            // Register that stream at the writer
            if (!Writer.RegisterStream(headers.StreamId, (int)RemoteSettings.InitialWindowSize))
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

            var handledByUser = StreamListener(newStream);
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
            if (fh.Length > LocalSettings.MaxFrameSize)
            {
                return new Http2Error
                {
                    StreamId = 0,
                    Code = ErrorCode.FrameSizeError,
                    Message = "Maximum frame size exceeded",
                };
            }

            // Check if the data frame exceeds the flow control window for the
            // connection
            // TODO: This is wrong. We need to check for the size without PADDING!
            // However we don't know the padding here yet.
            if (fh.Length > connReceiveFlowWindow)
            {
                return new Http2Error
                {
                    StreamId = 0,
                    Code = ErrorCode.FlowControlError,
                    Message = "Received window exceeded",
                };
            }
            // Decrement the flow control window of the connection
            connReceiveFlowWindow -= fh.Length;

            StreamImpl stream = null;
            lock (shared.Mutex)
            {
                shared.streamMap.TryGetValue(fh.StreamId, out stream);
            }

            Http2Error? processError = null;

            if (stream != null)
            {
                //Delegate processing of the DATA frame to the stream
                processError = await stream.ProcessData(fh, InputStream, receiveBuffer);
            }
            else
            {
                // The stream for which we received data does not exist :O
                // Maybe because we have reset it.
                // In this case we still need to read the data.
                // It might also have never been established at all.
                // But we can only roughly check that
                // TODO: Check if the StreamId is bigger then the maximum
                // received or sent one - potentially send a reset frame

                // Consume the data by reading it into our receiveBuffer
                await InputStream.ReadAll(
                    new ArraySegment<byte>(receiveBuffer, 0, fh.Length));

                // Check the most necessary things, e.g. if size is valid wrt padding
                if ((fh.Flags & (byte)DataFrameFlags.Padded) != 0)
                {
                    // Access is safe. Length 1 is checked before
                    var padLen = receiveBuffer[0];
                    var contentSize = fh.Length - 1 - padLen;
                    if (contentSize < 0)
                    {
                        return new Http2Error
                        {
                            StreamId = 0,
                            Code = ErrorCode.ProtocolError,
                            Message = "Frame is too small after substracting padding",
                        };
                    }
                }

                // TODO: Send a reset stream in this case?
                // processError = new Http2Error()...
            }

            // Check if we should send a window update for the connection
            // If we have encountered a connection error we will close anyway
            // and sending the update is not relevant
            if (processError.HasValue && processError.Value.StreamId == 0)
            {
                return processError;
            }

            var maxWindow = Constants.InitialConnectionWindowSize;
            var possibleWindowUpdate = maxWindow - connReceiveFlowWindow;
            var windowUpdateAmount = 0;
            if (possibleWindowUpdate >= (maxWindow/2))
            {
                windowUpdateAmount = possibleWindowUpdate;
                connReceiveFlowWindow += windowUpdateAmount;
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
                    await Writer.WriteWindowUpdate(wfh, updateData);
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
            if (fh.Length > LocalSettings.MaxFrameSize)
            {
                return new Http2Error
                {
                    StreamId = 0,
                    Code = ErrorCode.FrameSizeError,
                    Message = "Maximum frame size exceeded",
                };
            }

            // Read data from the unknown frame into the receive buffer
            await InputStream.ReadAll(
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
            if (fh.Length > LocalSettings.MaxFrameSize)
            {
                return new Http2Error
                {
                    StreamId = 0,
                    Code = ErrorCode.FrameSizeError,
                    Message = "Maximum frame size exceeded",
                };
            }

            // Read data
            await InputStream.ReadAll(new ArraySegment<byte>(receiveBuffer, 0, fh.Length));

            // Deserialize it
            var goawayData = GoAwayFrameData.DecodeFrom(
                new ArraySegment<byte>(receiveBuffer, 0, fh.Length));

            // TODO: Handle the GOAWAY
            // Remark: goawayData.DebugData is not valid outside of this scope

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
            await InputStream.ReadAll(new ArraySegment<byte>(receiveBuffer, 0, ResetFrameData.Size));

            // Deserialize it
            var resetData = ResetFrameData.DecodeFrom(
                new ArraySegment<byte>(receiveBuffer, 0, ResetFrameData.Size));

            // Handle the reset
            StreamImpl stream = null;
            lock (shared.Mutex)
            {
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
            return null;
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
            await InputStream.ReadAll(new ArraySegment<byte>(receiveBuffer, 0, WindowUpdateData.Size));

            // Deserialize it
            var windowUpdateData = WindowUpdateData.DecodeFrom(
                new ArraySegment<byte>(receiveBuffer, 0, WindowUpdateData.Size));

            // Handle it - 0 size increments will be handled by the writer
            return Writer.UpdateFlowControlWindow(fh.StreamId, windowUpdateData.WindowSizeIncrement);
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
            await InputStream.ReadAll(new ArraySegment<byte>(receiveBuffer, 0, 8));

            var hasAck = (fh.Flags & (byte)PingFrameFlags.Ack) != 0;
            if (hasAck)
            {
                // Do nothing for the moment. We don't send PINGs
                // Also not worth to treat this as an error
            }
            else
            {
                // Respond to the ping
                var pongHeader = fh;
                pongHeader.Flags = (byte)PingFrameFlags.Ack;
                await Writer.WritePing(
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
            await InputStream.ReadAll(new ArraySegment<byte>(receiveBuffer, 0, PriorityData.Size));

            // Decode the priority data
            // We don't reuse the same ArraySegment to avoid capturing it in the closure
            var prioData = PriorityData.DecodeFrom(
                new ArraySegment<byte>(receiveBuffer, 0, PriorityData.Size));
            // Do nothing with the priority data at the moment

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
                if (fh.Length > LocalSettings.MaxFrameSize)
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
                await InputStream.ReadAll(
                    new ArraySegment<byte>(receiveBuffer, 0, fh.Length));

                // Update the remote settings from that data
                // This will also validate the settings
                var err = RemoteSettings.UpdateFromData(
                    new ArraySegment<byte>(receiveBuffer, 0, fh.Length));
                if (err != null)
                {
                    return err;
                }

                // Update the writer with new values for the remote settings
                // As with the current UpdateSettings API we don't see what has
                // changed we need to overwrite everything.
                Writer.UpdateSettings(RemoteSettings);

                // Set the settings received flag
                settingsReceived = true;

                // Send an acknowledge for the settings
                var ackHeader = new FrameHeader
                {
                    Type = FrameType.Settings,
                    Flags = (byte)SettingsFrameFlags.Ack,
                    Length = 0,
                    StreamId = 0u,
                };
                await Writer.WriteSettings(
                    ackHeader, Constants.EmptyByteArray);
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
}