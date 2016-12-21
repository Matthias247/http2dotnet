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
        bool settingsReceived = false;
        int nrUnackedSettings = 0;
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
                // input buffer for the write task.
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

                if (IsServer)
                {
                    // If this is a server we need to read the preface first
                    await ClientPreface.ReadAsync(InputStream);
                }

                // After the client preface the next thing to do is to write the
                // local settings
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

        private async ValueTask<Http2Error?> ReadOnce()
        {
            var fh = await FrameHeader.ReceiveAsync(InputStream, receiveBuffer);

            // As a server the first thing that we need to receive after the preface
            // is a SETTINGS frame without ACK flag
            if (IsServer && !settingsReceived)
            {
                if (fh.Type != FrameType.Settings || (fh.Flags & (byte)SettingsFrameFlags.Ack) != 0)
                {
                    // TODO: Handle error
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
                        Type = ErrorType.ConnectionError,
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
                        Type = ErrorType.ConnectionError,
                        Code = ErrorCode.ProtocolError,
                        Message = "Unexpected frame type",
                    };
            }
        }

        private async ValueTask<Http2Error?> HandleHeaders(CompleteHeadersFrameData headers)
        {
            return null;
        }

        private async ValueTask<Http2Error?> HandleDataFrame(FrameHeader fh)
        {
            if (fh.StreamId == 0)
            {
                return new Http2Error
                {
                    Type = ErrorType.ConnectionError,
                    Code = ErrorCode.ProtocolError,
                    Message = "Received invalid DATA frame header",
                };
            }
            if (fh.Length > LocalSettings.MaxFrameSize)
            {
                return new Http2Error
                {
                    Type = ErrorType.ConnectionError,
                    Code = ErrorCode.FrameSizeError,
                    Message = "Maximum frame size exceeded",
                };
            }

            StreamImpl stream = null;
            lock (shared.Mutex)
            {
                stream = shared.streamMap[fh.StreamId];
            }

            if (stream != null)
            {
                //Delegate processing of the DATA frame to the stream
                return await stream.ProcessData(fh, InputStream, receiveBuffer);
            }
            else
            {
                // The stream for which we received data does not exist :O
                // TODO: We can have killed it.
                // But it also may have not be established at all.
                // In both cases we still need to read the data.
            }

            // TODO: Send window updates for connection somewhere here
        }

        private async ValueTask<Http2Error?> HandlePushPromiseFrame(FrameHeader fh)
        {
            return new Http2Error
            {
                Type = ErrorType.ConnectionError,
                Code = ErrorCode.ProtocolError,
                Message = "Received unsupported PUSH_PROMISE frame",
            };
        }

        private async ValueTask<Http2Error?> HandleGoAwayFrame(FrameHeader fh)
        {
            if (fh.StreamId != 0 || fh.Length < 8)
            {
                return new Http2Error
                {
                    Type = ErrorType.ConnectionError,
                    Code = ErrorCode.ProtocolError,
                    Message = "Received invalid GOAWAY frame header",
                };
            }
            if (fh.Length > LocalSettings.MaxFrameSize)
            {
                return new Http2Error
                {
                    Type = ErrorType.ConnectionError,
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
                    Type = ErrorType.ConnectionError,
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
                    Type = ErrorType.ConnectionError,
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
                    Type = ErrorType.ConnectionError,
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
                    Type = ErrorType.ConnectionError,
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
                    Type = ErrorType.ConnectionError,
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
                        Type = ErrorType.ConnectionError,
                        Code = ErrorCode.ProtocolError,
                        Message = "Received SETTINGS ACK with non-zero length",
                    };
                }
                // TODO: Stop potential timer that waits for SETTINGS acks
                // Might need to protect nrUnackedSettings with a mutex
                nrUnackedSettings--;
                if (nrUnackedSettings < 0)
                {
                    return new Http2Error
                    {
                        Type = ErrorType.ConnectionError,
                        Code = ErrorCode.ProtocolError,
                        Message = "Received unexpected SETTINGS ACK",
                    };
                }
            }
            else
            {
                // Received SETTINGS from the remote side
                // Read remaining data
                if (fh.Length > LocalSettings.MaxFrameSize)
                {
                    return new Http2Error
                    {
                        Type = ErrorType.ConnectionError,
                        Code = ErrorCode.FrameSizeError,
                        Message = "Maximum frame size exceeded",
                    };
                }
                await InputStream.ReadAll(new ArraySegment<byte>(receiveBuffer, 0, fh.Length));
                // TODO: Extract those settings
                // TODO: Validate those
                // TODO: Update writer values
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

        // TODO: Somewhere we need to send window update frames for the connection
    }
}