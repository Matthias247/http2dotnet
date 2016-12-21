using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Hpack;

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
            public IStreamReader InputStream;

            /// <summary>
            /// The stream which is used for writing data
            /// </summary>
            public IStreamWriterCloser OutputStream;

            /// <summary>
            /// Whether the connection represents the client or server part of
            /// a HTTP/2 connection. True for servers.
            /// </summary>
            public bool IsServer;

            /// <summary>
            /// The function that should be called whenever a new stream is
            /// opened by the remote peer.
            /// </summary>
            public Action<Object> StreamListener;

            /// <summary>
            /// Strategy for applying huffman encoding on outgoing headers
            /// </summary>
            public HuffmanStrategy? HuffmanStrategy;

            /// <summary>
            /// Allows to override settings for the connection
            /// </summary>
            public Settings Settings;
        }

        internal struct SharedData
        {
            public object Mutex;
            public Dictionary<uint, StreamImpl> streamMap;
        }

        internal SharedData shared;
        byte[] receiveBuffer;
        /// <summary>Whether the initial settings have been received from the remote</summary>
        bool settingsReceived = false;
        int nrUnackedSettings = 0;
        /// <summary>Flow control window for the connection</summary>
        private int connReceiveFlowWindow = Constants.InitialConnectionWindowSize;
        TaskCompletionSource<object> readerFinished = new TaskCompletionSource<object>();
        private Action<Object> StreamListener;

        internal ConnectionWriter Writer;
        internal IStreamReader InputStream;
        internal HeaderReader HeaderReader;
        internal Settings LocalSettings;
        internal Settings RemoteSettings = Settings.Default;

        /// <summary>
        /// Whether the connection represents the client or server part of
        /// a HTTP/2 connection. True for servers.
        /// </summary>
        public readonly bool IsServer;

        /// <summary>
        /// Creates a new HTTP/2 connection on top of the a bidirectional stream
        /// </summary>
        public Connection(Options options)
        {
            IsServer = options.IsServer;

            LocalSettings = options.Settings; // TODO: Validate
            // Disable server push as it's not supported.
            // Disabling it here is easier than wanting a custom config from the
            // user which disables it.
            LocalSettings.EnablePush = false;
            // TODO: If the remote settings are not the default ones we will
            // also need to validate those

            if (options.InputStream == null) throw new ArgumentNullException(nameof(options.InputStream));
            if (options.OutputStream == null) throw new ArgumentNullException(nameof(options.OutputStream));
            if (options.IsServer && options.StreamListener == null)
                throw new ArgumentNullException(nameof(options.StreamListener));
            StreamListener = options.StreamListener;

            this.InputStream = options.InputStream;
            receiveBuffer = new byte[LocalSettings.MaxFrameSize + FrameHeader.HeaderSize];

            // Initialize shared data
            shared.Mutex = new object();
            shared.streamMap = new Dictionary<uint, StreamImpl>();

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

            HeaderReader = new HeaderReader(
                new Hpack.Decoder(new Hpack.Decoder.Options
                {
                    // Use the default options here as long as this is not configurable
                    DynamicTableSizeLimit = (int)LocalSettings.HeaderTableSize,
                }),
                (int)LocalSettings.MaxFrameSize,
                (int)LocalSettings.MaxHeaderListSize,
                receiveBuffer,
                options.InputStream
            );

            // Start the task that performs the actual reading
            Task.Run(() => this.RunReaderAsync());
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
                    await ClientPreface.ReadAsync(InputStream);
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
                            // The error is a connection error
                            // TODO: Handle connection error
                            // TODO: Write a suitable GOAWAY frame and stop the writer
                            // This should be an urgent GOAWAY thingy
                            // And stop to read
                            continueRead = false;
                        }
                        else
                        {
                            // The error is a stream error
                            // Check out if we know a stream with the given ID
                            StreamImpl stream = null;
                            lock (shared.Mutex)
                            {
                                stream = shared.streamMap[err.Value.StreamId];
                            }

                            if (stream != null)
                            {
                                // The stream is known
                                // Reset the stream locally, which will also
                                // enqueue a RST_STREAM frame and remove it from
                                // the map.
                                // TODO: This won't wait until the reset is done
                                stream.Reset(err.Value.Code, false);
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
                readerFinished.SetException(e);
            }

            // This will only succeed if we don't land in the catch block before
            // It's just more readable to put this here instead of at the end
            // of the try block
            readerFinished.TrySetResult(null);
        }

        private async ValueTask<Http2Error?> ReadOneFrame()
        {
            var fh = await FrameHeader.ReceiveAsync(InputStream, receiveBuffer);

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
                    return new Http2Error
                    {
                        StreamId = 0,
                        Code = ErrorCode.ProtocolError,
                        Message = "Unexpected frame type",
                    };
            }
        }

        private async ValueTask<Http2Error?> HandleHeaders(CompleteHeadersFrameData headers)
        {
            StreamImpl stream = null;
            lock (shared.Mutex)
            {
                stream = shared.streamMap[headers.StreamId];
            }

            if (stream != null)
            {
                //Delegate processing of the HEADERS frame to the stream
                return stream.ProcessHeaders(headers);
            }
            else
            {
                // This might be a new stream - or a protocol error
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
            if (fh.Length > connReceiveFlowWindow)
            {
                return new Http2Error
                {
                    StreamId = 0,
                    Code = ErrorCode.FlowControlError,
                    Message = "Received window exceeded",
                };
            }
            // Decrement the flow control window
            connReceiveFlowWindow -= fh.Length;

            StreamImpl stream = null;
            lock (shared.Mutex)
            {
                stream = shared.streamMap[fh.StreamId];
            }

            if (stream != null)
            {
                //Delegate processing of the DATA frame to the stream
                var err = await stream.ProcessData(fh, InputStream, receiveBuffer);
                if (err != null) return err;
            }
            else
            {
                // The stream for which we received data does not exist :O
                // Maybe because we have reset it.
                // In this case we still need to read the data.
                // It might also have never been established at all.
                // But we can only roughly check that
                // TODO: Check if the StreamId is bigger then the maximum
                // received or sent one

                // Consume the data by reading it into our receiveBuffer
                await InputStream.ReadAll(
                    new ArraySegment<byte>(receiveBuffer, 0, fh.Length));
            }

            // Check if we should sent a window update for the connection
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

            return null;
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
                stream = shared.streamMap[fh.StreamId];
                if (stream != null)
                {
                    shared.streamMap.Remove(fh.StreamId);
                }
            }

            if (stream != null)
            {
                stream.Reset(resetData.ErrorCode, true);
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
            if (fh.StreamId == 0 || fh.Length != 8)
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
                Writer.UpdateMaximumFrameSize((int)RemoteSettings.MaxFrameSize);
                Writer.UpdateMaximumHeaderTableSize((int)RemoteSettings.HeaderTableSize);
                // RemoteSettings.MaxHeaderListSize is currently not used

                // Set the settings received flag
                settingsReceived = true;
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
                shared.streamMap.Remove(stream.Id);
            }
        }
    }
}