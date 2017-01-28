using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

using Xunit;
using Xunit.Abstractions;

using Http2;

namespace Http2Tests
{
    public class ConnectionPushPromiseTests
    {
        private readonly ILoggerProvider loggerProvider;

        public ConnectionPushPromiseTests(ITestOutputHelper outputHelper)
        {
            this.loggerProvider = new XUnitOutputLoggerProvider(outputHelper);
        }

        [Theory]
        [InlineData(true, 1, 0)]
        [InlineData(true, 1, 128)]
        [InlineData(true, 2, 0)]
        [InlineData(true, 2, 128)]
        [InlineData(false, 1, 0)]
        [InlineData(false, 1, 128)]
        [InlineData(false, 2, 0)]
        [InlineData(false, 2, 128)]
        public async Task ConnectionShouldGoAwayOnPushPromise(
            bool isServer, uint streamId, int payloadLength)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                isServer, inPipe, outPipe, loggerProvider);

            var fh = new FrameHeader
            {
                Type = FrameType.PushPromise,
                Flags = 0,
                Length = payloadLength,
                StreamId = streamId,
            };
            await inPipe.WriteFrameHeader(fh);
            await outPipe.AssertGoAwayReception(ErrorCode.ProtocolError, 0);
            await outPipe.AssertStreamEnd();
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ConnectionShouldGoAwayOnPushPromiseWithInvalidStreamId(
            bool isServer)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                isServer, inPipe, outPipe, loggerProvider);

            var fh = new FrameHeader
            {
                Type = FrameType.PushPromise,
                Flags = (byte)PushPromiseFrameFlags.EndOfHeaders,
                Length = 128,
                StreamId = 1,
            };
            await inPipe.WriteFrameHeader(fh);
            await outPipe.AssertGoAwayReception(ErrorCode.ProtocolError, 0);
            await outPipe.AssertStreamEnd();
        }

        [Theory]
        [InlineData(true, null)]
        [InlineData(true, 0)]
        [InlineData(true, 1)]
        [InlineData(true, 255)]
        [InlineData(false, null)]
        [InlineData(false, 0)]
        [InlineData(false, 1)]
        [InlineData(false, 255)]
        public async Task ConnectionShouldGoAwayOnPushPromiseWithInvalidLength(
            bool isServer, int? padLen)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                isServer, inPipe, outPipe, loggerProvider);

            // This test is set up in a way where the payload length is 1 byte
            // too small for the transferred content
            var requiredLen = 4;
            var flags = (byte)PushPromiseFrameFlags.EndOfHeaders;
            if (padLen != null)
            {
                flags |= (byte)PushPromiseFrameFlags.Padded;
                requiredLen += 1 + padLen.Value;
            }
            var actualLen = requiredLen - 1;
            var fh = new FrameHeader
            {
                Type = FrameType.PushPromise,
                Flags = flags,
                Length = actualLen,
                StreamId = 1,
            };
            await inPipe.WriteFrameHeader(fh);
            var content = new byte[actualLen];
            var offset = 0;
            if (padLen != null)
            {
                content[offset] = (byte)padLen.Value;
                offset++;
            }
            // Set promised stream Id
            content[offset+0] = content[offset+1] = content[offset+2] = 0;
            if (offset+3 <= content.Length -1)
            {
                content[offset+3] = 1;
            }
            offset += 4;
            await inPipe.WriteAsync(new ArraySegment<byte>(content));

            await outPipe.AssertGoAwayReception(ErrorCode.ProtocolError, 0);
            await outPipe.AssertStreamEnd();
        }
    }
}