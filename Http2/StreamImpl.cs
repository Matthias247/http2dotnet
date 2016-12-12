using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Hpack;
using Http2.Internal;

namespace Http2
{
    /// <summary>
    /// Implementation of a HTTP/2 stream
    /// </summary>
    public class StreamImpl : IStream
    {
        /// <summary>The ID of the stream</summary>
        public uint Id { get; }
        /// <summary>Returns the current state of the stream</summary>
        public StreamState State
        {
            get
            {
                lock (stateMutex)
                {
                    return this.state;
                }
            }
        }

        private Connection connection;
        private StreamState state;
        private object stateMutex = new object();
        private SemaphoreSlim writeMutex = new SemaphoreSlim(1);
        private AsyncManualResetEvent recvPossible = new AsyncManualResetEvent(false);

        private bool headersSent = false;
        private bool dataSent = false;

        private bool headersReceived = false;
        private bool trailersReceived = false;
        private List<HeaderField> inHeaders = new List<HeaderField>();
        private List<HeaderField> inTrailers = new List<HeaderField>();

        private int receiveWindow; // Might be superficial since that info is also in RingBuf size
        private RingBuf recvBuf;

        private static readonly byte[] nullBuffer = new byte[0];

        public StreamImpl(
            Connection connection, uint id, StreamState state,
            int recvBufSize, int receiveWindow)
        {
            this.connection = connection;
            this.Id = id;
            this.state = state;
            this.receiveWindow = receiveWindow;
            this.recvBuf = new RingBuf(recvBufSize);
        }

        private async ValueTask<object> SendHeaders(
            IEnumerable<HeaderField> headers, bool endOfStream)
        {
            var fhh = new FrameHeader {
                StreamId = this.Id,
                Type = FrameType.Headers,
                // EndOfHeaders will be auto-set
                Flags = endOfStream ? (byte)HeadersFrameFlags.EndOfStream : (byte)0,
            };
            var res = await connection.Writer.WriteHeaders(fhh, headers);
            if (res != ConnectionWriter.WriteResult.Success)
            {
                throw new Exception("Can not write to stream"); // TODO: Improve me
            }
            return null;
        }

        public async ValueTask<object> WriteHeaders(IEnumerable<HeaderField> headers, bool endOfStream)
        {
            HeaderValidationResult hvr;
            // TODO: For push promises other validates might need to be used
            if (connection.IsServer) hvr = HeaderValidator.ValidateResponseHeaders(headers);
            else hvr = HeaderValidator.ValidateRequestHeaders(headers);
            if (hvr != HeaderValidationResult.Ok)
            {
                throw new Exception(hvr.ToString());
            }

            var removeStream = false;

            await writeMutex.WaitAsync();
            try
            {
                // Check what already has been sent
                lock (stateMutex)
                {
                    // TODO: Check here the state also?
                    // If we are in a Reset state we might otherwise still try
                    // to send headers
                    // TODO: If we put endofstream we might move into other states
                    if (dataSent)
                    {
                        throw new Exception("Attempted to write headers after data");
                    }

                    if (this.headersSent)
                    {
                        // TODO: Allow for multiple header packets or not?
                        // It seems it is required for informal headers to work
                    }

                    headersSent = true;
                    switch (state)
                    {
                        case StreamState.Idle:
                            state = StreamState.Open;
                            if (endOfStream)
                            {
                                state = StreamState.HalfClosedLocal;
                            }
                            break;
                        case StreamState.ReservedLocal:
                            state = StreamState.HalfClosedRemote;
                            if (endOfStream)
                            {
                                state = StreamState.Closed;
                                removeStream = true;
                            }
                            break;
                    }
                }

                await SendHeaders(headers, endOfStream);
            }
            finally
            {
                writeMutex.Release();
                if (removeStream)
                {
                    connection.RemoveStream(this);
                }
            }

            return null;
        }

        public async ValueTask<object> WriteTrailers(IEnumerable<HeaderField> headers)
        {
            HeaderValidationResult hvr = HeaderValidator.ValidateTrailingHeaders(headers);
            // TODO: For push promises other validates might need to be used
            if (hvr != HeaderValidationResult.Ok)
            {
                throw new Exception(hvr.ToString());
            }

            var removeStream = false;

            await writeMutex.WaitAsync();
            try
            {
                // Check what already has been sent
                lock (stateMutex)
                {
                    // TODO: Check here the state also?
                    // If we are in a Reset state we might otherwise still try
                    // to send headers
                    // TODO: If we put endofstream we might move into other states
                    if (!dataSent)
                    {
                        throw new Exception("Attempted to write trailers without data");
                    }

                    switch (state)
                    {
                        case StreamState.Open:
                            state = StreamState.HalfClosedLocal;
                            break;
                        case StreamState.HalfClosedRemote:
                            state = StreamState.HalfClosedRemote;
                            state = StreamState.Closed;
                            removeStream = true;
                            break;
                        case StreamState.Idle:
                        case StreamState.ReservedRemote:
                        case StreamState.HalfClosedLocal:
                        case StreamState.Reset:
                        case StreamState.Closed:
                            throw new Exception("Invalid state for sending trailers");
                        case StreamState.ReservedLocal:
                            // We can't be in here if we already have data sent
                            throw new Exception("Unexpected state: ReservedLocal after data sent");
                    }
                }

                await SendHeaders(headers, true);
            }
            finally
            {
                writeMutex.Release();
                if (removeStream)
                {
                    connection.RemoveStream(this);
                }
            }

            return null;
        }

        private void WakeupWaiters()
        {
            this.recvPossible.Set();
        }

        public ValueTask<object> Reset()
        {
            lock (stateMutex)
            {
                // TODO: If we were IDLE it probably means that the remote doesn't even know
                // of us. And we don't need to send reset.

                if (state == StreamState.Reset)
                {
                    // Already reset
                    return new ValueTask<object>(null);
                }
                else if (state == StreamState.Closed)
                {
                    // No need to reset closed streams
                    return new ValueTask<object>(null);
                }
                state = StreamState.Reset;
            }

            // TODO: Send a reset frame. Might use ErrorCode.Cancel for this
            var fh = new FrameHeader {
                StreamId = this.Id,
                Type = FrameType.ResetStream,
                Flags = 0,
            };

            var resetData = new ResetFrameData{ ErrorCode = ErrorCode.Cancel };
            var writeResetTask = connection.Writer.WriteResetStream(fh, resetData);
            // We don't really need to care about this task.
            // If it fails the stream will be reset anyway internally.
            // And failing most likely occured because of a dead connection.
            // The only helpful thing could be attaching a continuation for
            // logging

            // TODO: Unregister from the connection
            this.connection.UnregisterStream(this);

            // Unblock a waiting reader
            WakeupWaiters();
            return new ValueTask<object>(null);
        }

        public async ValueTask<StreamReadResult> ReadAsync(ArraySegment<byte> buffer)
        {
            while (true)
            {
                await recvPossible;

                int windowUpdateAmount = 0;
                StreamReadResult result = new StreamReadResult();
                bool hasResult = false;

                lock (stateMutex)
                {
                    if (state == StreamState.Reset)
                    {
                        throw new Exception("Stream is reset");
                    }

                    if (recvBuf != null && recvBuf.Available > 0)
                    {
                        // Copy data from receive buffer to target
                        var toCopy = Math.Min(recvBuf.Available, buffer.Count);
                        recvBuf.Read(new ArraySegment<byte>(buffer.Array, buffer.Offset, buffer.Count));

                        // Calculate whether we should send a window update frame
                        // after the read is complete.
                        // Only need to do this if the stream has not yet ended
                        if (state != StreamState.Closed && state != StreamState.HalfClosedRemote)
                        {
                            var freeWindow = recvBuf.Capacity - this.receiveWindow;
                            if (freeWindow >= (recvBuf.Capacity/2))
                            {
                                windowUpdateAmount = freeWindow;
                                receiveWindow += windowUpdateAmount;
                            }
                        }

                        result = new StreamReadResult{
                            BytesRead = toCopy,
                            EndOfStream = false,
                        };
                        hasResult = true;
                    }

                    if (state == StreamState.Closed || state == StreamState.HalfClosedRemote)
                    {
                        result = new StreamReadResult{
                            BytesRead = 0,
                            EndOfStream = true,
                        };
                        hasResult = true;
                    }

                    // No data and not closed or reset
                    // Sleep until data arrives or stream is reset
                    recvPossible.Reset();
                }

                if (hasResult)
                {
                    if (windowUpdateAmount > 0)
                    {
                        // We need to send a window update frame before delivering
                        // the result
                        await SendWindowUpdate(windowUpdateAmount);
                    }
                    return result;
                }
            }
        }

        /// <summary>
        /// Checks whether a window update needs to be sent and enqueues it at the session
        /// </summary>
        private async ValueTask<object> SendWindowUpdate(int amount)
        {
            // Send the header
            var fh = new FrameHeader {
                StreamId = this.Id,
                Type = FrameType.WindowUpdate,
                Flags = 0,
            };

            var updateData = new WindowUpdateData
            {
                WindowSizeIncrement = amount,
            };

            try
            {
                await this.connection.Writer.WriteWindowUpdate(fh, updateData);
            }
            catch (Exception)
            {
                // We ignore errors on sending window updates since they are
                // not important to the reading process
            }

            return null;
        }

        public ValueTask<object> WriteAsync(ArraySegment<byte> buffer)
        {
            return WriteAsync(buffer, false);
        }

        public async ValueTask<object> WriteAsync(ArraySegment<byte> buffer, bool endOfStream = false)
        {
            var removeStream = false;

            await writeMutex.WaitAsync();
            try
            {
                bool canWrite = false;

                lock (stateMutex)
                {
                    canWrite = state == StreamState.Open || state == StreamState.HalfClosedRemote;
                    if (state == StreamState.Open && endOfStream)
                    {
                        state = StreamState.HalfClosedLocal;
                    }
                    else if (state == StreamState.HalfClosedRemote && endOfStream)
                    {
                        state = StreamState.Closed;
                        removeStream = true;
                    }
                    // TODO: Send data depending on the stream state
                    // If we have not sent headers before we also need to do that
                    if (!this.headersSent)
                    {
                        throw new Exception("Attempted to write data before headers");
                    }
                    // Remark: There's no need to check whether trailers have already
                    // been sent as writing trailers will (half)close the stream,
                    // which is checked for

                    dataSent = true; // Set a flag do disallow following headers
                }

                // Remark:
                // As we hold the writeMutex nobody can close the stream or send trailers
                // in between.
                // The only thing that may happen is that the stream get's reset in between,
                // which would be reported through the ConnectionWriter to us
                if (!canWrite) throw new Exception("Stream is not writeable"); // TODO: Make better

                var fh = new FrameHeader {
                    StreamId = this.Id,
                    Type = FrameType.Data,
                    Flags = endOfStream ? ((byte)DataFrameFlags.EndOfStream) : (byte)0,
                };

                var res = await connection.Writer.WriteData(fh, buffer);
                if (res != ConnectionWriter.WriteResult.Success)
                {
                    throw new Exception("Can not write to stream"); // TODO: Improve me
                }

                return null;
            }
            finally
            {
                writeMutex.Release();
                if (removeStream)
                {
                    this.connection._removeStream(this.Id);
                }
            }
        }

        public async ValueTask<object> CloseAsync()
        {
            var removeStream = false;
            var sendClose = false;

            await writeMutex.WaitAsync();
            try
            {
                lock (stateMutex)
                {
                    // TODO: Send data depending on the stream state
                    switch (state)
                    {
                        case StreamState.Idle:
                            // TODO: Review these states.
                            // Actually we can't close without going to Open before
                            // What we must to is send the headers with end of stream flag
                            // We must even send trailers here too!
                        case StreamState.ReservedLocal: // TODO: Review these states. Used for push promise
                        case StreamState.ReservedRemote: // TODO: Review these states. Used for push promise
                        case StreamState.Open:
                            state = StreamState.HalfClosedLocal;
                            sendClose = true;
                            dataSent = true;
                            // TODO: Open can mean we have sent the headers or not
                            // We also may need to send trailers or not
                            break;
                        case StreamState.HalfClosedRemote:
                            state = StreamState.Closed;
                            sendClose = true;
                            dataSent = true;
                            removeStream = true;
                            break;
                        case StreamState.HalfClosedLocal:
                        case StreamState.Closed:
                        case StreamState.Reset:
                            // Nothing to do in this cases
                            break;
                        default:
                            throw new Exception("Unhandled stream state");
                    }
                }

                if (sendClose)
                {
                    var fh = new FrameHeader {
                        StreamId = this.Id,
                        Type = FrameType.Data,
                        Flags = ((byte)DataFrameFlags.EndOfStream),
                    };

                    var res = await connection.Writer.WriteData(
                        fh, new ArraySegment<byte>(nullBuffer));
                    if (res != ConnectionWriter.WriteResult.Success)
                    {
                        throw new Exception("Can not write to stream"); // TODO: Improve me
                    }

                    WakeupWaiters();
                    // TODO: We currently only wake up the reader,
                    // which is not affected through a local close.
                    // So this should not be needed
                }
            }
            finally
            {
                writeMutex.Release();
                if (removeStream)
                {
                    this.connection._removeStream(this.Id);
                }
            }

            return null;
        }
    }
}