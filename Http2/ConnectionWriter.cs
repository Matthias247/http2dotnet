using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Hpack;
using Http2.Internal;

namespace Http2
{
    /// <summary>
    /// The task that writes to the connection
    /// </summary>
    // TODO: This is only public for testing purposes
    public class ConnectionWriter
    {
        /// <summary>
        /// Configuration options for the ConnectionWriter
        /// </summary>
        public struct Options
        {
            public int MaxFrameSize;
            public int MaxHeaderListSize;
            public int InitialWindowSize;
        }

        private struct StreamData
        {
            public uint StreamId;
            public int Window;
            public Queue<WriteRequest> WriteQueue;
            public bool EndOfStreamQueued;
            public bool ResetQueued;
        }

        // This needs be a class in order to be mutatable
        private class WriteRequest
        {
            public FrameHeader Header;
            public WindowUpdateData WindowUpdateData;
            public ResetFrameData ResetFrameData;
            public GoAwayFrameData GoAwayData;
            public List<HeaderField> Headers;
            public ArraySegment<byte> Data;
            public AsyncManualResetEvent Completed;
        }

        /// <summary>The associated connection</summary>
        public Connection Connection { get; }
        /// <summary>The output stream this is utilizing</summary>
        IStreamWriterCloser outStream;

        /// <summary>HPack encoder</summary>
        Hpack.Encoder hEncoder;
        /// <summary>Current set of options</summary>
        Options options;

        /// <summary>Whether a goaway frame is already queued</summary>
        bool goAwayQueued = false;
        /// <summary>Whether the writer was requested to close after completing all writes</summary>
        bool closeRequested = false;

        /// <summary>Outstanding writes the are associated to the connection</summary>
        Queue<WriteRequest> WriteQueue = new Queue<WriteRequest>();
        /// <summary>Streams for which data needs to be written</summary>
        List<StreamData> Streams = new List<StreamData>();

        private SemaphoreSlim mutex = new SemaphoreSlim(1);
        private AsyncManualResetEvent wakeupWriter = new AsyncManualResetEvent(false);
        private TaskCompletionSource<bool> doneTcs = new TaskCompletionSource<bool>();
        private bool closed = false;

        private byte[] outBuf;

        /// <summary>Flow control window for the connection</summary>
        private int connFlowWindow = Constants.InitialConnectionWindowSize;

        /// <summary>
        /// Returns a task that will be completed when the write task finishes
        /// </summary>
        public Task Done
        {
            get { return doneTcs.Task; }
        }

        /// <summary>
        /// Creates a new instance of the ConnectionWriter with the given options
        /// </summary>
        public ConnectionWriter(
            Connection connection, IStreamWriterCloser outStream,
            Options options, Hpack.Encoder.Options hpackOptions)
        {
            this.Connection = connection;
            this.outStream = outStream;
            this.options = options;
            this.hEncoder = new Hpack.Encoder(hpackOptions);
            // Create a buffer for outgoing data
            // TODO: Pooling
            this.outBuf = new byte[FrameHeader.HeaderSize + options.MaxFrameSize];
            // Start the task that performs the actual writing
            Task.Run(() => this.Run());
        }

        /// <summary>
        /// The mainloop of the connection writer
        /// </summary>
        private async Task Run()
        {
            try
            {
                bool continueRun = true;
                while (continueRun)
                {
                    // Wait until there is something to do for us
                    await this.wakeupWriter;
                    // Fetch the next task from shared information
                    await this.mutex.WaitAsync();
                    WriteRequest writeRequest = GetNextReadyWriteRequest();
                    bool doClose = false;
                    // Copy the maximum frame size inside the lock to avoid races
                    int maxFrameSize = options.MaxFrameSize;

                    // If there's nothing to write check if we should close the connection
                    if (writeRequest != null)
                    {
                        if (this.closeRequested)
                        {
                            doClose = true;
                        }
                        else
                        {
                            // There is really no task for us.
                            // Sleep until we got one
                            this.wakeupWriter.Reset();
                        }
                    }

                    this.mutex.Release();

                    if (writeRequest != null)
                    {
                        await ProcessWriteRequest(writeRequest, maxFrameSize);
                    }
                    else if (doClose)
                    {
                        // We are tasked to close the connection
                        await outStream.CloseAsync();
                        continueRun = false;
                    }
                }
            }
            catch (Exception e)
            {
                // We will catch this exception if writing to the output stream
                // will fail at any point of time
                // We need to fail all pending writes at that point
                // TODO: Might not be necessary to handle it in case of Close() errors
            }
            finally
            {
                // Set the closed flag which will avoid new write items to be added
                await this.mutex.WaitAsync();
                closed = true;
                this.mutex.Release();
                // TODO: Fail pending writes that are still queued up
                doneTcs.SetResult(true);
            }
        }

        private WriteRequest GetNextReadyWriteRequest()
        {
            // Look if there are writes necessary for the connection queue
            // Connection related writes are prioritized against
            // stream related writes
            // TODO: Check if that's really always the case
            if (this.WriteQueue.Count > 0)
            {
                var writeRequest = this.WriteQueue.Dequeue();
                return writeRequest;
            }

            // Look if any of the streams is writeable
            // This logic is quite primitive at the moment and won't be fair to
            // higher stream numbers. However the flow control windows will avoid
            // total starvation for those
            foreach (var s in Streams)
            {
                if (s.WriteQueue.Count == 0) continue;
                var first = s.WriteQueue.Peek();
                // If it's not a data frame we can always write it
                // Otherwise me must check the flow control window
                if (first.Header.Type == FrameType.Data)
                {
                    // Check how much flow we have
                    var availableFlow = Math.Min(this.connFlowWindow, s.Window);
                    if (availableFlow == 0) continue; // Not flow
                    if (availableFlow < first.Data.Count)
                    {
                        // We can write a part of the request
                        // In order to write the complete request we have to segment it
                        s.WriteQueue.Dequeue();
                        var we = new WriteRequest();
                        we.Header = first.Header;
                        we.Data = new ArraySegment<byte>(
                            first.Data.Array, first.Data.Offset, availableFlow);
                        we.Completed = null; // Don't send the completion notification
                        // Adjust the amount of bytes that have to be written later on
                        var oldData = first.Data;
                        first.Data = new ArraySegment<byte>(
                            oldData.Array, oldData.Offset + availableFlow, oldData.Count - availableFlow);
                        return we;
                    }
                }
                // We have not continued, so we can write this item
                return s.WriteQueue.Dequeue();
            }

            return null;
        }

        private ValueTask<object> ProcessWriteRequest(WriteRequest wr, int maxFrameSize)
        {
            // TODO: In general we SHOULD check whether the data payload exceeds the
            // maximum frame size. It is just copied at the moment
            // However in general that won't fail, as the maxFrameSize is bigger
            // than settings or other data
            try
            {
                switch (wr.Header.Type)
                {
                    case FrameType.Headers:
                        return WriteHeaders(wr, maxFrameSize);
                    case FrameType.PushPromise:
                        return WritePushPromise(wr);
                    case FrameType.Data:
                        return WriteDataFrame(wr);
                    case FrameType.GoAway:
                        return WriteGoAwayFrame(wr);
                    case FrameType.Continuation:
                        throw new Exception("Continuations might not be directly queued");
                    case FrameType.Ping:
                        return WritePingFrame(wr);
                    case FrameType.Priority:
                        return WritePriorityFrame(wr);
                    case FrameType.ResetStream:
                        return WriteResetFrame(wr);
                    case FrameType.WindowUpdate:
                        return WriteWindowUpdateFrame(wr);
                    case FrameType.Settings:
                        return WriteSettingsFrame(wr);
                    default:
                        throw new Exception("Unknown frame type");
                }
            }
            finally
            {
                if (wr.Completed != null)
                {
                    wr.Completed.Set();
                }
            }
        }

        private ValueTask<object> WriteWindowUpdateFrame(WriteRequest wr)
        {
            wr.Header.Length = WindowUpdateData.Size;
            // Serialize the frame header into the outgoing buffer
            wr.Header.EncodeInto(new ArraySegment<byte>(outBuf, 0, FrameHeader.HeaderSize));
            // Serialize the window update data
            wr.WindowUpdateData.EncodeInto(new ArraySegment<byte>(outBuf, FrameHeader.HeaderSize, WindowUpdateData.Size));
            var totalSize = FrameHeader.HeaderSize + WindowUpdateData.Size;
            var data = new ArraySegment<byte>(outBuf, 0, totalSize);

            // Write the header
            return this.outStream.WriteAsync(data);
        }

        private void CheckClosed()
        {
            if (this.closed)
            {
                mutex.Release();
                throw new Exception("Writer is closed");
            }
        }

        private bool TryEnqueueWriteRequest(uint streamId, WriteRequest wr)
        {
            if (streamId == 0)
            {
                this.WriteQueue.Enqueue(wr);
                return true;
            }

            foreach (var stream in Streams)
            {
                if (stream.StreamId == streamId)
                {
                    stream.WriteQueue.Enqueue(wr);
                    return true;
                }
            }

            return false;
        }

        private WriteRequest AllocateWriteRequest()
        {
            var wr = new WriteRequest();
            return wr;
        }

        private void ReleaseWriteRequest(WriteRequest wr)
        {
            wr.Completed.Reset();
        }

        private async ValueTask<object> PerformWriteRequest(
            uint streamId, Action<WriteRequest> populateRequest)
        {
            WriteRequest wr = null;
            await mutex.WaitAsync();
            try
            {
                CheckClosed();
                wr = AllocateWriteRequest();
                populateRequest(wr);

                var enqueued = TryEnqueueWriteRequest(streamId, wr);

                if (!enqueued)
                {
                    throw new Exception("Stream is not writable");
                }
            }
            finally
            {
                mutex.Release();
            }

            // Wakeup the task
            // TODO: We might wakeup the task only if we also have a flow control
            // window. However that isn't currently reported by TryEnqueueWriteRequest
            // and would only be a minor optimization
            wakeupWriter.Set();
            // Wait until the request was written
            await wr.Completed;
            ReleaseWriteRequest(wr);

            return null;
        }

        public ValueTask<object> WriteHeaders(
            FrameHeader header, List<HeaderField> headers)
        {
            return PerformWriteRequest(
                header.StreamId,
                wr => {
                    wr.Header = header;
                    wr.Headers = headers;
                });
        }

        public ValueTask<object> WriteSettings(
            FrameHeader header, ArraySegment<byte> data)
        {
            return PerformWriteRequest(
                0,
                wr => {
                    wr.Header = header;
                    wr.Data = data;
                });
        }

        public ValueTask<object> WriteResetStream(
            FrameHeader header, ResetFrameData data)
        {
            return PerformWriteRequest(
                header.StreamId,
                wr => {
                    wr.Header = header;
                    wr.ResetFrameData = data;
                });
        }

        public ValueTask<object> WritePing(
            FrameHeader header, ArraySegment<byte> data)
        {
            return PerformWriteRequest(
                0,
                wr => {
                    wr.Header = header;
                    wr.Data = data;
                });
        }

        public ValueTask<object> WriteWindowUpdate(
            FrameHeader header, WindowUpdateData data)
        {
            return PerformWriteRequest(
                header.StreamId,
                wr => {
                    wr.Header = header;
                    wr.WindowUpdateData = data;
                });
        }

        public ValueTask<object> WriteGoAway(
            FrameHeader header, GoAwayFrameData data)
        {
            return PerformWriteRequest(
                0,
                wr => {
                    wr.Header = header;
                    wr.GoAwayData = data;
                });
        }

        public ValueTask<object> WriteData(
            FrameHeader header, ArraySegment<byte> data)
        {
            return PerformWriteRequest(
                header.StreamId,
                wr1 => {
                    wr1.Header = header;
                    wr1.Data = data;
                });
                
            // WriteRequest wr = null;
            // await mutex.WaitAsync();
            // try
            // {
            //     CheckClosed();
            //     wr = AllocateWriteRequest();
            //     wr.Header = header;
            //     wr.Data = data;

            //     var enqueued = TryEnqueueWriteRequest(header.StreamId, wr);

            //     if (!enqueued)
            //     {
            //         throw new Exception("Stream is not writable");
            //     }
            // }
            // finally
            // {
            //     mutex.Release();
            // }

            // // Wakeup the task
            // // TODO: We might wakeup the task only if we also have a flow control
            // // window. However that isn't currently reported by TryEnqueueWriteRequest
            // // and would only be a minor optimization
            // wakeupWriter.Set();
            // // Wait until the request was written
            // await wr.Completed;
            // ReleaseWriteRequest(wr);

            // return null;
        }

        /// <summary>
        /// Writes a DATA frame.
        /// This will not utilize the padding feature currently
        /// </summary>
        private async ValueTask<object> WriteDataFrame(WriteRequest wr)
        {
            // TODO: Check whether padding flag is set and either throw, reset it or handle it
            wr.Header.Length = wr.Data.Count;
            var headerView = new ArraySegment<byte>(outBuf, 0, FrameHeader.HeaderSize);
            wr.Header.EncodeInto(headerView);

            await this.outStream.WriteAsync(headerView);
            await this.outStream.WriteAsync(wr.Data);

            return null;

            // TODO: If end of stream is set we may want to remove the stream from the
            // outgoing list afterwards
            // TODO_TODO: End of stream might also be set after other streams
        }

        /// <summary>
        /// Writes a PING frame
        /// </summary>
        private ValueTask<object> WritePingFrame(WriteRequest wr)
        {
            wr.Header.Length = 8;
            var headerView = new ArraySegment<byte>(outBuf, 0, FrameHeader.HeaderSize);
            wr.Header.EncodeInto(headerView);
            // Copy the ping payload data into the outgoing array, so we only have 1 write
            Array.Copy(wr.Data.Array, wr.Data.Offset, outBuf, FrameHeader.HeaderSize, 8);
            var totalSize = FrameHeader.HeaderSize + 8;
            var data = new ArraySegment<byte>(outBuf, 0, totalSize);

            return this.outStream.WriteAsync(data);
        }

        /// <summary>
        /// Writes a GoAway frame
        /// </summary>
        private ValueTask<object> WriteGoAwayFrame(WriteRequest wr)
        {
            var dataSize = wr.GoAwayData.RequiredSize;
            wr.Header.Length = dataSize;
            var headerView = new ArraySegment<byte>(outBuf, 0, FrameHeader.HeaderSize);
            wr.Header.EncodeInto(headerView);
            wr.GoAwayData.EncodeInto(new ArraySegment<byte>(outBuf, FrameHeader.HeaderSize, dataSize));
            var totalSize = FrameHeader.HeaderSize + dataSize;
            var data = new ArraySegment<byte>(outBuf, 0, totalSize);

            return this.outStream.WriteAsync(data);
        }

        /// <summary>
        /// Writes a RESET frame
        /// </summary>
        private ValueTask<object> WriteResetFrame(WriteRequest wr)
        {
            wr.Header.Length = ResetFrameData.Size;
            // Serialize the frame header into the outgoing buffer
            wr.Header.EncodeInto(new ArraySegment<byte>(outBuf, 0, FrameHeader.HeaderSize));
            wr.ResetFrameData.EncodeInto(new ArraySegment<byte>(outBuf, FrameHeader.HeaderSize, ResetFrameData.Size));
            var totalSize = FrameHeader.HeaderSize + ResetFrameData.Size;
            var data = new ArraySegment<byte>(outBuf, 0, totalSize);

            // Write the header
            return this.outStream.WriteAsync(data);

            // TODO: After writing a reset frame no further frame data might
            // be written for that stream
        }

        /// <summary>
        /// Writes a SETTINGS frame
        /// </summary>
        private ValueTask<object> WriteSettingsFrame(WriteRequest wr)
        {
            wr.Header.Length = wr.Data.Count;
            var headerView = new ArraySegment<byte>(outBuf, 0, FrameHeader.HeaderSize);
            wr.Header.EncodeInto(headerView);
            // Copy the settings payload data into the outgoing array, so we only have 1 write
            Array.Copy(wr.Data.Array, wr.Data.Offset, outBuf, FrameHeader.HeaderSize, wr.Data.Count);
            var totalSize = FrameHeader.HeaderSize + wr.Data.Count;
            var data = new ArraySegment<byte>(outBuf, 0, totalSize);

            return this.outStream.WriteAsync(data);
        }

        /// <summary>
        /// Writes HeaderSize.
        /// This will actually write a headers frame and possibly multiple continuation frames.
        /// This will not utilize the padding feature currently
        /// </summary>
        private async ValueTask<object> WriteHeaders(WriteRequest wr, int maxFrameSize)
        {
            var headerView = new ArraySegment<byte>(
                outBuf, 0, FrameHeader.HeaderSize);

            // Try to encode as much headers as possible into the frame
            var headers = wr.Headers;
            var nrHeaders = headers.Count;
            var isContinuation = false;

            // TODO:
            // We currently don't respect the SETTINGS_MAX_HEADER_LIST_SIZE 
            // limit for the complete header block.
            // However implementing it would only help partially, since the stream
            // would now fail on this side instead of the remote side.

            while (true)
            {
                // Encode a header block fragment
                var encodeResult = this.hEncoder.Encode(headers, maxFrameSize);
                var remaining = nrHeaders - encodeResult.FieldCount;

                FrameHeader hdr = wr.Header;
                hdr.Length = encodeResult.Bytes.Length;
                if (!isContinuation)
                {
                    hdr.Type = FrameType.Headers;
                    // Flags must be set according to whether a continuation follows
                    if (remaining == 0)
                    {
                        hdr.Flags |= (byte)HeadersFrameFlags.EndOfHeaders;
                    }
                    else
                    {
                        var f = hdr.Flags & ~((byte)HeadersFrameFlags.EndOfHeaders);
                        hdr.Flags = (byte)f;
                    }
                }
                else
                {
                    hdr.Type = FrameType.Continuation;
                    hdr.Flags = 0;
                    if (remaining == 0) hdr.Flags = (byte)ContinuationFrameFlags.EndOfHeaders;
                }

                // Serialize the frame header and write it together with the header block
                hdr.EncodeInto(headerView);
                var dataView = new ArraySegment<byte>(
                    outBuf, 0, FrameHeader.HeaderSize + encodeResult.Bytes.Length);
                await this.outStream.WriteAsync(dataView);

                nrHeaders = remaining;
                if (nrHeaders == 0)
                {
                    break;
                }
                else
                {
                    isContinuation = true;
                    // TODO: Might not be the best way to create a slice,
                    // as that might allocate without need
                    headers = headers.GetRange(encodeResult.FieldCount, nrHeaders);
                }
            }

            return null;
            // TODO: If end of stream is set we may want to remove the stream from the
            // outgoing list afterwards
        }

        private ValueTask<object> WritePushPromise(WriteRequest wr)
        {
            throw new NotSupportedException("Push promises are not supported");
        }

        private ValueTask<object> WritePriorityFrame(WriteRequest wr)
        {
            throw new NotSupportedException("Priority is not supported");
        }

        /// <summary>
        /// Registers a new stream for which frames must be transmitted at the
        /// writer.
        /// </summary>
        public async Task<bool> RegisterStream(uint streamId, int flowWindow)
        {
            await mutex.WaitAsync();
            // After close is initiated or errors happened no further streams
            // may be registered
            if (this.closed)
            {
                mutex.Release();
                return false;
            }

            var sd = new StreamData{
                StreamId = streamId,
                Window = flowWindow,
                WriteQueue = new Queue<WriteRequest>(),
                EndOfStreamQueued = false,
                ResetQueued = false,
            };
            this.Streams.Add(sd);

            mutex.Release();
            return true;
        }

        /// <summary>
        /// Updates the maximum frame size to the new value
        /// </summary>
        public async Task UpdateMaximumFrameSize(int maxFrameSize)
        {
            // It doesn't really matter if the writer is already closed.
            // We just set the value
            await mutex.WaitAsync();
            this.options.MaxFrameSize = maxFrameSize;
            mutex.Release();
        }

        /// <summary>
        /// Updates the maximum header table size
        /// </summary>
        public async Task UpdateMaximumHeaderTableSize(int newMaxHeaderTableSize)
        {
            await mutex.WaitAsync();
            
            if (this.hEncoder.DynamicTableSize <= newMaxHeaderTableSize)
            {
                // We can just keep the current setting
            }
            else
            {
                // We should lower ower header table size and send a notification
                // about that. However this this currently not supported.
                // Additionally changing the size would cause a data race, as
                // it is concurrently used by the writer process.
            }

            mutex.Release();
        }

        /// <summary>
        /// Updates the flow control window of the given stream by amount.
        /// If the streamId is 0 the window of the connection will be increased.
        /// Amount must be a positive number
        /// </summary>
        public async Task UpdateFlowControlWindow(int streamId, int amount)
        {
            await mutex.WaitAsync();
            var wakeup = false;
            
            if (streamId == 0)
            {
                if (connFlowWindow == 0) wakeup = true;
                connFlowWindow += amount; // TODO: Check for overflow
            }
            else
            {
                for (var i = 0; i < Streams.Count; i++)
                {
                    if (Streams[i].StreamId == streamId)
                    {
                        var s = Streams[i];
                        if (s.Window == 0) wakeup = true;
                        s.Window += amount; // TODO: Check for overflow
                        Streams[i] = s;
                        break;
                    }
                }
            }

            mutex.Release();

            if (wakeup)
            {
                this.wakeupWriter.Set();
            }
        }
    }
}