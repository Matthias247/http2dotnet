using System;
using System.IO;
using System.Threading.Tasks;

using Xunit;

using Http2;

namespace Http2Tests
{
    public static class ReadableStreamTestExtensions
    {
        public const int ReadTimeout = 250;

        public static async Task<StreamReadResult> ReadWithTimeout(
            this IReadableByteStream stream, ArraySegment<byte> buf)
        {
            var readTask = stream.ReadAsync(buf).AsTask();
            var timeoutTask = Task.Delay(ReadTimeout);
            var combined = Task.WhenAny(new Task[]{ readTask, timeoutTask });
            var done = await combined;
            if (done == readTask)
            {
                return readTask.Result;
            }
            throw new TimeoutException();
        }

        public static async Task ReadAllWithTimeout(
            this IReadableByteStream stream, ArraySegment<byte> buf)
        {
            var readTask = stream.ReadAll(buf).AsTask();
            var timeoutTask = Task.Delay(ReadTimeout);
            var combined = Task.WhenAny(new Task[]{ readTask, timeoutTask });
            var done = await combined;
            if (done == readTask)
            {
                return;
            }
            throw new TimeoutException();
        }

        public static async Task<byte[]> ReadAllToArray(
            this IReadableByteStream stream)
        {
            var totalBuf = new MemoryStream();
            var buf = new byte[16*1024];
            while (true)
            {
                var res = await stream.ReadAsync(new ArraySegment<byte>(buf));
                if (res.BytesRead > 0)
                {
                    totalBuf.Write(buf, 0, res.BytesRead);
                }
                if (res.EndOfStream)
                {
                    return totalBuf.ToArray();
                }
            }
        }

        public static async Task<byte[]> ReadAllToArrayWithTimeout(
            this IReadableByteStream stream)
        {
            var readTask = stream.ReadAllToArray();
            var timeoutTask = Task.Delay(ReadTimeout);
            var combined = Task.WhenAny(new Task[]{ readTask, timeoutTask });
            var done = await combined;
            if (done == readTask)
            {
                return readTask.Result;
            }
            throw new TimeoutException();
        }

        public static async Task<FrameHeader> ReadFrameHeaderWithTimeout(
            this IReadableByteStream stream)
        {
            var headerSpace = new byte[FrameHeader.HeaderSize];
            var readTask = FrameHeader.ReceiveAsync(stream, headerSpace).AsTask();
            var timeoutTask = Task.Delay(ReadTimeout);
            var combined = Task.WhenAny(new Task[]{ readTask, timeoutTask });
            var done = await combined;
            if (done == readTask)
            {
                return readTask.Result;
            }
            throw new TimeoutException();
        }

        public static async Task AssertReadTimeout(
            this IReadableByteStream stream)
        {
            var buf = new byte[1];
            try
            {
                await stream.ReadAllWithTimeout(
                    new ArraySegment<byte>(buf));
            }
            catch (Exception e)
            {
                Assert.IsType<TimeoutException>(e);
                return;
            }
            Assert.False(true, "Expected no more data but received a byte");
        }

        public static async Task ReadAndDiscardPreface(
            this IReadableByteStream stream)
        {
            var b = new byte[ClientPreface.Length];
            await stream.ReadAllWithTimeout(new ArraySegment<byte>(b));
        }

        public static async Task ReadAndDiscardSettings(
            this IReadableByteStream stream)
        {
            var header = await stream.ReadFrameHeaderWithTimeout();
            Assert.Equal(FrameType.Settings, header.Type);
            Assert.InRange(header.Length, 0, 256);
            await stream.ReadAllWithTimeout(
                new ArraySegment<byte>(new byte[header.Length]));
        }

        public static async Task ReadAndDiscardHeaders(
            this IReadableByteStream stream,
            uint expectedStreamId,
            bool expectEndOfStream)
        {
            var header = await stream.ReadFrameHeaderWithTimeout();
            Assert.Equal(FrameType.Headers, header.Type);
            Assert.Equal(expectedStreamId, header.StreamId);
            var isEndOfStream = (header.Flags & (byte)HeadersFrameFlags.EndOfStream) != 0;
            Assert.Equal(expectEndOfStream, isEndOfStream);
            var hbuf = new ArraySegment<byte>(new byte[header.Length]);
            await stream.ReadAllWithTimeout(hbuf);
        }

        public static async Task ReadAndDiscardData(
            this IReadableByteStream stream,
            uint expectedStreamId,
            bool expectEndOfStream,
            int? expectedAmount)
        {
            var header = await stream.ReadFrameHeaderWithTimeout();
            Assert.Equal(FrameType.Data, header.Type);
            Assert.Equal(expectedStreamId, header.StreamId);
            var isEndOfStream = (header.Flags & (byte)DataFrameFlags.EndOfStream) != 0;
            Assert.Equal(expectEndOfStream, isEndOfStream);
            if (expectedAmount.HasValue)
            {
                Assert.Equal(expectedAmount.Value, header.Length);
            }
            var dataBuf = new ArraySegment<byte>(new byte[header.Length]);
            await stream.ReadAllWithTimeout(dataBuf);
        }

        public static async Task ReadAndDiscardPong(
            this IReadableByteStream stream)
        {
            var header = await stream.ReadFrameHeaderWithTimeout();
            Assert.Equal(FrameType.Ping, header.Type);
            Assert.Equal(8, header.Length);
            Assert.Equal((byte)PingFrameFlags.Ack, header.Flags);
            Assert.Equal(0u, header.StreamId);
            await stream.ReadAllWithTimeout(
                new ArraySegment<byte>(new byte[8]));
        }

        public static async Task AssertSettingsAck(
            this IReadableByteStream stream)
        {
            var header = await stream.ReadFrameHeaderWithTimeout();
            Assert.Equal(FrameType.Settings, header.Type);
            Assert.Equal(0, header.Length);
            Assert.Equal((byte)SettingsFrameFlags.Ack, header.Flags);
            Assert.Equal(0u, header.StreamId);
        }

        public static async Task AssertStreamEnd(
            this IReadableByteStream stream)
        {
            var headerBytes = new byte[FrameHeader.HeaderSize];
            var res = await ReadWithTimeout(stream, new ArraySegment<byte>(headerBytes));
            if (!res.EndOfStream)
            {
                var hdr = FrameHeader.DecodeFrom(
                    new ArraySegment<byte>(headerBytes));
                var msg = "Expected end of stream, but got " +
                    FramePrinter.PrintFrameHeader(hdr);
                Assert.True(res.EndOfStream, msg);
            }
            Assert.Equal(0, res.BytesRead);
        }

        public static async Task AssertGoAwayReception(
            this IReadableByteStream stream,
            ErrorCode expectedErrorCode,
            uint lastStreamId)
        {
            var hdr = await stream.ReadFrameHeaderWithTimeout();
            Assert.Equal(FrameType.GoAway, hdr.Type);
            Assert.Equal(0u, hdr.StreamId);
            Assert.Equal(0, hdr.Flags);
            Assert.InRange(hdr.Length, 8, 256);
            var goAwayBytes = new byte[hdr.Length];
            await stream.ReadAllWithTimeout(new ArraySegment<byte>(goAwayBytes));
            var goAwayData = GoAwayFrameData.DecodeFrom(new ArraySegment<byte>(goAwayBytes));
            Assert.Equal(lastStreamId, goAwayData.Reason.LastStreamId);
            Assert.Equal(expectedErrorCode, goAwayData.Reason.ErrorCode);
        }

        public static async Task AssertResetStreamReception(
            this IReadableByteStream stream,
            uint expectedStreamId,
            ErrorCode expectedErrorCode)
        {
            var hdr = await stream.ReadFrameHeaderWithTimeout();
            Assert.Equal(FrameType.ResetStream, hdr.Type);
            Assert.Equal(expectedStreamId, hdr.StreamId);
            Assert.Equal(0, hdr.Flags);
            Assert.Equal(ResetFrameData.Size, hdr.Length);
            var resetBytes = new byte[hdr.Length];
            await stream.ReadAllWithTimeout(new ArraySegment<byte>(resetBytes));
            var resetData = ResetFrameData.DecodeFrom(new ArraySegment<byte>(resetBytes));
            Assert.Equal(expectedErrorCode, resetData.ErrorCode);
        }

        public static async Task AssertWindowUpdate(
            this IReadableByteStream stream,
            uint expectedStreamId,
            int increment)
        {
            var header = await stream.ReadFrameHeaderWithTimeout();
            Assert.Equal(FrameType.WindowUpdate, header.Type);
            Assert.Equal(WindowUpdateData.Size, header.Length);
            Assert.Equal(0, header.Flags);
            Assert.Equal(expectedStreamId, header.StreamId);
            var buf = new byte[WindowUpdateData.Size];
            await stream.ReadAllWithTimeout(new ArraySegment<byte>(buf));
            var wu = WindowUpdateData.DecodeFrom(new ArraySegment<byte>(buf));
            Assert.Equal(increment, wu.WindowSizeIncrement);
        }
    }
}