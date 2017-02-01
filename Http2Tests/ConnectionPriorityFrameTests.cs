using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

using Xunit;
using Xunit.Abstractions;

using Http2;

namespace Http2Tests
{
    public class ConnectionPriorityFrameTests
    {
        private readonly ILoggerProvider loggerProvider;

        public ConnectionPriorityFrameTests(ITestOutputHelper outputHelper)
        {
            this.loggerProvider = new XUnitOutputLoggerProvider(outputHelper);
        }

        [Theory]
        [InlineData(true, 3, 1, true, 0)]
        [InlineData(false, 3, 1, true, 0)]
        [InlineData(true, int.MaxValue - 2, int.MaxValue, false, 255)]
        [InlineData(false, int.MaxValue - 2, int.MaxValue, false, 255)]
        public async Task ConnectionShouldIgnorePriorityData(
            bool isServer, uint streamId,
            uint streamDependency, bool isExclusive, byte weight)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                isServer, inPipe, outPipe, loggerProvider);

            var prioData = new PriorityData
            {
                StreamDependency = streamDependency,
                StreamDependencyIsExclusive = isExclusive,
                Weight = weight,
            };
            await inPipe.WritePriority(streamId, prioData);

            // Send a ping afterwards
            // If we get a response the priority frame in between was ignored
            var pingData = new byte[8];
            for (var i = 0; i < 8; i++) pingData[i] = (byte)i;
            await inPipe.WritePing(pingData, false);
            await outPipe.ReadAndDiscardPong();
        }

        [Fact]
        public async Task ConnectionShouldGoAwayOnPriorityStreamIdZero()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider);

            var prioData = new PriorityData
            {
                StreamDependency = 1,
                StreamDependencyIsExclusive = false,
                Weight = 0,
            };
            await inPipe.WritePriority(0, prioData);

            await outPipe.AssertGoAwayReception(ErrorCode.ProtocolError, 0u);
            await outPipe.AssertStreamEnd();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(PriorityData.Size-1)]
        [InlineData(PriorityData.Size+1)]
        public async Task ConnectionShouldGoAwayOnInvalidPriorityFrameLength(
            int priorityFrameSize)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider);

           var fh = new FrameHeader
            {
                Type = FrameType.Priority,
                Flags = 0,
                Length = priorityFrameSize,
                StreamId = 1u,
            };
            await inPipe.WriteFrameHeader(fh);

            await outPipe.AssertGoAwayReception(ErrorCode.FrameSizeError, 0u);
            await outPipe.AssertStreamEnd();
        }
    }
}