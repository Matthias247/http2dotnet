using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

using Xunit;
using Xunit.Abstractions;

using Http2;
using Http2.Hpack;

namespace Http2Tests
{
    public class ConnectionResetStreamTests
    {
        private readonly ILoggerProvider loggerProvider;

        public ConnectionResetStreamTests(ITestOutputHelper outputHelper)
        {
            this.loggerProvider = new XUnitOutputLoggerProvider(outputHelper);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ConnectionShouldGoAwayOnInvalidResetStreamId(
            bool isServer)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                isServer, inPipe, outPipe, loggerProvider);

            await inPipe.WriteResetStream(0, ErrorCode.Cancel);
            await outPipe.AssertGoAwayReception(ErrorCode.ProtocolError, 0);
            await outPipe.AssertStreamEnd();
        }

        [Theory]
        [InlineData(false, 0)]
        [InlineData(false, ResetFrameData.Size-1)]
        [InlineData(false, ResetFrameData.Size+1)]
        [InlineData(true, 0)]
        [InlineData(true, ResetFrameData.Size-1)]
        [InlineData(true, ResetFrameData.Size+1)]
        public async Task ConnectionShouldGoAwayOnInvalidResetStreamFrameLength(
            bool isServer, int resetFrameLength)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                isServer, inPipe, outPipe, loggerProvider);

            var rstStreamHeader = new FrameHeader
            {
                Type = FrameType.ResetStream,
                Flags = 0,
                Length = resetFrameLength,
                StreamId = 1,
            };
            await inPipe.WriteFrameHeader(rstStreamHeader);
            await outPipe.AssertGoAwayReception(ErrorCode.FrameSizeError, 0);
            await outPipe.AssertStreamEnd();
        }

        [Fact]
        public async Task ConnectionShouldIgnoreResetsforUnknownStreams()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            Func<IStream, bool> listener = (s) => true;
            var conn = await ConnectionUtils.BuildEstablishedConnection(
                                true, inPipe, outPipe, loggerProvider, listener);
            var hEncoder = new Encoder();

            var streamId = 7u;
            await inPipe.WriteHeaders(
                hEncoder, streamId, false, ServerStreamTests.DefaultGetHeaders);

            await inPipe.WriteResetStream(streamId - 2, ErrorCode.RefusedStream);
            await inPipe.WriteResetStream(streamId - 4, ErrorCode.Cancel);

            // Send a ping afterwards
            // If we get a response the reset frame in between was ignored
            await inPipe.WritePing(new byte[8], false);
            await outPipe.ReadAndDiscardPong();
        }

        [Theory]
        [InlineData(true, 1u)]
        [InlineData(false, 1u)]
        [InlineData(true, 2u)]
        [InlineData(false, 2u)]
        public async Task ResetsOnIdleStreamsShouldBeTreatedAsConnectionError(
            bool isServer, uint streamId)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                isServer, inPipe, outPipe, loggerProvider);

            await inPipe.WriteResetStream(streamId, ErrorCode.RefusedStream);
            await outPipe.AssertGoAwayReception(ErrorCode.ProtocolError, 0u);
        }
    }
}