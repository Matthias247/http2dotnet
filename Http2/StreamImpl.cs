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
        private bool headersSent = false;
        private bool dataSent = false;

        private bool headersReceived = false;
        private bool dataReceived = false;
        private bool trailersReceived = false;
        private List<HeaderField> inHeaders;
        private List<HeaderField> inTrailers;

        // Semaphores for unblocking async access operations
        private AsyncManualResetEvent readDataPossible = new AsyncManualResetEvent(false);
        private AsyncManualResetEvent readHeadersPossible = new AsyncManualResetEvent(false);
        private AsyncManualResetEvent readTrailersPossible = new AsyncManualResetEvent(false);

        private int receiveWindow; // Might be superficial since that info is also in RingBuf size
        private RingBuf recvBuf;

        private static readonly HeaderField[] EmptyHeaders = new HeaderField[0];
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

            // Unblock all waiters
            readDataPossible.Set();
            readTrailersPossible.Set();
            readHeadersPossible.Set();
        }

        public async ValueTask<StreamReadResult> ReadAsync(ArraySegment<byte> buffer)
        {
            while (true)
            {
                await readDataPossible;

                int windowUpdateAmount = 0;
                StreamReadResult result = new StreamReadResult();
                bool hasResult = false;

                lock (stateMutex)
                {
                    if (state == StreamState.Reset)
                    {
                        throw new StreamResetException();
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
                    readDataPossible.Reset();
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
                lock (stateMutex)
                {
                    // Check the current stream state
                    if (state == StreamState.Reset)
                    {
                        throw new StreamResetException();
                    }
                    else if (state != StreamState.Open && state != StreamState.HalfClosedRemote)
                    {
                        throw new Exception("Attempt to write data in invalid stream state");
                    }
                    else if (state == StreamState.Open && endOfStream)
                    {
                        state = StreamState.HalfClosedLocal;
                    }
                    else if (state == StreamState.HalfClosedRemote && endOfStream)
                    {
                        state = StreamState.Closed;
                        removeStream = true;
                    }

                    // Besides state check also check if headers have already been sent
                    // StreamState.Open could mean we only have received them
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

                var fh = new FrameHeader {
                    StreamId = this.Id,
                    Type = FrameType.Data,
                    Flags = endOfStream ? ((byte)DataFrameFlags.EndOfStream) : (byte)0,
                };

                var res = await connection.Writer.WriteData(fh, buffer);
                if (res == ConnectionWriter.WriteResult.StreamResetError)
                {
                    throw new StreamResetException();
                }
                else if (res != ConnectionWriter.WriteResult.Success)
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

        public ValueTask<object> CloseAsync()
        {
            return this.WriteAsync(EmptyBuffer, true);
        }

        public async ValueTask<IEnumerable<HeaderField>> ReadHeaders()
        {
            await readHeadersPossible;
            IEnumerable<HeaderField> result = null;
            lock (stateMutex)
            {
                if (state == StreamState.Reset)
                {
                    throw new StreamResetException();
                }
                if (inHeaders != null) result = inHeaders;
                else result = EmptyHeaders;
            }
            return result;
        }

        public async ValueTask<IEnumerable<HeaderField>> ReadTrailers()
        {
            await readTrailersPossible;
            IEnumerable<HeaderField> result = null;
            lock (stateMutex)
            {
                if (state == StreamState.Reset)
                {
                    throw new StreamResetException();
                }
                if (inTrailers != null) result = inTrailers;
                else result = EmptyHeaders;
            }
            return result;
        }

        /// <summary>
        /// Processes the reception of incoming headers
        /// </summary>
        public Http2Error? ProcessHeaders(
            CompleteHeadersFrameData headers)
        {
            var wakeupDataWaiter = false;
            var wakeupHeaderWaiter = false;
            var wakeupTrailerWaiter = false;
            var removeStream = false;

            // TODO: Need to verify the headers according to given rules

            lock (stateMutex)
            {
                // Header frames are not valid in all states
                switch (state)
                {
                    case StreamState.ReservedLocal:
                    case StreamState.ReservedRemote:
                        // Push promises are currently not implemented
                        // So this needs to be reviewed later on
                        throw new NotImplementedException();
                    case StreamState.Idle:
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
                        // Decision for the current point of time:
                        // Don't support informational frames, since there is
                        // no way to store the second headers
                        // This must be handled on a higher layer.
                        // On this layer we will reset the stream if we get
                        // additional headers
                        if (!headersReceived)
                        {
                            // We are receiving headers
                            HeaderValidationResult hvr;
                            if (connection.IsServer)
                            {
                                hvr = HeaderValidator.ValidateRequestHeaders(headers.Headers);
                            }
                            else
                            {
                                hvr = HeaderValidator.ValidateResponseHeaders(headers.Headers);
                            }
                            if (hvr != HeaderValidationResult.Ok)
                            {
                                return new Http2Error
                                {
                                    Type = ErrorType.StreamError,
                                    Code = ErrorCode.ProtocolError,
                                    Message = "Received invalid headers",
                                };
                            }
                            headersReceived = true;
                            wakeupHeaderWaiter = true;
                            inHeaders = headers.Headers;
                        }
                        else if (!dataReceived)
                        {
                            // We already have received headers, so this should
                            // be trailers. However there was no DATA frame in
                            // between, so this is simply invalid.
                            return new Http2Error
                            {
                                Type = ErrorType.StreamError,
                                Code = ErrorCode.ProtocolError,
                                Message = "Received trailers without headers",
                            };
                        }
                        else
                        {
                            // These are trailers
                            // trailers must have end of stream set. It is not
                            // valid to receive multiple trailers
                            if (!headers.EndOfStream)
                            {
                                return new Http2Error
                                {
                                    Type = ErrorType.StreamError,
                                    Code = ErrorCode.ProtocolError,
                                    Message = "Received trailers without EndOfStream flag",
                                };
                            }
                            var hvr = HeaderValidator.ValidateTrailingHeaders(headers.Headers);
                            if (hvr != HeaderValidationResult.Ok)
                            {
                                return new Http2Error
                                {
                                    Type = ErrorType.StreamError,
                                    Code = ErrorCode.ProtocolError,
                                    Message = "Received invalid trailers",
                                };
                            }
                            trailersReceived = true;
                            wakeupTrailerWaiter = true;
                            inTrailers = headers.Headers;
                        }

                        // Handle state changes that are caused by HEADERS frame
                        if (state == StreamState.Idle)
                        {
                            state = StreamState.Open;
                        }
                        if (headers.EndOfStream)
                        {
                            if (state == StreamState.HalfClosedLocal)
                            {
                                state = StreamState.Closed;
                                removeStream = true;
                            }
                            else // Must be Open, since Idle moves to Open
                            {
                                state = StreamState.HalfClosedRemote;
                            }
                            wakeupTrailerWaiter = true;
                            wakeupDataWaiter = true;
                        }
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
                        // which is not stored for efficiency. If we reset the
                        // stream late headers are ok. If the remote resetted it
                        // this is a protocol error for the stream.
                        // As it does not really matter just ignore the frame.
                        break;
                    default:
                        throw new Exception("Unhandled stream state");
                }
            }

            // Wakeup any blocked calls that are waiting on headers or end of stream
            if (wakeupHeaderWaiter)
            {
                readHeadersPossible.Set();
            }
            if (wakeupDataWaiter)
            {
                readDataPossible.Set();
            }
            if (wakeupTrailerWaiter)
            {
                readTrailersPossible.Set();
            }

            if (removeStream)
            {
                connection.UnregisterStream(this);
            }

            return null;
        }

        /// <summary>
        /// Processes the reception of a DATA frame.
        /// The connection is responsible for checking the maximum frame length
        /// before calling this function.
        /// </summary>
        public async ValueTask<Http2Error?> ProcessData(
            FrameHeader dataHeader,
            IStreamReader inputStream,
            byte[] tempBuf)
        {
            // TODO: Actually we should read the data directly into the RingBuf
            // However as async reads therein are not supported we read it in the
            // temp buffer and copy it into the RingBuf later on.
            // Remark: The data always has to be consumed, even if the stream is
            // reset. Otherwise the remaining protocol framing would be broken.
            await inputStream.ReadAll(
                new ArraySegment<byte>(tempBuf, 0, dataHeader.Length));

            // Checkout the real data content
            var offset = 0;
            var length = dataHeader.Length;
            var flags = (DataFrameFlags)dataHeader.Flags;
            if (flags.HasFlag(DataFrameFlags.Padded))
            {
                // Must have at least 1 byte
                if (length < 1)
                {
                    return new Http2Error
                    {
                        Type = ErrorType.StreamError,
                        Code = ErrorCode.FrameSizeError,
                        Message = "Frame is too small to contain padding",
                    };
                }
                var padLen = tempBuf[0];
                offset++;
                length--;
                length -= padLen;

                if (length < 0)
                {
                    return new Http2Error
                    {
                        Type = ErrorType.StreamError,
                        Code = ErrorCode.FrameSizeError,
                        Message = "Frame is too small after substracting padding",
                    };
                }
            }

            var contentSegment = new ArraySegment<byte>(tempBuf, offset, length);
            var wakeupDataWaiter = false;
            var wakeupTrailerWaiter = false;
            var removeStream = false;

            lock (stateMutex)
            {
                // Header frames are not valid in all states
                switch (state)
                {
                    case StreamState.ReservedLocal:
                    case StreamState.ReservedRemote:
                        // Push promises are currently not implemented
                        // So this needs to be reviewed later on
                        throw new NotImplementedException();
                    case StreamState.Open:
                    case StreamState.HalfClosedLocal:
                        if (!dataReceived)
                        {
                            // Received DATA without HEADERS before.
                            // As state Open can also mean we only have sent
                            // headers but not received them checking the state
                            // alone isn't sufficient.
                            return new Http2Error
                            {
                                Type = ErrorType.StreamError,
                                Code = ErrorCode.ProtocolError,
                                Message = "Received trailers without headers",
                            };
                        }
                        // Check if the flow control window is exceeded
                        if (length > receiveWindow)
                        {
                            return new Http2Error
                            {
                                Type = ErrorType.StreamError,
                                Code = ErrorCode.FlowControlError,
                                Message = "Received window exceeded",
                            };
                        }
                        receiveWindow -= length;
                        // Copy the data
                        recvBuf.Write(contentSegment);
                        dataReceived = true;
                        wakeupDataWaiter = true;
                        // Handle state changes that are caused by DATA frames
                        if (dataHeader.HasEndOfStreamFlag)
                        {
                            if (state == StreamState.HalfClosedLocal)
                            {
                                state = StreamState.Closed;
                                removeStream = true;
                            }
                            else // Open
                            {
                                state = StreamState.HalfClosedRemote;
                            }
                            wakeupTrailerWaiter = true;
                        }
                        break;
                    case StreamState.Idle:
                    case StreamState.HalfClosedRemote:
                    case StreamState.Closed:
                        // Received a DATA frame for a stream that was
                        // already closed or not properly opened from remote side.
                        // That's not valid
                        return new Http2Error
                        {
                            Code = ErrorCode.StreamClosed,
                            Type = ErrorType.StreamError,
                            Message = "Received data for closed stream",
                        };
                    case StreamState.Reset:
                        // The stream was already reset
                        // What we really should do here depends on the previous state,
                        // which is not stored for efficiency. If we reset the
                        // stream late headers are ok. If the remote resetted it
                        // this is a protocol error for the stream.
                        // As it does not really matter just ignore the frame.
                        break;
                    default:
                        throw new Exception("Unhandled stream state");
                }
            }

            // Wakeup any blocked calls that are waiting on data or end of stream
            if (wakeupDataWaiter)
            {
                // Wakeup any blocked call that waits for headers to get available
                readDataPossible.Set();
            }
            if (wakeupTrailerWaiter)
            {
                readTrailersPossible.Set();
            }

            if (removeStream)
            {
                connection.UnregisterStream(this);
            }

            return null;
        }
    }
}