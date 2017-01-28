using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

using Xunit;
using Xunit.Abstractions;

using Http2;

namespace Http2Tests
{
    public class ConnectionPingTests
    {
        private readonly ILoggerProvider loggerProvider;

        public ConnectionPingTests(ITestOutputHelper outputHelper)
        {
            this.loggerProvider = new XUnitOutputLoggerProvider(outputHelper);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ConnectionShouldRespondToPingWithPong(bool isServer)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                isServer, inPipe, outPipe, loggerProvider);

            var pingData = new byte[8];
            for (var i = 0; i < pingData.Length; i++) pingData[i] = (byte)i;
            await inPipe.WritePing(pingData, false);
            var res = await outPipe.ReadFrameHeaderWithTimeout();
            Assert.Equal(FrameType.Ping, res.Type);
            Assert.Equal(0u, res.StreamId);
            Assert.Equal(8, res.Length);
            Assert.Equal((byte)PingFrameFlags.Ack, res.Flags);
            var pongData = new byte[8];
            await outPipe.ReadAllWithTimeout(new ArraySegment<byte>(pongData));
            for (var i = 0; i < pingData.Length; i++) Assert.Equal((byte)i, pongData[i]);
        }

        [Fact]
        public async Task ConnectionShouldGoAwayOnInvalidPingStreamId()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider);

            var pingHeader = new FrameHeader
            {
                Type = FrameType.Ping,
                Flags = 0,
                Length = 8,
                StreamId = 1,
            };
            await inPipe.WriteFrameHeader(pingHeader);
            await outPipe.AssertGoAwayReception(ErrorCode.ProtocolError, 0);
            await outPipe.AssertStreamEnd();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(7)]
        [InlineData(9)]
        [InlineData(16384+1)]
        public async Task ConnectionShouldGoAwayOnInvalidPingFrameLength(int pingFrameLength)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider);

            var pingHeader = new FrameHeader
            {
                Type = FrameType.Ping,
                Flags = 0,
                Length = pingFrameLength,
                StreamId = 0,
            };
            await inPipe.WriteFrameHeader(pingHeader);
            await outPipe.AssertGoAwayReception(ErrorCode.FrameSizeError, 0);
            await outPipe.AssertStreamEnd();
        }

        [Fact]
        public async Task ConnectionShouldIgnoreUnsolicitedPingAcks()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider);

            // Write ping ACK
            var pingData = new byte[8];
            for (var i = 0; i < pingData.Length; i++) pingData[i] = (byte)i;
            await inPipe.WritePing(pingData, true);

            // Expect no reaction
            await outPipe.AssertReadTimeout();
        }
    }
}