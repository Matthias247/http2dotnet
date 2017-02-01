using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

using Http2.Hpack;
using Http2.Internal;

namespace Http2
{
    /// <summary>
    /// The task that writes to the connection
    /// </summary>
    public class ConnectionWriter
    {
        /// <summary>
        /// Configuration options for the ConnectionWriter
        /// </summary>
        public struct Options
        {
            public int MaxFrameSize;
            public int MaxHeaderListSize;
        }

        private struct StreamData
        {
            public uint StreamId;
            public int Window;
            // TODO: Is the queue needed here?
            // As the StreamImpl API does not allow
            // writing concurrent header and data frames
            // it seems superfluos
            // It might be required if we have a client, and the client headers
            // are sent from another method which might finish only after the
            // stream is handed to the user.
            public Queue<WriteRequest> WriteQueue;
            public bool EndOfStreamQueued;
        }

        /// <summary>
        /// Signals the result of a write operation
        /// </summary>
        public enum WriteResult
        {
            /// <summary>Write is still in progress</summary>
            InProgress,
            /// <summary>Write succeded</summary>
            Success,
            /// <summary>
            /// Write failed because the write to the underlying connection failed
            /// </summary>
            ConnectionError,
            /// <summary>
            /// Write failed because the write to the underlying connection is closed
            /// </summary>
            ConnectionClosedError,
            /// <summary>Write failed because the stream was reset</summary>
            StreamResetError,
        }

        // This needs be a class in order to be mutatable
        private class WriteRequest
        {
            public FrameHeader Header;
            public WindowUpdateData WindowUpdateData;
            public ResetFrameData ResetFrameData;
            public GoAwayFrameData GoAwayData;
            public IEnumerable<HeaderField> Headers;
            public ArraySegment<byte> Data;
            public AsyncManualResetEvent Completed;
            public WriteResult Result;
        }

        private static readonly ArrayPool<byte> _pool = ArrayPool<byte>.Shared;

        /// <summary>The associated connection</summary>
        public Connection Connection { get; }
        /// <summary>The output stream this is utilizing</summary>
        IWriteAndCloseableByteStream outStream;

        /// <summary>HPack encoder</summary>
        Hpack.Encoder hEncoder;
        /// <summary>Current set of options</summary>
        Options options;

        /// <summary>Whether the writer was requested to close after completing all writes</summary>
        bool closeRequested = false;
        /// <summary>Whether CloseAsync() has already been called on the connection</summary>
        int closeConnectionIssued = 0;

        /// <summary>Outstanding writes the are associated to the connection</summary>
        Queue<WriteRequest> WriteQueue = new Queue<WriteRequest>();
        /// <summary>Streams for which data needs to be written</summary>
        List<StreamData> Streams = new List<StreamData>();

        private Object mutex = new Object();
        private AsyncManualResetEvent wakeupWriter = new AsyncManualResetEvent(false);
        private Task writeTask;

        private byte[] outBuf;

        /// <summary>Flow control window for the connection</summary>
        private int connFlowWindow = Constants.InitialConnectionWindowSize;

        /// <summary>
        /// Returns a task that will be completed when the write task finishes
        /// </summary>
        public Task Done => writeTask;

        /// <summary>
        /// Creates a new instance of the ConnectionWriter with the given options
        /// </summary>
        public ConnectionWriter(
            Connection connection, IWriteAndCloseableByteStream outStream,
            Options options, Hpack.Encoder.Options hpackOptions)
        {
            this.Connection = connection;
            this.outStream = outStream;
            this.options = options;
            this.hEncoder = new Hpack.Encoder(hpackOptions);
            // Create a buffer for outgoing data
            this.outBuf = _pool.Rent(FrameHeader.HeaderSize + options.MaxFrameSize);
            // Start the task that performs the actual writing
            this.writeTask = Task.Run(() => this.RunAsync());
        }

        /// <summary>
        /// The mainloop of the connection writer
        /// </summary>
        private async Task RunAsync()
        {
            try
            {
                // If we are a client then we have to write the preface before
                // doing anything else
                if (!Connection.IsServer)
                {
                    await ClientPreface.WriteAsync(outStream);
                    if (Connection.logger != null &&
                        Connection.logger.IsEnabled(LogLevel.Trace))
                    {
                        Connection.logger.LogTrace("send ClientPreface");
                    }
                }

                bool continueRun = true;
                while (continueRun)
                {
                    // Wait until there is something to do for us
                    await this.wakeupWriter;
                    // Fetch the next task from shared information
                    WriteRequest writeRequest = null;
                    bool doClose = false;
                    int maxFrameSize = 0;
                    lock (this.mutex)
                    {
                        writeRequest = GetNextReadyWriteRequest();
                        // Copy the maximum frame size inside the lock to avoid races
                        maxFrameSize = options.MaxFrameSize;
                        // If there's nothing to write check if we should close the connection
                        if (writeRequest == null)
                        {
                            if (this.closeRequested)
                            {
                                doClose = true;
                            }
                            else
                            {
                                // There is really no task for us.
                                // Sleep until we got one.
                                // When we loop around we will sleep in await wakeupWriter
                                this.wakeupWriter.Reset();
                            }
                        }
                    }

                    if (writeRequest != null)
                    {
                        await ProcessWriteRequestAsync(writeRequest, maxFrameSize);
                    }
                    else if (doClose)
                    {
                        // We are tasked to close the connection
                        // We simply return from the loop.
                        // Connection will be closed at the end of the task
                        continueRun = false;
                    }
                }
            }
            catch (Exception e)
            {
                // We will catch this exception if writing to the output stream
                // will fail at any point of time
                if (Connection.logger != null && Connection.logger.IsEnabled(LogLevel.Error))
                {
                    Connection.logger.LogError("Writer error: {0}", e.Message);
                }
            }

            // Close the connection if that hasn't happened yet
            // That's even necessary if the an error happened
            await CloseNow(false);

            // Set the closeRequested flag which will avoid new write items to be added
            // In normal close procedure this will already have been set.
            // But in the case the writer does through an exception it's necessary
            lock (mutex)
            {
                closeRequested = true;
                // Fail pending writes that are still queued up
                FinishAllOutstandingWritesLocked();
            }

            // Return buffer to the pool.
            // As all writes are completed we no longer need it.
            _pool.Return(this.outBuf);
            this.outBuf = null;
        }

        /// <summary>
        /// Forces closing the connection immediatly.
        /// Pending write tasks will fail.
        /// </summary>
        private async ValueTask<object> CloseNow(bool needWakeup)
        {
            if (Interlocked.CompareExchange(ref closeConnectionIssued, 1, 0) == 0)
            {
                try
                {
                    await outStream.CloseAsync();
                }
                catch (Exception)
                {
                    // There's not really something meaningfull we can do here
                }

                // If the writer is blocked (waits on wakeupWriter) we need to wake
                // it up. Otherwise it wouldn't notice that the connection is dead
                // We also set closeRequested, because otherwise if it's not set and
                // there is no outstanding write request the writer would go to sleep
                // again.
                // If there is a write request the writer will not close immediatly
                // but try to write that request. Won't matter if the writes fails,
                // since it's intended to get out of the main working loop - with or
                // without an exception.
                if (needWakeup)
                {
                    lock (mutex)
                    {
                        this.closeRequested = true;
                    }
                    wakeupWriter.Set();
                }
            }
            return null;
        }

        /// <summary>
        /// Forces closing the connection immediatly.
        /// Pending write tasks will fail.
        /// </summary>
        public ValueTask<object> CloseNow()
        {
            // This API is called from outside of the write task
            // This means the task might be blocked waiting for a need write
            // request and must be woken up.
            return CloseNow(true);
        }

        /// <summary>
        /// Retrieves the next write request from the internal queues
        /// This may only be called within the lock
        /// </summary>
        private WriteRequest GetNextReadyWriteRequest()
        {
            // Look if there are writes necessary for the connection queue
            // Connection related writes are prioritized against
            // stream related writes
            if (this.WriteQueue.Count > 0)
            {
                var writeRequest = this.WriteQueue.Dequeue();
                return writeRequest;
            }

            // Look if any of the streams is writeable
            // This logic is quite primitive at the moment and won't be fair to
            // higher stream numbers. However the flow control windows will avoid
            // total starvation for those
            for (var i = 0; i < Streams.Count; i++)
            {
                var s = Streams[i];
                if (s.WriteQueue.Count == 0) continue;
                var first = s.WriteQueue.Peek();
                // If it's not a data frame we can always write it
                // Otherwise me must check the flow control window
                if (first.Header.Type == FrameType.Data)
                {
                    // Check how much flow we have
                    var canSend = Math.Min(this.connFlowWindow, s.Window);
                    // And also respect the maximum frame size
                    // As we don't use padding we can use the full frame
                    canSend = Math.Min(canSend, options.MaxFrameSize);
                    // If flow control window is empty check the next stream
                    // However empty data frames can be sent without a window
                    if (canSend == 0 && first.Data.Count != 0) continue;

                    // Adjust the flow control windows by what we are able to write
                    var toSend = Math.Min(canSend, first.Data.Count);
                    connFlowWindow -= toSend;
                    s.Window -= toSend;
                    Streams[i] = s;

                    if (Connection.logger != null &&
                        Connection.logger.IsEnabled(LogLevel.Trace))
                    {
                        Connection.logger.LogTrace(
                            "Outgoing flow control window update:\n" +
                            "  Connection window: {0} -> {1}\n" +
                            "  Stream {2} window: {2} -> {3}",
                            connFlowWindow + toSend, connFlowWindow,
                            s.StreamId, s.Window + toSend, s.Window);
                    }

                    if (canSend < first.Data.Count)
                    {
                        // We can write a part of the request
                        // In order to write the complete request we have to segment it
                        // The DATA frame will stay queued, but we will create an
                        // additional WriteRequest which handles writing the first
                        // part of it. The queued WriteRequest gets modified to
                        // handle the remaining part
                        var we = AllocateWriteRequest();
                        we.Header = first.Header;
                        // Reset a potential EndOfStream flag
                        // The part that we can't send now might be the end of
                        // the stream. This isn't it.
                        we.Header.Flags = 0;
                        we.Data = new ArraySegment<byte>(
                            first.Data.Array, first.Data.Offset, canSend);
                        // Adjust the amount of bytes that have to be written later on
                        var oldData = first.Data;
                        first.Data = new ArraySegment<byte>(
                            oldData.Array, oldData.Offset + canSend, oldData.Count - canSend);
                        return we;
                    }
                }
                // We have not continued, so we can write this item
                first = s.WriteQueue.Dequeue();
                // Determine whether we can remove the stream entry
                // If this is the end of a stream or a reset frame no other frames
                // will follow and we can remove it.
                // Window update frames are send through the generic queue
                if (first.Header.Type == FrameType.ResetStream || first.Header.HasEndOfStreamFlag)
                {
                    Streams.RemoveAt(i);
                    // Make sure that the queue inside this stream
                    // does not contain anything after the written element.
                    // This would be a contract violation and StreamImpl should be checked.
                    if (s.WriteQueue.Count > 0)
                    {
                        throw new Exception(
                            "Unexpected: WriteQueue for stream contains data after EndOfStream");
                    }
                }
                return first;
            }

            return null;
        }

        /// <summary>
        /// Logs the outgoing frames header
        /// </summary>
        private void LogOutgoingFrameHeader(FrameHeader fh)
        {
            if (Connection.logger != null && Connection.logger.IsEnabled(LogLevel.Trace))
            {
                Connection.logger.LogTrace(
                    "send " + FramePrinter.PrintFrameHeader(fh));
            }
        }

        private async ValueTask<object> ProcessWriteRequestAsync(
            WriteRequest wr, int maxFrameSize)
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
                        await WriteHeadersAsync(wr, maxFrameSize);
                        break;
                    case FrameType.PushPromise:
                        await WritePushPromiseAsync(wr);
                        break;
                    case FrameType.Data:
                        await WriteDataFrameAsync(wr);
                        break;
                    case FrameType.GoAway:
                        await WriteGoAwayFrameAsync(wr);
                        break;
                    case FrameType.Continuation:
                        throw new Exception("Continuations may not be directly queued");
                    case FrameType.Ping:
                        await WritePingFrameAsync(wr);
                        break;
                    case FrameType.Priority:
                        await WritePriorityFrameAsync(wr);
                        break;
                    case FrameType.ResetStream:
                        await WriteResetFrameAsync(wr);
                        break;
                    case FrameType.WindowUpdate:
                        await WriteWindowUpdateFrame(wr);
                        break;
                    case FrameType.Settings:
                        await WriteSettingsFrameAsync(wr);
                        break;
                    default:
                        throw new Exception("Unknown frame type");
                }
                wr.Result = WriteResult.Success;
            }
            catch (Exception)
            {
                wr.Result = WriteResult.ConnectionError;
                throw;
            }
            finally
            {
                // If this was the end of a stream then we don't need the stream
                // data anymore
                if (wr.Header.HasEndOfStreamFlag)
                {
                    lock (mutex)
                    {
                        RemoveStreamLocked(wr.Header.StreamId);
                    }
                }

                wr.Completed.Set();
            }
            return null;
        }

        private ValueTask<object> WriteWindowUpdateFrame(WriteRequest wr)
        {
            wr.Header.Length = WindowUpdateData.Size;
            LogOutgoingFrameHeader(wr.Header);
            // Serialize the frame header into the outgoing buffer
            wr.Header.EncodeInto(
                new ArraySegment<byte>(outBuf, 0, FrameHeader.HeaderSize));
            // Serialize the window update data
            wr.WindowUpdateData.EncodeInto(new ArraySegment<byte>(
                outBuf, FrameHeader.HeaderSize, WindowUpdateData.Size));
            var totalSize = FrameHeader.HeaderSize + WindowUpdateData.Size;
            var data = new ArraySegment<byte>(outBuf, 0, totalSize);

            // Write the header
            return this.outStream.WriteAsync(data);
        }

        private bool TryEnqueueWriteRequest(uint streamId, WriteRequest wr)
        {
            // Put frames for the connection(streamId 0)
            // as well as reset and window update frames in the main outgoing queue
            if (streamId == 0 ||
                wr.Header.Type == FrameType.WindowUpdate ||
                wr.Header.Type == FrameType.ResetStream)
            {
                this.WriteQueue.Enqueue(wr);
                // Possible improvement for resets: Check if a reset for that stream
                // ID is already queued.
                // If we queue a reset frame for a stream we don't need
                // the writequeue for it anymore. Any queued up frames for that
                // stream may proceed as written.
                if (wr.Header.Type == FrameType.ResetStream && streamId != 0)
                {
                    this.RemoveStreamLocked(streamId);
                }

                return true;
            }

            // All other frame types belong in the queue for the associated stream
            for (var i = 0; i < Streams.Count; i++)
            {
                var stream = Streams[i];
                if (stream.StreamId == streamId)
                {
                    // If a reset or end of stream is already queued
                    // we are not allowed to queue any additional item,
                    // as that would cause the receiver to receive a headers
                    // or data frame for an already reset stream.
                    // However in case a reset was queued the stream will
                    // already be removed and we won't end up in here.
                    if (wr.Header.HasEndOfStreamFlag)
                    {
                        stream.EndOfStreamQueued = true;
                        Streams[i] = stream;
                    }
                    stream.WriteQueue.Enqueue(wr);
                    return true;
                }
            }

            // The stream was not found
            return false;
        }

        /// <summary>A pool of WriteRequest structures for reuse</summary>
        private static readonly ConcurrentBag<WriteRequest> writeRequestPool =
            new ConcurrentBag<WriteRequest>();
        /// <summary>Max amount of pooled requests</summary>
        private const int MaxPooledWriteRequests = 10*1024;

        /// <summary>
        /// Allocates a new WriteRequest structure.
        /// This could be delegated to a connection-local
        /// or global pool in future.
        /// </summary>
        /// <returns></returns>
        private WriteRequest AllocateWriteRequest()
        {
            WriteRequest r;
            if (writeRequestPool.TryTake(out r))
            {
                return r;
            }

            var wr = new WriteRequest()
            {
                Completed = new AsyncManualResetEvent(false),
            };
            return wr;
        }

        /// <summary>
        /// Releases a WriteRequest structure.
        /// After releasing it it may be reused for another write.
        /// </summary>
        /// <param name="wr">The WriteRequest to release</param>
        private void ReleaseWriteRequest(WriteRequest wr)
        {
            wr.Result = WriteResult.InProgress;
            wr.Headers = null;
            // Reset all contained byte arrays so that we leak no
            // data if a pool allocator is used.
            wr.Data = Constants.EmptyByteArray;
            wr.GoAwayData.Reason.DebugData = Constants.EmptyByteArray;
            wr.Completed.Reset();

            // It would for sure be better if the ConcurrentBag had some kind of
            // PutIfCountLessThan method
            if (writeRequestPool.Count < MaxPooledWriteRequests)
            {
                writeRequestPool.Add(wr);
            }
            // in other situations the WriteRequest just gets garbage collected
        }

        private async ValueTask<WriteResult> PerformWriteRequestAsync(
            uint streamId, Action<WriteRequest> populateRequest, bool closeAfterwards)
        {
            WriteRequest wr = null;
            lock (mutex)
            {
                if (closeRequested)
                {
                    return WriteResult.ConnectionClosedError;
                }
                if (closeAfterwards)
                {
                    closeRequested = true;
                }

                wr = AllocateWriteRequest();
                populateRequest(wr);

                var enqueued = TryEnqueueWriteRequest(streamId, wr);
                if (!enqueued)
                {
                    return WriteResult.StreamResetError;
                }
            }

            // Wakeup the task
            // TODO: We might wakeup the task only if we also have a flow control
            // window. However that isn't currently reported by TryEnqueueWriteRequest
            // and would only be a minor optimization
            wakeupWriter.Set();
            // Wait until the request was written
            await wr.Completed;
            // Copy the result before releasing the writeRequest
            // After ReleaseWriteRequest wr will be invalid
            var result = wr.Result;
            ReleaseWriteRequest(wr);

            if (result == WriteResult.InProgress)
            {
                throw new Exception(
                    "Unexpected: Write is still marked as in progress");
            }

            return result;
        }

        public ValueTask<WriteResult> WriteHeaders(
            FrameHeader header, IEnumerable<HeaderField> headers)
        {
            return PerformWriteRequestAsync(
                header.StreamId,
                wr => {
                    wr.Header = header;
                    wr.Headers = headers;
                },
                false);
        }

        public ValueTask<WriteResult> WriteSettings(
            FrameHeader header, ArraySegment<byte> data)
        {
            return PerformWriteRequestAsync(
                0,
                wr => {
                    wr.Header = header;
                    wr.Data = data;
                },
                false);
        }

        public ValueTask<WriteResult> WriteResetStream(
            FrameHeader header, ResetFrameData data)
        {
            return PerformWriteRequestAsync(
                header.StreamId,
                wr => {
                    wr.Header = header;
                    wr.ResetFrameData = data;
                },
                false);
        }

        public ValueTask<WriteResult> WritePing(
            FrameHeader header, ArraySegment<byte> data)
        {
            return PerformWriteRequestAsync(
                0,
                wr => {
                    wr.Header = header;
                    wr.Data = data;
                },
                false);
        }

        public ValueTask<WriteResult> WriteWindowUpdate(
            FrameHeader header, WindowUpdateData data)
        {
            return PerformWriteRequestAsync(
                header.StreamId,
                wr => {
                    wr.Header = header;
                    wr.WindowUpdateData = data;
                },
                false);
        }

        public ValueTask<WriteResult> WriteGoAway(
            FrameHeader header, GoAwayFrameData data, bool closeAfterwards)
        {
            return PerformWriteRequestAsync(
                0,
                wr => {
                    wr.Header = header;
                    wr.GoAwayData = data;
                },
                closeAfterwards);
        }

        public ValueTask<WriteResult> WriteData(
            FrameHeader header, ArraySegment<byte> data)
        {
            return PerformWriteRequestAsync(
                header.StreamId,
                wr => {
                    wr.Header = header;
                    wr.Data = data;
                },
                false);
        }

        /// <summary>
        /// Writes a DATA frame.
        /// This will not utilize the padding feature currently
        /// </summary>
        private async ValueTask<object> WriteDataFrameAsync(WriteRequest wr)
        {
            // Reset the padding flag. Padding is not supported
            wr.Header.Flags = (byte)((wr.Header.Flags & ~((uint)DataFrameFlags.Padded)) & 0xFF);
            wr.Header.Length = wr.Data.Count;

            LogOutgoingFrameHeader(wr.Header);

            var headerView = new ArraySegment<byte>(outBuf, 0, FrameHeader.HeaderSize);
            wr.Header.EncodeInto(headerView);

            await this.outStream.WriteAsync(headerView);
            await this.outStream.WriteAsync(wr.Data);

            return null;
        }

        /// <summary>
        /// Writes a PING frame
        /// </summary>
        private ValueTask<object> WritePingFrameAsync(WriteRequest wr)
        {
            wr.Header.Length = 8;

            LogOutgoingFrameHeader(wr.Header);

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
        private ValueTask<object> WriteGoAwayFrameAsync(WriteRequest wr)
        {
            var dataSize = wr.GoAwayData.RequiredSize;
            wr.Header.Length = dataSize;

            LogOutgoingFrameHeader(wr.Header);

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
        private ValueTask<object> WriteResetFrameAsync(WriteRequest wr)
        {
            wr.Header.Length = ResetFrameData.Size;
            LogOutgoingFrameHeader(wr.Header);
            // Serialize the frame header into the outgoing buffer
            wr.Header.EncodeInto(new ArraySegment<byte>(outBuf, 0, FrameHeader.HeaderSize));
            wr.ResetFrameData.EncodeInto(new ArraySegment<byte>(outBuf, FrameHeader.HeaderSize, ResetFrameData.Size));
            var totalSize = FrameHeader.HeaderSize + ResetFrameData.Size;
            var data = new ArraySegment<byte>(outBuf, 0, totalSize);

            // Write the header
            return this.outStream.WriteAsync(data);
        }

        /// <summary>
        /// Writes a SETTINGS frame
        /// </summary>
        private ValueTask<object> WriteSettingsFrameAsync(WriteRequest wr)
        {
            wr.Header.Length = wr.Data.Count;
            LogOutgoingFrameHeader(wr.Header);
            var headerView = new ArraySegment<byte>(outBuf, 0, FrameHeader.HeaderSize);
            wr.Header.EncodeInto(headerView);
            // Copy the settings payload data into the outgoing array, so we only have 1 write
            // The size check is omitted here since the size of outBuf will always exceed
            // the size of settings that we write.
            // The minimal value for outBuf/maxFrameSize is 16kB.
            // Settings are only 6 * 6 byte currently.
            Array.Copy(wr.Data.Array, wr.Data.Offset, outBuf, FrameHeader.HeaderSize, wr.Data.Count);
            var totalSize = FrameHeader.HeaderSize + wr.Data.Count;
            var data = new ArraySegment<byte>(outBuf, 0, totalSize);

            return this.outStream.WriteAsync(data);
        }

        /// <summary>
        /// Writes a full header block.
        /// This will actually write a headers frame and possibly
        /// multiple continuation frames.
        /// This will not utilize the padding feature currently
        /// </summary>
        private async ValueTask<object> WriteHeadersAsync(
            WriteRequest wr, int maxFrameSize)
        {
            // Limit the maximum frame size also to the limit of the output
            // buffer.
            // If the remote increases the size through SETTINGS update we might
            // try to write bigger frames, which would however need a bigger
            // output buffer. Instead of allowing this and reallocating the buffer
            // we keep the old buffer and use it's max size for headers.
            maxFrameSize = Math.Min(
                maxFrameSize,
                outBuf.Length - FrameHeader.HeaderSize);

            var headerView = new ArraySegment<byte>(
                outBuf, 0, FrameHeader.HeaderSize);

            // Try to encode as much headers as possible into the frame
            var headers = wr.Headers;
            var nrTotalHeaders = headers.Count();
            var sentHeaders = 0;
            var isContinuation = false;

            // TODO:
            // We currently don't respect the SETTINGS_MAX_HEADER_LIST_SIZE
            // limit for the complete header block.
            // However implementing it would only help partially, since the stream
            // would now fail on this side instead of the remote side.

            while (true)
            {
                // Encode a header block fragment into the output buffer
                var headerBlockFragment = new ArraySegment<byte>(
                    outBuf, FrameHeader.HeaderSize, maxFrameSize);
                var encodeResult = this.hEncoder.EncodeInto(
                    headerBlockFragment, headers);

                // If not a single header was encoded but there are headers remaining
                // to be sent this is an error.
                // It means the output buffer is not large enough to accomodate
                // a single header field.
                // Retrying it in a continuation frame
                // - is not valid since it's not allowed to send an empty fragment
                // - won't to better, since buffer size is the same
                if (encodeResult.FieldCount == 0 && (nrTotalHeaders - sentHeaders) != 0)
                {
                    // Sending should be stopped and an error should be reported
                    // to the sending application.
                    // TODO: There's an open question how to do this gracefully
                    // If the too large header field is encountered inside the
                    // continuation frame the transmission of headers is already
                    // in progress and can't be stopped.
                    // So as an intermediate measure kill the connection in this
                    // case by throwing an exception.
                    throw new Exception(
                        "Encountered too large HeaderField which can't be encoded " +
                        "in a HTTP2 frame. Closing connection");
                }

                sentHeaders += encodeResult.FieldCount;
                var remaining = nrTotalHeaders - sentHeaders;

                FrameHeader hdr = wr.Header;
                hdr.Length = encodeResult.UsedBytes;
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
                    if (remaining == 0)
                    {
                        hdr.Flags = (byte)ContinuationFrameFlags.EndOfHeaders;
                    }
                }

                // Log the complete header
                LogOutgoingFrameHeader(hdr);
                // Serialize the frame header and write it together with the header block
                hdr.EncodeInto(headerView);
                var dataView = new ArraySegment<byte>(
                    outBuf, 0, FrameHeader.HeaderSize + encodeResult.UsedBytes);
                await this.outStream.WriteAsync(dataView);

                if (remaining == 0)
                {
                    break;
                }
                else
                {
                    isContinuation = true;
                    // TODO: This might not be the best way to create a slice,
                    // as that might allocate without need. However it works.
                    headers = wr.Headers.Skip(sentHeaders);
                }
            }

            return null;
        }

        private ValueTask<object> WritePushPromiseAsync(WriteRequest wr)
        {
            LogOutgoingFrameHeader(wr.Header);
            throw new NotSupportedException("Push promises are not supported");
        }

        private ValueTask<object> WritePriorityFrameAsync(WriteRequest wr)
        {
            LogOutgoingFrameHeader(wr.Header);
            throw new NotSupportedException("Priority is not supported");
        }

        /// <summary>
        /// Registers a new stream for which frames must be transmitted at the
        /// writer.
        /// </summary>
        /// <returns>
        /// True if the stream could be succesfully regiestered.
        /// False otherwise.
        /// </returns>
        public bool RegisterStream(uint streamId, int flowWindow)
        {
            lock (mutex)
            {
                // After close is initiated or errors happened no further streams
                // may be registered
                if (this.closeRequested)
                {
                    return false;
                }

                var sd = new StreamData{
                    StreamId = streamId,
                    Window = flowWindow,
                    WriteQueue = new Queue<WriteRequest>(),
                    EndOfStreamQueued = false,
                };
                this.Streams.Add(sd);
            }

            return true;
        }

        /// <summary>
        /// Removes the stream with the given stream ID from the writer.
        /// Pending writes will be canceled.
        /// This does not cancel any pending WindowUpdate or ResetStream writes
        /// </summary>
        public void RemoveStream(uint streamId)
        {
            lock (mutex)
            {
                RemoveStreamLocked(streamId);
            }
        }

        private void RemoveStreamLocked(uint streamId)
        {
            Queue<WriteRequest> writeQueue = null;
            for (var i = 0; i < Streams.Count; i++)
            {
                var s = Streams[i];
                if (s.StreamId == streamId)
                {
                    writeQueue = s.WriteQueue;
                    Streams.RemoveAt(i);
                    break;
                }
            }

            if (writeQueue != null)
            {
                // Signal all queued up writes as finished with an ResetError
                foreach (var elem in writeQueue)
                {
                    elem.Result = WriteResult.StreamResetError;
                    elem.Completed.Set();
                }
            }
        }

        private void FinishAllOutstandingWritesLocked()
        {
            var streamsMap = this.Streams;
            foreach (var stream in Streams)
            {
                // Signal all queued up writes as finished with an ResetError
                foreach (var elem in stream.WriteQueue)
                {
                    elem.Result = WriteResult.StreamResetError;
                    elem.Completed.Set();
                }
                stream.WriteQueue.Clear();
            }
            streamsMap.Clear();

            foreach (var elem in WriteQueue)
            {
                elem.Result = WriteResult.StreamResetError;
                elem.Completed.Set();
            }
            WriteQueue.Clear();
        }

        /// <summary>
        /// Instruct the writer to user the new settings that the remote required.
        /// </summary>
        public void UpdateSettings(Settings remoteSettings)
        {
            // It doesn't really matter if the writer is already closed.
            // We just set the value
            lock (mutex)
            {
                // Update the maximum frame size
                // Remark: The cast is valid, since the settings are validated before
                // and the max MaxFrameSize fits into an integer.
                // Remark 2: In order to send bigger HEADERS/CONTINUATION frames
                // we would also need to update the size of the output buffer.
                // This is not done here - instead the size of these frames
                // will still be clamped to the max output buffer size in the
                // respective routine. However the output buffer size might be
                // bigger than the initally set maxFrameSize, as the pool allocator
                // is able to return a bigger size. In that case the bigger size
                // will be utilized up to maxFrameSize.
                this.options.MaxFrameSize = (int)remoteSettings.MaxFrameSize;

                // remoteSettings.MaxHeaderListSize is currently not used

                // Update the maximum HPack table size
                if (this.hEncoder.DynamicTableSize <= remoteSettings.HeaderTableSize)
                {
                    // We can just keep the current setting
                    // There's no need to use a bigger setting, it's just an
                    // option that is granted to us from the remote.
                }
                else
                {
                    // We should lower our header table size and send a notification
                    // about that. However this this currently not supported.
                    // Additionally changing the size would cause a data race, as
                    // it is concurrently used by the writer process.
                    // We can continue the old settings and hope that the remote
                    // won't reset the connection. This will only affect compatibility
                    // with systems that use and enforce a header table size lower
                    // than the default one.
                    // TODO: Support also this case
                }
            }
        }

        /// <summary>
        /// Updates the flow control window of the given stream by amount.
        /// If the streamId is 0 the window of the connection will be increased.
        /// Amount must be a positive number greater than 0.
        /// </summary>
        /// <returns>
        /// Returns true if the flow control window for the stream could be updated
        /// or if the stream does not exist.
        /// Returns false in cases where an overflow error for the flow control
        /// window occurs.
        /// </returns>
        public Http2Error? UpdateFlowControlWindow(uint streamId, int amount)
        {
            var wakeup = false;

            // Negative or zero flow control window updates are not valid
            if (amount < 1)
            {
                return new Http2Error
                {
                    Code = ErrorCode.ProtocolError,
                    StreamId = streamId,
                    Message = "Received an invalid flow control window update",
                };
            }

            lock (mutex)
            {
                if (streamId == 0)
                {
                    // Check for overflow
                    var maxIncrease = int.MaxValue - connFlowWindow;
                    if (amount > maxIncrease)
                    {
                        return new Http2Error
                        {
                            StreamId = 0,
                            Code = ErrorCode.FlowControlError,
                            Message = "Flow control window overflow",
                        };
                    }
                    // Increase connection flow control value
                    if (Connection.logger != null &&
                        Connection.logger.IsEnabled(LogLevel.Trace))
                    {
                        Connection.logger.LogTrace(
                            "Outgoing flow control window update:\n" +
                            "  Connection window: {0} -> {1}",
                            connFlowWindow, connFlowWindow + amount);
                    }
                    if (connFlowWindow == 0) wakeup = true;
                    connFlowWindow += amount;
                }
                else
                {
                    for (var i = 0; i < Streams.Count; i++)
                    {
                        if (Streams[i].StreamId == streamId)
                        {
                            var s = Streams[i];
                            // Check for overflow
                            var maxIncrease = int.MaxValue - s.Window;
                            if (amount > maxIncrease)
                            {
                                return new Http2Error
                                {
                                    Code = ErrorCode.FlowControlError,
                                    StreamId = streamId,
                                    Message = "Flow control window overflow",
                                };
                            }
                            // Increase stream flow control value
                            if (Connection.logger != null &&
                                Connection.logger.IsEnabled(LogLevel.Trace))
                            {
                                Connection.logger.LogTrace(
                                    "Outgoing flow control window update:\n" +
                                    "  Stream {0} window: {1} -> {2}",
                                    streamId, s.Window, s.Window + amount);
                            }
                            if (s.Window == 0 && s.WriteQueue.Count > 0) wakeup = true;
                            s.Window += amount;
                            Streams[i] = s;
                            break;
                        }
                    }
                }
            }

            if (wakeup)
            {
                this.wakeupWriter.Set();
            }
            return null;
        }
    }
}