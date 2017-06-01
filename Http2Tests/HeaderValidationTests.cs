using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

using Http2;
using Http2.Hpack;

namespace Http2Tests
{
    public class HeaderValidationTests
    {
        private ILoggerProvider loggerProvider;

        public HeaderValidationTests(ITestOutputHelper outHelper)
        {
            loggerProvider = new XUnitOutputLoggerProvider(outHelper);
        }

        [Fact]
        public async Task SendingInvalidHeadersShouldTriggerAStreamReset()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var headers = new HeaderField[]
            {
                new HeaderField { Name = "method", Value = "GET" },
                new HeaderField { Name = ":scheme", Value = "http" },
                new HeaderField { Name = ":path", Value = "/" },
            };

            Func<IStream, bool> listener = (s) => true;
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider, listener);

            var hEncoder = new Encoder();
            await inPipe.WriteHeaders(hEncoder, 1, false, headers);
            await outPipe.AssertResetStreamReception(1, ErrorCode.ProtocolError);
        }

        [Fact]
        public async Task SendingInvalidTrailersShouldTriggerAStreamReset()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var headers = new HeaderField[]
            {
                new HeaderField { Name = ":method", Value = "GET" },
                new HeaderField { Name = ":scheme", Value = "http" },
                new HeaderField { Name = ":path", Value = "/" },
            };
            var trailers = new HeaderField[]
            {
                new HeaderField { Name = ":method", Value = "GET" },
            };

            Func<IStream, bool> listener = (s) => true;
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider, listener);

            var hEncoder = new Encoder();
            await inPipe.WriteHeaders(hEncoder, 1, false, headers);
            await inPipe.WriteHeaders(hEncoder, 1, true, trailers);
            await outPipe.AssertResetStreamReception(1, ErrorCode.ProtocolError);
        }

        [Fact]
        public async Task RespondingInvalidHeadersShouldTriggerAnException()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var r = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);

            var headers = new HeaderField[]
            {
                new HeaderField { Name = "status", Value = "200" },
            };
            var ex = await Assert.ThrowsAsync<Exception>(async () =>
                await r.stream.WriteHeadersAsync(headers, false));
            Assert.Equal("ErrorInvalidPseudoHeader", ex.Message);
        }

        [Fact]
        public async Task RespondingInvalidTrailersShouldTriggerAnException()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var r = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);

            var headers = new HeaderField[]
            {
                new HeaderField { Name = ":asdf", Value = "200" },
            };
            var ex = await Assert.ThrowsAsync<Exception>(async () =>
                await r.stream.WriteHeadersAsync(headers, false));
            Assert.Equal("ErrorInvalidPseudoHeader", ex.Message);
        }

        [Theory]
        [InlineData(0, new int[]{ 0 }, false, false)]
        [InlineData(2, new int[]{ 2 }, false, false)]
        [InlineData(2, new int[]{ 2 }, true, false)]
        [InlineData(0, new int[]{ 1 }, false, true)]
        [InlineData(2, new int[]{ 1 }, false, true)]
        [InlineData(2, new int[]{ 1 }, true, true)]
        [InlineData(2, new int[]{ 3 }, false, true)]
        [InlineData(2, new int[]{ 1, 2 }, false, true)]
        [InlineData(2, new int[]{ 1, 2 }, true, true)]
        [InlineData(1024, new int[]{ 512, 513 }, false, true)]
        [InlineData(1024, new int[]{ 512, 513 }, true, true)]
        public async Task HeadersWithContentLengthShouldForceDataLengthValidation(
            int contentLen, int[] dataLength, bool useTrailers, bool shouldError)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            Func<IStream, bool> listener = (s) => true;
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider, listener);

            var headers = TestHeaders.DefaultGetHeaders.Append(
                new HeaderField {
                    Name = "content-length",
                    Value = contentLen.ToString() });

            var hEncoder = new Encoder();
            await inPipe.WriteHeaders(hEncoder, 1, false, headers);

            for (var i = 0; i < dataLength.Length; i++)
            {
                var isEos = i == (dataLength.Length - 1) && !useTrailers;
                await inPipe.WriteData(1u, dataLength[i], endOfStream: isEos);
            }

            if (useTrailers)
            {
                await inPipe.WriteHeaders(hEncoder, 1, true, new HeaderField[0]);
            }

            if (shouldError)
            {
                await outPipe.AssertResetStreamReception(1u, ErrorCode.ProtocolError);
            }
            else
            {
                await inPipe.WritePing(new byte[8], false);
                await outPipe.ReadAndDiscardPong();
            }
        }
    }
}