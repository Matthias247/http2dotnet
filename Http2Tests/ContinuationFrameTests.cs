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

        [Theory]
        [InlineData(16384, 8000, 1, 1)]      // No continuation required
        [InlineData(16384, 16384, 1, 1)]     // No continuation required
        [InlineData(16384, 100*1024, 7, 7)]  // Continuations required
        [InlineData(65535, 100*1024, 2, 7)]  // Continuations required
        public async Task OutgoingHeadersShouldBeFragmentedIntoContinuationsAccordingToFrameSize(
            int maxFrameSize, int totalHeaderBytes,
            int expectedMinNrOfFrames, int expectedMaxNrOfFrames)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var remoteSettings = Settings.Default;
            remoteSettings.MaxFrameSize = (uint)maxFrameSize;
            var res = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe,
                remoteSettings: remoteSettings,
                huffmanStrategy: HuffmanStrategy.Never);

            // Create the list of headers that should be sent
            // This must be big enough in order to send multiple frames
            var headers = new List<HeaderField>();
            // The :status header is necessary to avoid an exception on send
            headers.AddRange(
                ServerStreamTests.DefaultStatusHeaders.TakeWhile(sh =>
                    sh.Name.StartsWith(":")));

            const int headerLen = 3 + 10 + 8; // The calculated size of one header field
            // Calculate the amount of headers of the given size that are needed
            // to be sent based on the required totalHeaderBytes number.
            int requiredHeaderData = totalHeaderBytes - 1; // -1 for :status field
            int amountHeaders =  requiredHeaderData / headerLen;

            for (var i = 0; i < amountHeaders; i++)
            {
                var headerField = new HeaderField
                {
                    Name = "hd" + i.ToString("D8"),
                    Value = i.ToString("D8"),
                    Sensitive = true,
                };
                headers.Add(headerField);
            }

            // Send the headers in background task
            var writeHeaderTask = Task.Run(async () =>
            {
                await res.stream.WriteHeaders(headers, false);
            });

            var hDecoder = new Decoder();
            var hBuf = new byte[maxFrameSize];
            var expectCont = false;
            var rcvdFrames = 0;
            var rcvdHeaders = new List<HeaderField>();
            while (true)
            {
                var fh = await outPipe.ReadFrameHeaderWithTimeout();
                Assert.Equal(expectCont ? FrameType.Continuation : FrameType.Headers, fh.Type);
                Assert.Equal(1u, fh.StreamId);
                Assert.InRange(fh.Length, 1, maxFrameSize);
                // Read header block fragment data
                await outPipe.ReadAllWithTimeout(
                    new ArraySegment<byte>(hBuf, 0, fh.Length));
                // Decode it
                var lastHeadersCount = rcvdHeaders.Count;
                var decodeResult = hDecoder.DecodeHeaderBlockFragment(
                    new ArraySegment<byte>(hBuf, 0, fh.Length),
                    int.MaxValue,
                    rcvdHeaders);

                Assert.True(
                    rcvdHeaders.Count > lastHeadersCount,
                    "Expected to retrieve at least one header per frame");
                Assert.Equal(DecoderExtensions.DecodeStatus.Success, decodeResult.Status);

                expectCont = true;
                rcvdFrames++;

                if ((fh.Flags & (byte)ContinuationFrameFlags.EndOfHeaders) != 0) break;
            }
            Assert.InRange(rcvdFrames, expectedMinNrOfFrames, expectedMaxNrOfFrames);
            Assert.Equal(headers.Count, rcvdHeaders.Count);
            Assert.True(rcvdHeaders.SequenceEqual(headers));
        }

        [Theory]
        [InlineData(1, 0, FrameType.Continuation)]
        [InlineData(1, 65536, FrameType.Continuation)]
        [InlineData(0, null, FrameType.Continuation)]
        [InlineData(1, null, FrameType.Data)]
        [InlineData(1, null, FrameType.Ping)]
        [InlineData(1, null, FrameType.Headers)]
        public async Task InvalidContinuationFramesShouldLeadToGoAway(
            uint contStreamId, int? contLength, FrameType contFrameType)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            Func<IStream, bool> listener = (s) => true;
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider, listener);

            var hEncoder = new Encoder();
            // Send a valid HEADERS frame
            await inPipe.WriteHeaders(
                hEncoder, 1, false,
                ServerStreamTests.DefaultGetHeaders.Take(2), false);

            // Followed by an invalid continuation frame
            var outBuf = new byte[Settings.Default.MaxFrameSize];
            var result = hEncoder.EncodeInto(
                new ArraySegment<byte>(outBuf),
                ServerStreamTests.DefaultGetHeaders.Skip(2));

            var length = contLength ?? result.UsedBytes;
            var fh = new FrameHeader
            {
                Type = contFrameType,
                StreamId = contStreamId,
                Length = length,
                Flags = 0,
            };
            await inPipe.WriteFrameHeader(fh);

            await outPipe.AssertGoAwayReception(ErrorCode.ProtocolError, 0u);
            await outPipe.AssertStreamEnd();
        }
    }
}