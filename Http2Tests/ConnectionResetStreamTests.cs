using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

using Xunit;
using Xunit.Abstractions;

using Http2;

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

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ConnectionShouldIgnoreResetsforUnknownStreams(
            bool isServer)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                isServer, inPipe, outPipe, loggerProvider);

            await inPipe.WriteResetStream(1, ErrorCode.RefusedStream);
            await inPipe.WriteResetStream(2, ErrorCode.Cancel);

            // Send a ping afterwards
            // If we get a response the reset frame in between was ignored
            var pingData = new byte[8];
            for (var i = 0; i < 8; i++) pingData[i] = (byte)i;
            await inPipe.WritePing(pingData, false);
            await outPipe.ReadAndDiscardPong();
        }
    }
}