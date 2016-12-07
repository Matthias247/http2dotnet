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
        private bool headersReceived = false;
        private List<HeaderField> inHeaders = new List<HeaderField>();
        private List<HeaderField> outHeaders = new List<HeaderField>();
        private List<HeaderField> inTrailers = new List<HeaderField>();
        private List<HeaderField> outTrailers = new List<HeaderField>();

        private int receiveWindow; // Might be superficial since that info is also in RingBuf size
        private RingBuf recvBuf;

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

        /// <summary>
        /// Adds a header that should be sent for the outgoing stream
        /// </summary>
        public void AddHeader(HeaderField header)
        {
            lock (stateMutex)
            {
                if (this.headersSent) return; // TODO: Maybe throw?
                this.outHeaders.Add(header);
            }
        }

        /// <summary>
        /// Adds headers that should be sent for the outgoing stream
        /// </summary>
        public void AddHeaders(IEnumerable<HeaderField> headers)
        {
            lock (stateMutex)
            {
                if (this.headersSent) return; // TODO: Maybe throw?
                this.outHeaders.AddRange(headers);
            }
        }

        /// <summary>
        /// Adds a trailer that should be sent for the outgoing stream
        /// </summary>
        public void AddTrailer(HeaderField header)
        {
            lock (stateMutex)
            {
                this.outTrailers.Add(header);
            }
        }

        /// <summary>
        /// Adds trailers that should be sent for the outgoing stream
        /// </summary>
        public void AddTrailers(IEnumerable<HeaderField> headers)
        {
            lock (stateMutex)
            {
                this.outTrailers.AddRange(headers);
            }
        }

        public async ValueTask<object> FlushHeaders()
        {
            await writeMutex.WaitAsync();
            try
            {
                // Check if headers have already been sent
                lock (stateMutex)
                {
                    if (this.headersSent) return null;
                    headersSent = true;
                    switch (state)
                    {
                        case StreamState.Idle:
                            state = StreamState.Open;
                            break;
                        case StreamState.ReservedLocal:
                            state = StreamState.HalfClosedRemote;
                            break;
                    }
                }

                // TODO: Send headers here
            }
            finally
            {
                writeMutex.Release();
            }

            return null;
        }

        private void WakeupWaiters()
        {
            this.recvPossible.Set();
        }

        public async ValueTask<object> Reset()
        {
            lock (stateMutex)
            {
                if (state == StreamState.Reset) return null; // Already reset
                else if (state == StreamState.Closed) return null; // No need to reset closed streams
                state = StreamState.Reset;
            }

            // TODO: Send a reset frame here. Might use ErrorCode.Cancel for this
            // TODO: Unregister from the connection
            WakeupWaiters();
            return null;
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

        public async ValueTask<object> WriteAsync(ArraySegment<byte> buffer)
        {
            await writeMutex.WaitAsync();
            try
            {
                lock (stateMutex)
                {
                    // TODO: Send data depending on the stream state
                    // If we have not sent headers before we also need to do that
                }
            }
            finally
            {
                writeMutex.Release();
            }
            return null;
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
                            break;
                        case StreamState.HalfClosedRemote:
                            state = StreamState.Closed;
                            sendClose = true;
                            removeStream = true;
                            break;
                        case StreamState.HalfClosedLocal:
                        case StreamState.Closed:
                        case StreamState.Reset:
                            // Nothing to do in this cases
                        default:
                            throw new Exception("Unhandled stream state");
                    }
                }

                if (sendClose)
                {
                    this._sendClose();
                    if (removeStream)
                    {
                        this.connection._removeStream(this.Id);
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
            }

            return null;
        }
    }
}