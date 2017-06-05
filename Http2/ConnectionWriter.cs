using System;
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
    internal class ConnectionWriter
    {
        /// <summary>
        /// Configuration options for the ConnectionWriter
        /// </summary>
        public struct Options
        {
            public int MaxFrameSize;
            public int MaxHeaderListSize;
            public int DynamicTableSizeLimit;
            public int InitialWindowSize;
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

        private struct SharedData
        {
            /// <summary>Guards the SharedData</summary>
            public object Mutex;
            /// <summary>
            /// The currently set initial window size as indicated by the remote
            /// settings.
            /// </summary>
            public int InitialWindowSize;
            /// <summary>Outstanding writes the are associated to the connection</summary>
            public Queue<WriteRequest> WriteQueue;
            /// <summary>Streams for which data needs to be written</summary>
            public List<StreamData> Streams;
            /// <summary>
            /// If this is not null the writer should apply the new settings
            /// and send a settings ACK.
            /// </summary>
            public ChangeSettingsRequest ChangeSettingsRequest;
            /// <summary>Whether the writer was requested to close after completing all writes</summary>
            public bool CloseRequested;
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

        private class ChangeSettingsRequest
        {
            public Settings NewRemoteSettings;
            public AsyncManualResetEvent Completed;
            public Http2Error? Result;
        }

        /// <summary>The associated connection</summary>
        public Connection Connection { get; }
        /// <summary>The output stream this is utilizing</summary>
        private readonly IWriteAndCloseableByteStream outStream;

        /// <summary>HPack encoder</summary>
        private readonly Hpack.Encoder hEncoder;

        /// <summary>
        /// Whether CloseAsync() has already been called on the connection.
        /// This variable is atomic (utilized with Interlocked operations).
        /// </summary>
        private int closeConnectionIssued = 0;

        /// <summary>
        /// Contains all data which is shared and protected between threads.
        /// </summary>
        private SharedData shared = new SharedData();
        private AsyncManualResetEvent wakeupWriter = new AsyncManualResetEvent(false);
        private Task writeTask;

        private byte[] outBuf;

        /// <summary>
        /// The maximum dynamictable size that should be used for header encoding
        /// </summary>
        private readonly int DynamicTableSizeLimit;

        // Current settings, which are only updated inside the write task and
        // therefore need no protection:

        /// <summary>Flow control window for the connection</summary>
        private int connFlowWindow = Constants.InitialConnectionWindowSize;
        /// <summary>The maximum frame size that should be used</summary>
        private int MaxFrameSize;
        /// <summary>The maximum amount of headerlist bytes that should be sent</summary>
        private int MaxHeaderListSize;

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
            this.DynamicTableSizeLimit = options.DynamicTableSizeLimit;
            this.MaxFrameSize = options.MaxFrameSize;
            this.MaxHeaderListSize = options.MaxHeaderListSize;
            this.hEncoder = new Hpack.Encoder(hpackOptions);

            // Initialize shared data
            shared.Mutex = new object();
            shared.CloseRequested = false;
            shared.InitialWindowSize = options.InitialWindowSize;
            shared.WriteQueue = new Queue<WriteRequest>();
            shared.Streams = new List<StreamData>();

            // Start the task that performs the actual writing
            this.writeTask = Task.Run(() => this.RunAsync());
        }

        internal void EnsureBuffer(int minSize)
        {
            if (outBuf != null)
            {
                if (outBuf.Length >= minSize) return;
                // Need a bigger buffer - return the old one
                Connection.config.BufferPool.Return(outBuf);
                outBuf = null;
            }

            outBuf = Connection.config.BufferPool.Rent(minSize);
        }

        internal void ReleaseBuffer(int treshold)
        {
            if (outBuf == null || outBuf.Length <= treshold) return;
            Connection.config.BufferPool.Return(outBuf);
            outBuf = null;
        }

        /// <summary>
        /// The mainloop of the connection writer
        /// </summary>
        private async Task RunAsync()
        {
            try
            {
                EnsureBuffer(Connection.PersistentBufferSize);

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

                // We always need to send the initial settings before
                // anything else. This is an extra step to avoid a race condition
                // with any other write or settings change request.
                // As the settings are not changeable we can directly take them
                // from the connection here. If they could be changed at some point
                // of time then this would need to change too.
                await WriteSettingsAsync(Connection.localSettings);

                bool continueRun = true;
                while (continueRun)
                {
                    // Wait until there is something to do for us
                    await this.wakeupWriter;
                    // Fetch the next task from shared information
                    WriteRequest writeRequest = null;
                    ChangeSettingsRequest changeSettings = null;
                    bool doClose = false;

                    lock (shared.Mutex)
                    {
                        // Do one of multiple possible actions
                        // Prio 1: Apply new settings
                        // Prio 2: Write a frame
                        // Prio 3: Close the connection if requested
                        if (shared.ChangeSettingsRequest != null)
                        {
                            changeSettings = shared.ChangeSettingsRequest;
                            shared.ChangeSettingsRequest = null;
                        }
                        else
                        {
                            writeRequest = GetNextReadyWriteRequest();
                        }
                        // If there's nothing to write check if we should close the connection
                        if (changeSettings == null && writeRequest == null)
                        {
                            if (shared.CloseRequested)
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

                    if (changeSettings != null)
                    {
                        var err = ApplyNewSettings(
                            changeSettings.NewRemoteSettings);
                        if (err == null)
                        {
                            // TODO: Probably the ACK should be written in all
                            // cases.
                            EnsureBuffer(Connection.PersistentBufferSize);
                            await WriteSettingsAckAsync();
                        }
                        changeSettings.Result = err;
                        changeSettings.Completed.Set();
                    }
                    else if (writeRequest != null)
                    {
                        EnsureBuffer(Connection.PersistentBufferSize);
                        await ProcessWriteRequestAsync(writeRequest);
                        ReleaseBuffer(Connection.PersistentBufferSize);
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
                if (Connection.logger != null &&
                    Connection.logger.IsEnabled(LogLevel.Error))
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
            lock (shared.Mutex)
            {
                shared.CloseRequested = true;
                // Complete a settings update that might still be enqueued
                if (shared.ChangeSettingsRequest != null)
                {
                    shared.ChangeSettingsRequest.Completed.Set();
                    shared.ChangeSettingsRequest = null;
                }
                // Fail pending writes that are still queued up
                FinishAllOutstandingWritesLocked();
            }

            // Return buffer to the pool.
            // As all writes are completed we no longer need it.
            if (outBuf != null)
            {
                Connection.config.BufferPool.Return(outBuf);
                outBuf = null;
            }
        }

        /// <summary>
        /// Forces closing the connection immediatly.
        /// Pending write tasks will fail.
        /// </summary>
        private async ValueTask<DoneHandle> CloseNow(bool needWakeup)
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
                    lock (shared.Mutex)
                    {
                        shared.CloseRequested = true;
                    }
                    wakeupWriter.Set();
                }
            }

            return DoneHandle.Instance;
        }

        /// <summary>
        /// Forces closing the connection immediatly.
        /// Pending write tasks will fail.
        /// </summary>
        public ValueTask<DoneHandle> CloseNow()
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
            if (shared.WriteQueue.Count > 0)
            {
                var writeRequest = shared.WriteQueue.Dequeue();
                return writeRequest;
            }

            // Look if any of the streams is writeable
            // This logic is quite primitive at the moment and won't be fair to
            // higher stream numbers. However the flow control windows will avoid
            // total starvation for those
            for (var i = 0; i < shared.Streams.Count; i++)
            {
                var s = shared.Streams[i];
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
                    canSend = Math.Min(canSend, MaxFrameSize);
                    // If flow control window is empty check the next stream
                    // However empty data frames can be sent without a window
                    // TODO: Check if it's allowed to send 0 byte data frames
                    // in case of a negative flow control window.
                    if (canSend <= 0 && first.Data.Count != 0) continue;

                    // Adjust the flow control windows by what we are able to write
                    var toSend = Math.Min(canSend, first.Data.Count);
                    connFlowWindow -= toSend;
                    s.Window -= toSend;
                    shared.Streams[i] = s;

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
                    shared.Streams.RemoveAt(i);
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
            if (Connection.logger != null &&
                Connection.logger.IsEnabled(LogLevel.Trace))
            {
                Connection.logger.LogTrace(
                    "send " + FramePrinter.PrintFrameHeader(fh));
            }
        }

        private async Task ProcessWriteRequestAsync(WriteRequest wr)
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
                        await WriteHeadersAsync(wr);
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
                        throw new Exception("Settings changes are not supported");
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
                    lock (shared.Mutex)
                    {
                        RemoveStreamLocked(wr.Header.StreamId);
                    }
                }

                wr.Completed.Set();
            }
        }

        private Task WriteWindowUpdateFrame(WriteRequest wr)
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

        private bool TryEnqueueWriteRequestLocked(uint streamId, WriteRequest wr)
        {
            // Put frames for the connection(streamId 0)
            // as well as reset and window update frames in the main outgoing queue
            if (streamId == 0 ||
                wr.Header.Type == FrameType.WindowUpdate ||
                wr.Header.Type == FrameType.ResetStream)
            {
                shared.WriteQueue.Enqueue(wr);
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
            for (var i = 0; i < shared.Streams.Count; i++)
            {
                var stream = shared.Streams[i];
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
                        shared.Streams[i] = stream;
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
            lock (shared.Mutex)
            {
                if (shared.CloseRequested)
                {
                    return WriteResult.ConnectionClosedError;
                }
                if (closeAfterwards)
                {
                    shared.CloseRequested = true;
                }

                wr = AllocateWriteRequest();
                populateRequest(wr);

                var enqueued = TryEnqueueWriteRequestLocked(streamId, wr);
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

        [Obsolete("Updating and writing settings is not supported")]
        public ValueTask<WriteResult> WriteSettings(
            FrameHeader header, ArraySegment<byte> data)
        {
            // This is here as a reminder.
            // If settings changes get implemented then the direct writing of
            // Connection.localSettings at the startup of the reader is no longer
            // valid.
            throw new NotSupportedException();
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
        private async Task WriteDataFrameAsync(WriteRequest wr)
        {
            // Reset the padding flag. Padding is not supported
            wr.Header.Flags = (byte)((wr.Header.Flags & ~((uint)DataFrameFlags.Padded)) & 0xFF);
            wr.Header.Length = wr.Data.Count;

            LogOutgoingFrameHeader(wr.Header);

            var headerView = new ArraySegment<byte>(outBuf, 0, FrameHeader.HeaderSize);
            wr.Header.EncodeInto(headerView);

            await this.outStream.WriteAsync(headerView);
            await this.outStream.WriteAsync(wr.Data);
        }

        /// <summary>
        /// Writes a PING frame
        /// </summary>
        private Task WritePingFrameAsync(WriteRequest wr)
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
        private Task WriteGoAwayFrameAsync(WriteRequest wr)
        {
            var dataSize = wr.GoAwayData.RequiredSize;
            var totalSize = FrameHeader.HeaderSize + dataSize;
            EnsureBuffer(totalSize);

            wr.Header.Length = dataSize;
            LogOutgoingFrameHeader(wr.Header);
            var headerView = new ArraySegment<byte>(outBuf, 0, FrameHeader.HeaderSize);
            wr.Header.EncodeInto(headerView);

            wr.GoAwayData.EncodeInto(
                new ArraySegment<byte>(outBuf, FrameHeader.HeaderSize, dataSize));
            var data = new ArraySegment<byte>(outBuf, 0, totalSize);

            return this.outStream.WriteAsync(data);
        }

        /// <summary>
        /// Writes a RESET frame
        /// </summary>
        private Task WriteResetFrameAsync(WriteRequest wr)
        {
            wr.Header.Length = ResetFrameData.Size;
            LogOutgoingFrameHeader(wr.Header);
            // Serialize the frame header into the outgoing buffer
            wr.Header.EncodeInto(new ArraySegment<byte>(outBuf, 0, FrameHeader.HeaderSize));
            wr.ResetFrameData.EncodeInto(
                new ArraySegment<byte>(outBuf, FrameHeader.HeaderSize, ResetFrameData.Size));
            var totalSize = FrameHeader.HeaderSize + ResetFrameData.Size;
            var data = new ArraySegment<byte>(outBuf, 0, totalSize);

            // Write the header
            return this.outStream.WriteAsync(data);
        }

        /// <summary>
        /// Writes a SETTINGS frame which contains the encoded settings.
        /// </summary>
        private Task WriteSettingsAsync(Settings settings)
        {
            var fh = new FrameHeader
            {
                Type = FrameType.Settings,
                StreamId = 0u,
                Length = settings.RequiredSize,
                Flags = 0,
            };
            LogOutgoingFrameHeader(fh);
            fh.EncodeInto(
                new ArraySegment<byte>(outBuf, 0, FrameHeader.HeaderSize));
            settings.EncodeInto(new ArraySegment<byte>(
                outBuf, FrameHeader.HeaderSize, settings.RequiredSize));
            var totalSize = FrameHeader.HeaderSize + settings.RequiredSize;
            var data = new ArraySegment<byte>(outBuf, 0, totalSize);
            return this.outStream.WriteAsync(data);
        }

        /// <summary>
        /// Writes a SETTINGS acknowledge frame
        /// </summary>
        private Task WriteSettingsAckAsync()
        {
            var fh = new FrameHeader
            {
                Type = FrameType.Settings,
                StreamId = 0u,
                Length = 0,
                Flags = (byte)SettingsFrameFlags.Ack,
            };
            LogOutgoingFrameHeader(fh);
            var headerView = new ArraySegment<byte>(outBuf, 0, FrameHeader.HeaderSize);
            fh.EncodeInto(headerView);
            return this.outStream.WriteAsync(headerView);
        }

        /// <summary>
        /// Writes a full header block.
        /// This will actually write a headers frame and possibly
        /// multiple continuation frames.
        /// This will not utilize the padding feature currently
        /// </summary>
        private async Task WriteHeadersAsync(WriteRequest wr)
        {
            // Get an output buffer that's big enough for the headers.
            // As there's currently no estimation logic how big the buffer needs
            // to be we just take options.MaxFrameSize.
            // The output buffer could however also be limited to a smaller value,
            // which means more continuation frames would be used (see logic below).
            EnsureBuffer(FrameHeader.HeaderSize + MaxFrameSize);

            // Limit the maximum frame size also to the limit of the output
            // buffer.
            var maxFrameSize = Math.Min(
                MaxFrameSize,
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
        public bool RegisterStream(uint streamId)
        {
            lock (shared.Mutex)
            {
                // After close is initiated or errors happened no further streams
                // may be registered
                if (shared.CloseRequested)
                {
                    return false;
                }

                var sd = new StreamData{
                    StreamId = streamId,
                    Window = shared.InitialWindowSize, // TODO: Switch to shared.?
                    WriteQueue = new Queue<WriteRequest>(),
                    EndOfStreamQueued = false,
                };
                shared.Streams.Add(sd);
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
            lock (shared.Mutex)
            {
                RemoveStreamLocked(streamId);
            }
        }

        private void RemoveStreamLocked(uint streamId)
        {
            Queue<WriteRequest> writeQueue = null;
            for (var i = 0; i < shared.Streams.Count; i++)
            {
                var s = shared.Streams[i];
                if (s.StreamId == streamId)
                {
                    writeQueue = s.WriteQueue;
                    shared.Streams.RemoveAt(i);
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
            var streamsMap = shared.Streams;
            foreach (var stream in shared.Streams)
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

            foreach (var elem in shared.WriteQueue)
            {
                elem.Result = WriteResult.StreamResetError;
                elem.Completed.Set();
            }
            shared.WriteQueue.Clear();
        }

        /// <summary>
        /// Instruct the writer to utilize the new settings that the remote
        /// required and to send a settings acknowledge frame.
        /// </summary>
        /// <returns>
        /// Returns an error if updating the signal leaded to an invalid state.
        /// </returns>
        public async Task<Http2Error?> ApplyAndAckRemoteSettings(
            Settings newRemoteSettings)
        {
            ChangeSettingsRequest changeRequest = null;

            lock (shared.Mutex)
            {
                // No need to apply settings when we are shutting down away
                // There might be some frames left for sending, for which the
                // new Settings can be used. But we should still be able to send
                // them with the old settings, as we don't ACK the new settings.
                if (shared.CloseRequested) return null;

                changeRequest = new ChangeSettingsRequest
                {
                    NewRemoteSettings = newRemoteSettings,
                    Completed = new AsyncManualResetEvent(false),
                };
                shared.ChangeSettingsRequest = changeRequest;
            }

            wakeupWriter.Set();

            await changeRequest.Completed;
            return changeRequest.Result;
        }

        private Http2Error? ApplyNewSettings(
            Settings remoteSettings)
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
            this.MaxFrameSize = (int)remoteSettings.MaxFrameSize;

            this.MaxHeaderListSize = (int)remoteSettings.MaxHeaderListSize;

            // Update the maximum HPACK table size
            var newRequestedTableSize = (int)remoteSettings.HeaderTableSize;
            if (newRequestedTableSize > this.hEncoder.DynamicTableSize)
            {
                // We could theoretically just keep the current setting.
                // There's no need to use a bigger setting, it's just an
                // option that is granted to us from the remote.
                // We will even not want to go to an arbitrary settings, since
                // this means the remote can trigger us into using an infinite
                // amount of memory.
                // As a compromise increase the header table size up to a
                // configured limit.
                this.hEncoder.DynamicTableSize =
                    Math.Min(newRequestedTableSize, DynamicTableSizeLimit);
            }
            else
            {
                // We need to lower our header table size.
                // The next header block that we encode will contain a
                // notification about it.
                this.hEncoder.DynamicTableSize = newRequestedTableSize;
            }

            // Store the new initial window size and update all existing flow
            // control windows. This needs to be atomic inside a mutex, to
            // guarantee that concurrent and future stream registrations utilize
            // the correct window.
            lock (shared.Mutex)
            {
                // Calculate the delta for the window size
                // The output stream windows need to be adjusted by this value.
                var initialWindowSizeDelta =
                    (int)remoteSettings.InitialWindowSize - shared.InitialWindowSize;

                shared.InitialWindowSize = (int)remoteSettings.InitialWindowSize;

                // Update all streams windows
                return UpdateAllStreamWindowsLocked(initialWindowSizeDelta);
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

            lock (shared.Mutex)
            {
                if (streamId == 0)
                {
                    // Check for overflow
                    var updatedValue = (long)connFlowWindow + (long)amount;
                    if (updatedValue > (long)int.MaxValue)
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
                            connFlowWindow, (int)updatedValue);
                    }
                    if (connFlowWindow == 0) wakeup = true;
                    connFlowWindow = (int)updatedValue;
                }
                else
                {
                    for (var i = 0; i < shared.Streams.Count; i++)
                    {
                        if (shared.Streams[i].StreamId == streamId)
                        {
                            var s = shared.Streams[i];
                            // Check for overflow
                            var updatedValue = (long)s.Window + (long)amount;
                            if (updatedValue > (long)int.MaxValue)
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
                                    streamId, s.Window, (int)updatedValue);
                            }
                            s.Window = (int)updatedValue;
                            shared.Streams[i] = s;
                            if (s.Window > 0 && s.WriteQueue.Count > 0)
                            {
                                wakeup = true;
                            }
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

        /// <summary>
        /// Updates the flow control window of all streams by the given stream by amount.
        /// The amount can be negative.
        /// </summary>
        /// <returns>
        /// Returns an error if at least one flow control window over- or underflows
        /// during this operation.
        /// </returns>
        private Http2Error? UpdateAllStreamWindowsLocked(int amount)
        {
            bool hasOverflow = false;
            if (amount == 0) return null;

            // Iterate over all streams and apply window update
            for (var i = 0; i < shared.Streams.Count; i++)
            {
                var s = shared.Streams[i];
                // Check for overflow and underflow
                var updatedValue = (long)s.Window + (long)amount;
                if (updatedValue > (long)int.MaxValue ||
                    updatedValue < (long)int.MinValue)
                {
                    hasOverflow = true;
                    break;
                }

                if (Connection.logger != null &&
                    Connection.logger.IsEnabled(LogLevel.Trace))
                {
                    Connection.logger.LogTrace(
                        "Outgoing flow control window update:\n" +
                        "  Stream {0} window: {1} -> {2}",
                        s.StreamId, s.Window, s.Window + amount);
                }
                s.Window += amount;
                shared.Streams[i] = s;
            }

            if (hasOverflow)
            {
                // We always signal a connection error if the flow control window
                // overflows as the result of a SETTINGS update, since
                // - that will not happen in normal environment (settings updates
                //   happen early and not near the end of the window)
                // - the update might overflow multiple stream windows at once,
                //   which we can't signal and handle through the Http2Error
                //   return value.
                return new Http2Error
                {
                    Code = ErrorCode.FlowControlError,
                    StreamId = 0u,
                    Message = "Flow control window overflow through SETTINGS update",
                };
            }
            else
            {
                return null;
            }
        }
    }
}