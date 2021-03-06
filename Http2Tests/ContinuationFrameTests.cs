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
            await inPipe.WriteContinuation(hEncoder, 1u, DefaultGetHeaders, true);
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
                    receivedHeaders = await s.ReadHeadersAsync();
                    handlerDone.Release();
                });
                return true;
            };
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider, listener);

            var hEncoder = new Encoder();
            var totalHeaders = DefaultGetHeaders;
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
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(17)]
        [InlineData(18)]
        public async Task ContinuationsWherePartsDontContainFullHeaderMustBeSupported(
            int nrContinuations)
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
                    receivedHeaders = await s.ReadHeadersAsync();
                    handlerDone.Release();
                });
                return true;
            };
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider, listener);

            var hEncoder = new Encoder();
            // Construct a proper set of header here, where a single headerfield could
            // by splitted over more than 1 fragment
            var totalHeaders = DefaultGetHeaders.TakeWhile(
                h => h.Name.StartsWith(":"));
            totalHeaders = totalHeaders.Append(
                new HeaderField { Name = "longname", Value = "longvalue" });
            totalHeaders = totalHeaders.Append(
                new HeaderField { Name = "abc", Value = "xyz" });

            // Encode all headers in a single header block
            var hBuf = new byte[32*1024];
            var encodeRes =
                hEncoder.EncodeInto(new ArraySegment<byte>(hBuf), totalHeaders);
            Assert.Equal(totalHeaders.Count(), encodeRes.FieldCount);
            Assert.True(
                encodeRes.UsedBytes >= 5,
                "Test must encode headers with at least 5 bytes to work properly");

            // Write the first 3 bytes in a header frame
            // These cover all pseudoheaders, which wouldn't be affected by
            // possible bugs anyway
            var fh = new FrameHeader
            {
                Type = FrameType.Headers,
                Length = 3,
                StreamId = 1u,
                Flags = (byte)0,
            };
            await inPipe.WriteFrameHeader(fh);
            await inPipe.WriteAsync(new ArraySegment<byte>(hBuf, 0, 3));

            var offset = 3;
            var length = encodeRes.UsedBytes - 3;

            for (var i = 0; i < nrContinuations; i++)
            {
                var isLast = i == nrContinuations - 1;
                var dataLength = isLast ? length : 1;
                fh = new FrameHeader
                {
                    Type = FrameType.Continuation,
                    Length = dataLength,
                    StreamId = 1u,
                    Flags = (byte)(isLast ? ContinuationFrameFlags.EndOfHeaders : 0),
                };
                await inPipe.WriteFrameHeader(fh);
                await inPipe.WriteAsync(new ArraySegment<byte>(hBuf, offset, dataLength));
                offset += dataLength;
                length -= dataLength;
            }

            var handlerCompleted = await handlerDone.WaitAsync(
                ReadableStreamTestExtensions.ReadTimeout);
            Assert.True(handlerCompleted, "Expected stream handler to complete");
            Assert.True(
                totalHeaders.SequenceEqual(receivedHeaders),
                "Expected to receive all sent headers");
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public async Task IncompleteHeaderBlocksShouldBeDetected(
            int nrContinuations)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            Func<IStream, bool> listener = (s) => true;
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider, listener);

            var hEncoder = new Encoder();
            // Construct a proper set of header here, where a single headerfield could
            // by splitted over more than 1 fragment
            var totalHeaders = DefaultGetHeaders.TakeWhile(
                h => h.Name.StartsWith(":"));
            totalHeaders = totalHeaders.Append(
                new HeaderField { Name = "longname", Value = "longvalue" });
            totalHeaders = totalHeaders.Append(
                new HeaderField { Name = "abc", Value = "xyz" });

            // Encode all headers in a single header block
            var hBuf = new byte[32*1024];
            var encodeRes =
                hEncoder.EncodeInto(new ArraySegment<byte>(hBuf), totalHeaders);
            Assert.Equal(totalHeaders.Count(), encodeRes.FieldCount);
            Assert.True(
                encodeRes.UsedBytes >= 5,
                "Test must encode headers with at least 5 bytes to work properly");

            // Write header data in multiple parts
            // The last part will miss one byte
            var offset = 0;
            var length = encodeRes.UsedBytes;

            var isLast = nrContinuations == 0;
            var dataLength = isLast ? (length - 1) : 3;
            var fh = new FrameHeader
            {
                Type = FrameType.Headers,
                Length = dataLength,
                StreamId = 1u,
                Flags = (byte)(isLast ? HeadersFrameFlags.EndOfHeaders : 0),
            };
            await inPipe.WriteFrameHeader(fh);
            await inPipe.WriteAsync(new ArraySegment<byte>(hBuf, offset, dataLength));

            offset += dataLength;
            length -= dataLength;

            for (var i = 0; i < nrContinuations; i++)
            {
                isLast = i == nrContinuations - 1;
                dataLength = isLast ? (length - 1) : 1;
                fh = new FrameHeader
                {
                    Type = FrameType.Continuation,
                    Length = dataLength,
                    StreamId = 1u,
                    Flags = (byte)(isLast ? ContinuationFrameFlags.EndOfHeaders : 0),
                };
                await inPipe.WriteFrameHeader(fh);
                await inPipe.WriteAsync(new ArraySegment<byte>(hBuf, offset, dataLength));
                offset += dataLength;
                length -= dataLength;
            }

            Assert.True(1 == length, "Expected to send all but 1 byte");

            // Expect a GoAway as reaction to incomplete headers
            await outPipe.AssertGoAwayReception(ErrorCode.CompressionError, 0u);
            await outPipe.AssertStreamEnd();
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
                DefaultStatusHeaders.TakeWhile(sh =>
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
                await res.stream.WriteHeadersAsync(headers, false);
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
                DefaultGetHeaders.Take(2), false);

            // Followed by an invalid continuation frame
            var outBuf = new byte[Settings.Default.MaxFrameSize];
            var result = hEncoder.EncodeInto(
                new ArraySegment<byte>(outBuf),
                DefaultGetHeaders.Skip(2));

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

        [Theory]
        [InlineData(123, 123, 0, false)]
        [InlineData(0, 123, 0, true)]
        [InlineData(123+3*34, 123+3*34, 0, false)]
        [InlineData(123+3*34-1, 123+3*34, 0, true)]
        [InlineData(123+34, 123, 34, false)]
        [InlineData(123+34, 123, 35, true)]
        [InlineData(123+34*7, 123+34*3, 34*4, false)]
        [InlineData(123+34*7, 123+34*3, 34*4+1, true)]
        public async Task MaxHeaderListSizeViolationsShouldBeDetected(
            uint maxHeaderListSize,
            int headersInFirstFrame,
            int headersInContFrame,
            bool shouldError)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            Func<IStream, bool> listener = (s) => true;
            var settings = Settings.Default;
            settings.MaxHeaderListSize = maxHeaderListSize;
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider, listener,
                localSettings: settings,
                huffmanStrategy: HuffmanStrategy.Never);

            var hEncoder = new Encoder();
            var headers = new List<HeaderField>();
            // Add the default headers
            // These take 123 bytes in store and 3 bytes in transmission
            headers.AddRange(new HeaderField[]
            {
                new HeaderField { Name = ":method", Value = "GET" },
                new HeaderField { Name = ":path", Value = "/" },
                new HeaderField { Name = ":scheme", Value = "http" },
            });
            var currentHeadersLength = headers
                .Select(hf => hf.Name.Length + hf.Value.Length + 32)
                .Sum();
            // Create a header which takes 34 bytes in store and 5 bytes in transmission
            var extraHeader = new HeaderField
            {
                Name = "a", Value = "b", Sensitive = true
            };
            while (currentHeadersLength < headersInFirstFrame)
            {
                headers.Add(extraHeader);
                currentHeadersLength += 1+1+32;
            }

            await inPipe.WriteHeaders(
                hEncoder, 1, false,
                headers, headersInContFrame == 0);

            if (headersInContFrame != 0)
            {
                headers.Clear();
                currentHeadersLength = 0;
                while (currentHeadersLength < headersInContFrame)
                {
                    headers.Add(extraHeader);
                    currentHeadersLength += 1+1+32;
                }
                await inPipe.WriteContinuation(
                    hEncoder, 1,
                    headers, true);
            }

            if (shouldError)
            {
                // TODO: The spec says actually the remote should answer with
                // an HTTP431 error - but that happens on another layer
                await outPipe.AssertGoAwayReception(ErrorCode.ProtocolError, 0u);
                await outPipe.AssertStreamEnd();
            }
            else
            {
                await inPipe.WritePing(new byte[8], false);
                await outPipe.ReadAndDiscardPong();
            }
        }
    }
}