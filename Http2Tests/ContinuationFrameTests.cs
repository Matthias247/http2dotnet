using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

using Http2;
using Http2.Hpack;

namespace Http2Tests
{
    public class ContinuationFrameTests
    {
        private ILoggerProvider loggerProvider;

        public ContinuationFrameTests(ITestOutputHelper outHelper)
        {
            loggerProvider = new XUnitOutputLoggerProvider(outHelper);
        }

        [Fact]
        public async Task ContinuationWithoutHeadersShouldLeadToGoAway()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            Func<IStream, bool> listener = (s) => true;
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider, listener);

            var hEncoder = new Encoder();
            await inPipe.WriteContinuation(hEncoder, 1u, ServerStreamTests.DefaultGetHeaders, true);
            await outPipe.AssertGoAwayReception(ErrorCode.ProtocolError, 0);
            await outPipe.AssertStreamEnd();
        }
    }
}