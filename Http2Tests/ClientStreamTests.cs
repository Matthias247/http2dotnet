using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

using Http2;
using Http2.Hpack;
using static Http2Tests.TestHeaders;

namespace Http2Tests
{
    public class ClientStreamTests
    {
        private ILoggerProvider loggerProvider;

        public ClientStreamTests(ITestOutputHelper outHelper)
        {
            loggerProvider = new XUnitOutputLoggerProvider(outHelper);
        }

        [Fact]
        public async Task CreatingStreamShouldEmitHeaders()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var conn = await ConnectionUtils.BuildEstablishedConnection(
                false, inPipe, outPipe, loggerProvider);

            var stream1Task = conn.CreateStream(DefaultGetHeaders, false);
            var stream1 = await stream1Task;
            Assert.Equal(StreamState.Open, stream1.State);
            Assert.Equal(1u, stream1.Id);

            var fh = await outPipe.ReadFrameHeaderWithTimeout();
            Assert.Equal(1u, fh.StreamId);
            Assert.Equal(FrameType.Headers, fh.Type);
            Assert.Equal((byte)HeadersFrameFlags.EndOfHeaders, fh.Flags);
            Assert.Equal(EncodedDefaultGetHeaders.Length, fh.Length);
            var hdrData = new byte[fh.Length];
            await outPipe.ReadWithTimeout(new ArraySegment<byte>(hdrData));
            Assert.Equal(EncodedDefaultGetHeaders, hdrData);

            var stream3Task = conn.CreateStream(DefaultGetHeaders, true);
            var stream3 = await stream3Task;
            Assert.Equal(StreamState.HalfClosedLocal, stream3.State);
            Assert.Equal(3u, stream3.Id);

            fh = await outPipe.ReadFrameHeaderWithTimeout();
            Assert.Equal(3u, fh.StreamId);
            Assert.Equal(FrameType.Headers, fh.Type);
            Assert.Equal(
                (byte)(HeadersFrameFlags.EndOfHeaders | HeadersFrameFlags.EndOfStream),
                fh.Flags);
            Assert.Equal(EncodedIndexedDefaultGetHeaders.Length, fh.Length);
            var hdrData3 = new byte[fh.Length];
            await outPipe.ReadWithTimeout(new ArraySegment<byte>(hdrData3));
            Assert.Equal(EncodedIndexedDefaultGetHeaders, hdrData3);
        }
    }
}