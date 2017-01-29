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
        public async Task ContinuationsWithoutHeadersShouldLeadToGoAway()
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

        [Theory]
        [InlineData(new int[]{2})]
        [InlineData(new int[]{2,1})]
        [InlineData(new int[]{1,1,1})]
        public async Task ContinuationFrameHeadersShouldBeAddedToTotalHeaders(
            int[] nrHeadersInFrame)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            IStream stream = null;
            IEnumerable<HeaderField> receivedHeaders = null;
            SemaphoreSlim handlerDone = new SemaphoreSlim(0);

            Func<IStream, bool> listener = (s) =>
            {
                stream = s;
                Task.Run(async () =>
                {
                    receivedHeaders = await s.ReadHeaders();
                    handlerDone.Release();
                });
                return true;
            };
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider, listener);

            var hEncoder = new Encoder();
            var totalHeaders = ServerStreamTests.DefaultGetHeaders;
            var isContinuation = false;
            var toSkip = 0;
            var isEndOfHeaders = false;
            for (var frameNr = 0; frameNr <= nrHeadersInFrame.Length; frameNr++)
            {
                var headersToSend = totalHeaders.Skip(toSkip);
                if (frameNr != nrHeadersInFrame.Length)
                {
                    var toSend = nrHeadersInFrame[frameNr];
                    headersToSend = headersToSend.Take(toSend);
                    toSkip += toSend;
                }
                else
                {
                    // send remaining headers
                    isEndOfHeaders = true;
                }
                if (!isContinuation)
                {
                    await inPipe.WriteHeaders(
                        hEncoder, 1, false, headersToSend, isEndOfHeaders);
                    isContinuation = true;
                }
                else
                {
                    await inPipe.WriteContinuation(
                        hEncoder, 1, headersToSend, isEndOfHeaders);
                }
            }
            var handlerCompleted = await handlerDone.WaitAsync(
                ReadableStreamTestExtensions.ReadTimeout);
            Assert.True(handlerCompleted, "Expected stream handler to complete");
            Assert.True(
                totalHeaders.SequenceEqual(receivedHeaders),
                "Expected to receive all sent headers");
        }
    }
}