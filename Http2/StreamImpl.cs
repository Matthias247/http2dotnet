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
        /// <summary>Allows only a single write at a time</summary>
        private SemaphoreSlim writeMutex = new SemaphoreSlim(1);
        /// <summary>Unblocks pending read operations</summary>
        private AsyncManualResetEvent recvPossible = new AsyncManualResetEvent(false);

        private bool headersSent = false;
        private bool dataSent = false;

        private bool headersReceived = false;
        private bool trailersReceived = false;
        private List<HeaderField> inHeaders;
        private List<HeaderField> inTrailers;

        private int receiveWindow; // Might be superficial since that info is also in RingBuf size
        private RingBuf recvBuf;

        private static readonly ArraySegment<byte> EmptyBuffer =
            new ArraySegment<byte>(new byte[0]);

        public StreamImpl(
            Connection connection,
            uint streamId,
            StreamState state,
            List<HeaderField> receivedHeaders,
            int recvBufSize,
            int receiveWindow)
        {
            this.connection = connection;
            this.Id = streamId;

            // In case we are on the server side and the client opens a stream
            // the expected state is Open and we get headers.
            // TODO: Or are we only Idle and get headers soon after that?
            // In case we are on the client side we are idle and need
            // to send headers before doing anything.
            this.state = state;
            this.inHeaders = receivedHeaders;
            if (receivedHeaders != null) headersReceived = true;
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
                // TODO: Improve the exception
                throw new Exception("Can not write to stream");
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

                    if (headersSent)
                    {
                        // TODO: Allow for multiple header packets or not?
                        // It seems it is required for informational headers to work
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
                    connection.UnregisterStream(this);
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
                    connection.UnregisterStream(this);
                }
            }

            return null;
        }

        private void WakeupWaiters()
        {
            this.recvPossible.Set();
        }

        public void Cancel()
        {
            Reset(ErrorCode.Cancel);
        }

        internal void Reset(ErrorCode errorCode)
        {
            lock (stateMutex)
            {
                // TODO: If we were IDLE it probably means that the remote doesn't even know
                // of us. And we don't need to send reset.

                if (state == StreamState.Reset || state == StreamState.Closed)
                {
                    // Already reset or fully closed
                    return;
                }
                state = StreamState.Reset;
            }

            // Send a reset frame with the given error code
            var fh = new FrameHeader
            {
                StreamId = this.Id,
                Type = FrameType.ResetStream,
                Flags = 0,
            };
            var resetData = new ResetFrameData
            {
                ErrorCode = errorCode
            };
            var writeResetTask = connection.Writer.WriteResetStream(fh, resetData);
            // We don't really need to care about this task.
            // If it fails the stream will be reset anyway internally.
            // And failing most likely occured because of a dead connection.
            // The only helpful thing could be attaching a continuation for
            // logging

            // Unregister from the connection
            this.connection.UnregisterStream(this);

            // Unblock a waiting reader
            WakeupWaiters();
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
                    connection.UnregisterStream(this);
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

                    var res = await connection.Writer.WriteData(fh, EmptyBuffer);
                    if (res != ConnectionWriter.WriteResult.Success)
                    {
                        // TODO: Throw a better typed exception
                        throw new Exception("Can not write to stream");
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
                    connection.UnregisterStream(this);
                }
            }

            return null;
        }

        public Http2Error? ProcessHeaders(
            List<HeaderField> headers,
            bool endOfStream)
        {
            lock (stateMutex)
            {
                // Header frames are not valid in all states
                switch (state)
                {
                    case StreamState.Idle:
                        // Received the first header frame
                        // This moves the stream into the Open state
                        state = StreamState.Open;
                        if (endOfStream) state = StreamState.HalfClosedRemote;
                        headersReceived = true;
                        // TODO: Wakeup the reader?
                        break;
                    case StreamState.ReservedLocal:
                    case StreamState.ReservedRemote:
                        // Push promises are currently not implemented
                        // So this needs to be reviewed later on
                        throw new NotImplementedException();
                    case StreamState.Open:
                    case StreamState.HalfClosedLocal:
                        // Open can mean we have already received headers
                        // (in case we are a server) or not (in case we are
                        // a client and only have sent headers)
                        // If headers were already before there must be a
                        // data frame in between and these are trailers.
                        // An exception is if we are client, where we can
                        // receive informational headers and normal headers.
                        // This required no data in between. And that the
                        // received headers contain a 1xy status code
                        // Trailers must have EndOfStream set.
                        if (connection.IsServer)
                        {
                            // TODO: On Server side we can't be in half-closed here
                            // Is this a problem?
                            // Actually on server side we may only receive trailers here
                            // headers are received in State Idle

                        }
                        else
                        {

                        }
                        if (headersReceived)
                        {

                        }
                        if (endOfStream)
                        {
                            if (state == StreamState.Open)
                            {
                                state = StreamState.HalfClosedRemote;
                            }
                            else // HalfClosedLocal
                            {
                                state = StreamState.Closed;
                                // TODO: The stream must be removed from the connection
                            }
                        }
                        // TODO: Wakeup the reader?
                        break;
                    case StreamState.HalfClosedRemote:
                    case StreamState.Closed:
                        // Received a header frame for a stream that was
                        // already closed from remote side.
                        // That's not valid
                        return new Http2Error
                        {
                            Code = ErrorCode.StreamClosed,
                            Type = ErrorType.StreamError,
                            Message = "Received headers for closed stream",
                        };
                    case StreamState.Reset:
                        // The stream was already reset
                        // What we really should do here depends on the previous state,
                        // which is not stored for efficiency.
                        // But most likely the incoming data is just late and the remote
                        // has not received our Reset.
                        // We just ignore the incoming frame.
                        break;
                    default:
                        throw new Exception("Unhandled stream state");
                }
            }
            return null;
        }
    }
}