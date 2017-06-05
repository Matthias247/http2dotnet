using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

using Http2.Hpack;
using Http2.Internal;

namespace Http2
{
    /// <summary>
    /// Implementation of a HTTP/2 stream
    /// </summary>
    internal class StreamImpl : IStream
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

        private readonly Connection connection;
        private StreamState state;
        private readonly object stateMutex = new object();

        /// Allows only a single write at a time
        private SemaphoreSlim writeMutex = new SemaphoreSlim(1);
        private bool headersSent = false;
        private bool dataSent = false;

        private enum HeaderReceptionState : byte
        {
            ReceivedNoHeaders,
            ReceivedInformationalHeaders,
            ReceivedAllHeaders,
        }
        private HeaderReceptionState headersReceived =
            HeaderReceptionState.ReceivedNoHeaders;

        private bool dataReceived = false;
        // A trailersReceived field is not necessary, since receiving trailers
        // moves the state to HalfClosedRemote

        private List<HeaderField> inHeaders;
        private List<HeaderField> inTrailers;

        // Semaphores for unblocking async access operations
        private readonly AsyncManualResetEvent readDataPossible =
            new AsyncManualResetEvent(false);
        private readonly AsyncManualResetEvent readHeadersPossible =
            new AsyncManualResetEvent(false);
        private readonly AsyncManualResetEvent readTrailersPossible =
            new AsyncManualResetEvent(false);

        private long declaredInContentLength = -1;
        private long totalInData = 0;

        private int totalReceiveWindow;
        private int receiveWindow;

        /// Intrusive linked list item for receive buffer queue
        private class ReceiveQueueItem
        {
            public byte[] Buffer;
            public int Offset;
            public int Count;
            public ReceiveQueueItem Next;

            public ReceiveQueueItem(ArraySegment<byte> segment)
            {
                this.Buffer = segment.Array;
                this.Offset = segment.Offset;
                this.Count = segment.Count;
            }
        }

        private ReceiveQueueItem receiveQueueHead = null;

        private int ReceiveQueueLength
        {
            get
            {
                int len = 0;
                var item = receiveQueueHead;
                while (item != null)
                {
                    len += item.Count;
                    item = item.Next;
                }
                return len;
            }
        }

        /// Reusable empty list of headers
        private static readonly HeaderField[] EmptyHeaders = new HeaderField[0];

        public StreamImpl(
            Connection connection,
            uint streamId,
            StreamState state,
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
            this.receiveWindow = receiveWindow;
            this.totalReceiveWindow = receiveWindow;
        }

        private async Task SendHeaders(
            IEnumerable<HeaderField> headers, bool endOfStream)
        {
            var fhh = new FrameHeader {
                StreamId = this.Id,
                Type = FrameType.Headers,
                // EndOfHeaders will be auto-set
                Flags = endOfStream ? (byte)HeadersFrameFlags.EndOfStream : (byte)0,
            };
            var res = await connection.writer.WriteHeaders(fhh, headers);
            if (res != ConnectionWriter.WriteResult.Success)
            {
                // TODO: Improve the exception
                throw new Exception("Can not write to stream");
            }
        }

        public Task WriteHeadersAsync(
            IEnumerable<HeaderField> headers, bool endOfStream)
        {
            HeaderValidationResult hvr;
            // TODO: For push promises other validates might need to be used
            if (connection.IsServer) hvr = HeaderValidator.ValidateResponseHeaders(headers);
            else hvr = HeaderValidator.ValidateRequestHeaders(headers);
            if (hvr != HeaderValidationResult.Ok)
            {
                throw new Exception(hvr.ToString());
            }

            return WriteValidatedHeadersAsync(headers, endOfStream);
        }

        internal async Task WriteValidatedHeadersAsync(
            IEnumerable<HeaderField> headers, bool endOfStream)
        {
            var removeStream = false;

            await writeMutex.WaitAsync();
            try
            {
                // Check what already has been sent
                lock (stateMutex)
                {
                    // Check if data has already been sent.
                    // Headers may be only sent in front of all data.
                    if (dataSent)
                    {
                        throw new Exception("Attempted to write headers after data");
                    }

                    if (headersSent)
                    {
                        // TODO: Allow for multiple header packets or not?
                        // It seems it is required for informational headers to work
                        // However we might check later on if a status code different
                        // from 100 was already sent and if yes don't allow further
                        // headers to be sent.
                    }

                    headersSent = true;
                    switch (state)
                    {
                        case StreamState.Idle:
                            state = StreamState.Open;
                            break;
                        case StreamState.ReservedLocal:
                            state = StreamState.HalfClosedRemote;
                            break;
                        case StreamState.Reset:
                            throw new StreamResetException();
                        // TODO: Check if the other stream states are covered
                        // by the dataSent/headersSent logic.
                        // At least in order for a stream to be closed headers
                        // need to be sent. With push promises it might be different
                    }

                    if (state == StreamState.Open && endOfStream)
                    {
                        state = StreamState.HalfClosedLocal;
                    }
                    else if (state == StreamState.HalfClosedRemote && endOfStream)
                    {
                        state = StreamState.Closed;
                        removeStream = true;
                    }
                }

                await SendHeaders(headers, endOfStream); // TODO: Use result
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

        public async Task WriteTrailersAsync(IEnumerable<HeaderField> headers)
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
                        case StreamState.Closed:
                            throw new Exception("Invalid state for sending trailers");
                        case StreamState.Reset:
                            throw new StreamResetException();
                        case StreamState.ReservedLocal:
                            // We can't be in here if we already have data sent
                            throw new Exception("Unexpected state: ReservedLocal after data sent");
                    }
                }

                await SendHeaders(headers, true); // TODO: Use result
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

        public void Cancel()
        {
            var writeResetTask = Reset(ErrorCode.Cancel, false);
            // We don't really need to care about this task.
            // Even if it fails the stream will be reset anyway internally.
            // And failing most likely occured because of a dead connection.
            // The only helpful thing could be attaching a continuation for
            // logging
        }

        public void Dispose()
        {
            // Disposing a stream is equivalent to resetting it
            Cancel();
        }

        internal ValueTask<ConnectionWriter.WriteResult> Reset(
            ErrorCode errorCode, bool fromRemote)
        {
            ValueTask<ConnectionWriter.WriteResult> writeResetTask =
                new ValueTask<ConnectionWriter.WriteResult>(
                    ConnectionWriter.WriteResult.Success);

            lock (stateMutex)
            {
                if (state == StreamState.Reset || state == StreamState.Closed)
                {
                    // Already reset or fully closed
                    return writeResetTask;
                }
                state = StreamState.Reset;

                // Free the receive queue
                var head = receiveQueueHead;
                receiveQueueHead = null;
                FreeReceiveQueue(head);
            }

            if (connection.logger != null)
            {
                connection.logger.LogTrace(
                    "Resetted stream {0} with error code {1}",
                    Id, errorCode);
            }

            // Remark: Even if we are here in IDLE state we need to send the
            // RESET frame. The reason for this is that if we receive a header
            // for a new stream which is invalid a StreamImpl instance will be
            // created and put into IDLE state. The header processing will fail
            // and Reset will get called. As the remote thinks we are in Open
            // state we must send a RST_STREAM.

            if (!fromRemote)
            {
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
                writeResetTask = connection.writer.WriteResetStream(fh, resetData);
            }
            else
            {
                // If we don't send a notification we still have to unregister
                // from the writer in order to cancel pending writes
                connection.writer.RemoveStream(this.Id);
            }

            // Unregister from the connection
            // If this has happened from the remote side the connection will
            // already have performed this
            if (!fromRemote)
            {
                this.connection.UnregisterStream(this);
            }

            // Unblock all waiters
            readDataPossible.Set();
            readTrailersPossible.Set();
            readHeadersPossible.Set();

            return writeResetTask;
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

                    var streamClosedFromRemote =
                        state == StreamState.Closed || state == StreamState.HalfClosedRemote;

                    if (receiveQueueHead != null)
                    {
                        // Copy as much data as possible from internal queue into
                        // user buffer
                        var offset = buffer.Offset;
                        var count = buffer.Count;
                        while (receiveQueueHead != null && count > 0)
                        {
                            // Copy data from receive buffer to target
                            var toCopy = Math.Min(receiveQueueHead.Count, count);
                            Array.Copy(
                                receiveQueueHead.Buffer, receiveQueueHead.Offset,
                                buffer.Array, offset,
                                toCopy);
                            offset += toCopy;
                            count -= toCopy;

                            if (toCopy == receiveQueueHead.Count)
                            {
                                connection.config.BufferPool.Return(
                                    receiveQueueHead.Buffer);
                                receiveQueueHead = receiveQueueHead.Next;
                            }
                            else
                            {
                                receiveQueueHead.Offset += toCopy;
                                receiveQueueHead.Count -= toCopy;
                                break;
                            }
                        }

                        // Calculate whether we should send a window update frame
                        // after the read is complete.
                        // Only need to do this if the stream has not yet ended
                        if (!streamClosedFromRemote)
                        {
                            var isFree = totalReceiveWindow - ReceiveQueueLength;
                            var possibleWindowUpdate = isFree - receiveWindow;
                            if (possibleWindowUpdate >= (totalReceiveWindow/2))
                            {
                                windowUpdateAmount = possibleWindowUpdate;
                                receiveWindow += windowUpdateAmount;

                                if (connection.logger != null &&
                                    connection.logger.IsEnabled(LogLevel.Trace))
                                {
                                    connection.logger.LogTrace(
                                        "Incoming flow control window update:\n" +
                                        "  Stream {0} window: {1} -> {2}",
                                        Id, receiveWindow - windowUpdateAmount, receiveWindow);
                                }
                            }
                        }

                        result = new StreamReadResult{
                            BytesRead = offset - buffer.Offset,
                            EndOfStream = false,
                        };
                        hasResult = true;

                        if (receiveQueueHead == null && !streamClosedFromRemote)
                        {
                            // If all data was consumed the next read must be blocked
                            // until more data comes in or the stream gets closed or reset
                            readDataPossible.Reset();
                        }
                    }
                    else if (streamClosedFromRemote)
                    {
                        // Deliver a notification that the stream was closed
                        result = new StreamReadResult{
                            BytesRead = 0,
                            EndOfStream = true,
                        };
                        hasResult = true;
                    }
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
        /// Sends a window update frame for this stream
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
                await this.connection.writer.WriteWindowUpdate(fh, updateData);
            }
            catch (Exception)
            {
                // We ignore errors on sending window updates since they are
                // not important to the reading task, which is the request
                // handling task.
                // An error means the writer is dead, which again means
                // window updates are no longer necessary
            }

            return null;
        }

        public Task WriteAsync(ArraySegment<byte> buffer)
        {
            return WriteAsync(buffer, false);
        }

        public async Task WriteAsync(ArraySegment<byte> buffer, bool endOfStream = false)
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

                var res = await connection.writer.WriteData(fh, buffer);
                if (res == ConnectionWriter.WriteResult.StreamResetError)
                {
                    throw new StreamResetException();
                }
                else if (res != ConnectionWriter.WriteResult.Success)
                {
                    throw new Exception("Can not write to stream"); // TODO: Improve me
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
        }

        public Task CloseAsync()
        {
            return this.WriteAsync(Constants.EmptyByteArray, true);
        }

        public async Task<IEnumerable<HeaderField>> ReadHeadersAsync()
        {
            await readHeadersPossible;
            IEnumerable<HeaderField> result = null;
            lock (stateMutex)
            {
                if (state == StreamState.Reset)
                {
                    throw new StreamResetException();
                }
                if (inHeaders != null)
                {
                    result = inHeaders;
                    // If the headers which are read are the informatianal headers
                    // we reset the received headers, so that they can be
                    // replaced by the real headers later on.
                    if (result.IsInformationalHeaders())
                    {
                        inHeaders = null;
                        readHeadersPossible.Reset();
                    }
                }
                else result = EmptyHeaders;
            }
            return result;
        }

        public async Task<IEnumerable<HeaderField>> ReadTrailersAsync()
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

            lock (stateMutex)
            {
                // Header frames are not valid in all states
                switch (state)
                {
                    case StreamState.ReservedLocal:
                    case StreamState.ReservedRemote:
                        // Push promises are currently not implemented
                        // So this needs to be reviewed later on
                        // Currently we should never encounter this state
                        return new Http2Error
                        {
                            StreamId = Id,
                            Code = ErrorCode.InternalError,
                            Message = "Received header frame in uncovered push promise state",
                        };
                    case StreamState.Idle:
                    case StreamState.Open:
                    case StreamState.HalfClosedLocal:
                        // Open can mean we have already received headers
                        // (in case we are a server) or not (in case we are
                        // a client and only have sent headers)
                        // If headers were already received before there must be
                        // a data frame in between and these are trailers.
                        // An exception is if we are client, where we can
                        // receive informational headers and normal headers.
                        // This requires no data in between. These header must
                        // contain a 1xy status code.
                        // Trailers must have the EndOfStream flag set and must
                        // always follow after a data frame.
                        if (headersReceived != HeaderReceptionState.ReceivedAllHeaders)
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
                                    StreamId = Id,
                                    Code = ErrorCode.ProtocolError,
                                    Message = "Received invalid headers",
                                };
                            }

                            if (!connection.config.IsServer &&
                                headers.Headers.IsInformationalHeaders())
                            {
                                // Clients support the reception of informational headers.
                                // If this is only an informational header we might
                                // receive additional headers later on.
                                headersReceived =
                                    HeaderReceptionState.ReceivedInformationalHeaders;
                            }
                            else
                            {
                                // Servers don't support informational headers at all.
                                // And if we are client and directly receive response
                                // headers it's also fine.
                                headersReceived =
                                    HeaderReceptionState.ReceivedAllHeaders;
                            }
                            wakeupHeaderWaiter = true;
                            // TODO: Uncompress cookie headers here?
                            declaredInContentLength = headers.Headers.GetContentLength();
                            inHeaders = headers.Headers;
                        }
                        else if (!dataReceived)
                        {
                            // We already have received headers, so this should
                            // be trailers. However there was no DATA frame in
                            // between, so this is simply invalid.
                            return new Http2Error
                            {
                                StreamId = Id,
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
                                    StreamId = Id,
                                    Code = ErrorCode.ProtocolError,
                                    Message = "Received trailers without EndOfStream flag",
                                };
                            }
                            var hvr = HeaderValidator.ValidateTrailingHeaders(headers.Headers);
                            if (hvr != HeaderValidationResult.Ok)
                            {
                                return new Http2Error
                                {
                                    StreamId = Id,
                                    Code = ErrorCode.ProtocolError,
                                    Message = "Received invalid trailers",
                                };
                            }

                            // If content-length was set we must also validate
                            // it against the received dataamount here
                            if (declaredInContentLength >= 0 &&
                                declaredInContentLength != totalInData)
                            {
                                return new Http2Error
                                {
                                    StreamId = Id,
                                    Code = ErrorCode.ProtocolError,
                                    Message =
                                        "Length of DATA frames does not match content-length",
                                };
                            }

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
                            StreamId = Id,
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
        public Http2Error? PushBuffer(
            ArraySegment<byte> buffer,
            bool endOfStream,
            out bool tookBufferOwnership)
        {
            tookBufferOwnership = false;
            var wakeupDataWaiter = false;
            var wakeupTrailerWaiter = false;
            var removeStream = false;

            lock (stateMutex)
            {
                // Data frames are not valid in all states
                switch (state)
                {
                    case StreamState.ReservedLocal:
                    case StreamState.ReservedRemote:
                        // Push promises are currently not implemented
                        // At the moment these should already been
                        // rejected in the Connection.
                        // This needs to be reviewed later on
                        throw new NotImplementedException();
                    case StreamState.Open:
                    case StreamState.HalfClosedLocal:
                        if (headersReceived != HeaderReceptionState.ReceivedAllHeaders)
                        {
                            // Received DATA without HEADERS before.
                            // State Open can also mean we only have sent
                            // headers but not received them.
                            // Therefore checking the state alone isn't sufficient.
                            return new Http2Error
                            {
                                StreamId = Id,
                                Code = ErrorCode.ProtocolError,
                                Message = "Received data before all headers",
                            };
                        }

                        // Only enqueue DATA frames with a real content length
                        // of at least 1 byte, otherwise the reader is needlessly
                        // woken up.
                        // Empty DATA frames are also not checked against the
                        // flow control window, which means they are valid even
                        // in case of a negative flow control window (which is
                        // not possible in the current state of the implementation).
                        // However we still treat empty DATA frames as a valid
                        // separator between HEADERS and trailing HEADERS.
                        if (buffer.Count > 0)
                        {
                            // Check if the flow control window is exceeded
                            if (buffer.Count > receiveWindow)
                            {
                                return new Http2Error
                                {
                                    StreamId = Id,
                                    Code = ErrorCode.FlowControlError,
                                    Message = "Received window exceeded",
                                };
                            }
                            if (connection.logger != null &&
                                connection.logger.IsEnabled(LogLevel.Trace))
                            {
                                connection.logger.LogTrace(
                                    "Incoming flow control window update:\n" +
                                    "  Stream {0} window: {1} -> {2}",
                                    Id, receiveWindow, receiveWindow - buffer.Count);
                            }
                            receiveWindow -= buffer.Count;

                            // Enqueue the data at the end of the receive queue
                            // TODO: Instead of appending buffers with only small
                            // content on the end of each other we might to concat
                            // the buffers together, which avoids the worst-case
                            // scenario: The remote side sending us lots of 1byte
                            // DATA frames, where we need a queue item for each
                            // one.
                            var newItem = new ReceiveQueueItem(buffer);
                            EnqueueReceiveQueueItem(newItem);
                            wakeupDataWaiter = true;
                            tookBufferOwnership = true;
                        }
                        // Allow trailing headers afterwards
                        dataReceived = true;

                        // Check if data matches declared content-length
                        // TODO: What should be done if the declared
                        // content-length was invalid (-2)?
                        totalInData += buffer.Count;
                        if (endOfStream &&
                            declaredInContentLength >= 0 &&
                            declaredInContentLength != totalInData)
                        {
                            return new Http2Error
                            {
                                StreamId = Id,
                                Code = ErrorCode.ProtocolError,
                                Message =
                                    "Length of DATA frames does not match content-length",
                            };
                        }

                        // All checks OK so far, wakeup waiter and handle state changes
                        // Handle state changes that are caused by DATA frames
                        if (endOfStream)
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
                            wakeupDataWaiter = true;
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
                            StreamId = Id,
                            Code = ErrorCode.StreamClosed,
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

        /// <summary>
        /// Enqueues a new item at the end of the receive queue
        /// </summary>
        private void EnqueueReceiveQueueItem(ReceiveQueueItem newItem)
        {
            if (receiveQueueHead == null)
            {
                receiveQueueHead = newItem;
            }
            else
            {
                var current = receiveQueueHead;
                var next = receiveQueueHead.Next;
                while (next != null)
                {
                    current = next;
                    next = current.Next;
                }
                current.Next = newItem;
            }
        }

        /// <summary>
        /// Frees a linked list of receive queue buffers.
        /// </summary>
        private void FreeReceiveQueue(ReceiveQueueItem item)
        {
            while (item != null && item.Buffer != null)
            {
                connection.config.BufferPool.Return(item.Buffer);
                item.Buffer = null;
                var current = item;
                item = item.Next;
                current.Next = null;
            }
        }
    }
}