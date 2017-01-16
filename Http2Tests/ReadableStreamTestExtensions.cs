using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Text;

using Xunit;

using Http2;

namespace Http2Tests
{
    public static class ReadableStreamTestExtensions
    {
        const int ReadTimeout = 200;

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
            await stream.ReadAllWithTimeout(new ArraySegment<byte>(new byte[header.Length]));
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
            Assert.True(res.EndOfStream, "Expected end of stream");
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
            Assert.Equal(lastStreamId, goAwayData.LastStreamId);
            Assert.Equal(expectedErrorCode, goAwayData.ErrorCode);
        }
    }
}