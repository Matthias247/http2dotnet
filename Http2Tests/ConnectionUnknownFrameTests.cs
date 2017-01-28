using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

using Xunit;
using Xunit.Abstractions;

using Http2;

namespace Http2Tests
{
    public class ConnectionUnknownFrameTests
    {
        private readonly ILoggerProvider loggerProvider;

        public ConnectionUnknownFrameTests(ITestOutputHelper outputHelper)
        {
            this.loggerProvider = new XUnitOutputLoggerProvider(outputHelper);
        }

        [Theory]
        [InlineData(true, 0)]
        [InlineData(true, 512)]
        [InlineData(false, 0)]
        [InlineData(false, 512)]
        public async Task ConnectionShouldIgnoreUnknownFrames(
            bool isServer, int payloadLength)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                isServer, inPipe, outPipe, loggerProvider);

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
            await outPipe.ReadAndDiscardPong();
        }
    }
}