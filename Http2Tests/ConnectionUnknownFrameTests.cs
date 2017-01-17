using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Xunit;

using Http2;

namespace Http2Tests
{
    public class ConnectionUnknownFrameTests
    {
        [Theory]
        [InlineData(true, 0)]
        [InlineData(true, 512)]
        [InlineData(false, 0)]
        [InlineData(false, 512)]
        public async Task ConnectionShouldIgnoreUnknownFramesWithPong(
            bool isServer, int payloadLength)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                isServer, inPipe, outPipe);

            // send an undefined frame type
            var fh = new FrameHeader
            {
                Type = (FrameType)100,
                Flags = 33,
                Length = payloadLength,
                StreamId = 0,
            };
            await inPipe.WriteFrameHeader(fh);
            if (payloadLength != 0)
            {
                var payload = new byte[payloadLength];
                await inPipe.WriteAsync(new ArraySegment<byte>(payload));
            }

            // Send a ping afterwards
            // If we get a response the unknown frame in between was ignored
            var pingData = new byte[8];
            for (var i = 0; i < 8; i++) pingData[i] = (byte)i;
            await inPipe.WritePing(pingData, false);
            var res = await outPipe.ReadFrameHeaderWithTimeout();
            Assert.Equal(FrameType.Ping, res.Type);
            Assert.Equal(0u, res.StreamId);
            Assert.Equal(8, res.Length);
            Assert.Equal((byte)PingFrameFlags.Ack, res.Flags);
            var pongData = new byte[8];
            await outPipe.ReadAllWithTimeout(new ArraySegment<byte>(pongData));
        }
    }
}