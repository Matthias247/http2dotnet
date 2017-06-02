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

        public static class StreamCreator
        {
            public struct Result
            {
                public Encoder hEncoder;
                public Connection conn;
                public IStream stream;
            }

            public static async Task<Result> CreateConnectionAndStream(
                StreamState state,
                ILoggerProvider loggerProvider,
                IBufferedPipe iPipe, IBufferedPipe oPipe,
                Settings? localSettings = null,
                Settings? remoteSettings = null,
                HuffmanStrategy huffmanStrategy = HuffmanStrategy.Never)
            {
                if (state == StreamState.Idle)
                {
                    throw new Exception("Not supported");
                }

                var hEncoder = new Encoder();
                var conn = await ConnectionUtils.BuildEstablishedConnection(
                    false, iPipe, oPipe, loggerProvider, null,
                    localSettings: localSettings,
                    remoteSettings: remoteSettings,
                    huffmanStrategy: huffmanStrategy);

                var endOfStream = false;
                if (state == StreamState.HalfClosedLocal ||
                    state == StreamState.Closed)
                    endOfStream = true;
                var stream = await conn.CreateStream(
                    DefaultGetHeaders, endOfStream: endOfStream);
                await oPipe.ReadAndDiscardHeaders(1u, endOfStream);

                if (state == StreamState.HalfClosedRemote ||
                    state == StreamState.Closed)
                {
                    var outBuf = new byte[Settings.Default.MaxFrameSize];
                    var result = hEncoder.EncodeInto(
                        new ArraySegment<byte>(outBuf),
                        DefaultStatusHeaders);
                    await iPipe.WriteFrameHeaderWithTimeout(
                        new FrameHeader
                        {
                            Type = FrameType.Headers,
                            Flags = (byte)(HeadersFrameFlags.EndOfHeaders |
                                           HeadersFrameFlags.EndOfStream),
                            StreamId = 1u,
                            Length = result.UsedBytes,
                        });
                    await iPipe.WriteAsync(new ArraySegment<byte>(outBuf, 0, result.UsedBytes));
                    var readHeadersTask = stream.ReadHeadersAsync();
                    var combined = await Task.WhenAny(readHeadersTask, Task.Delay(
                        ReadableStreamTestExtensions.ReadTimeout));
                    Assert.True(readHeadersTask == combined, "Expected to receive headers");
                    var headers = await readHeadersTask;
                    Assert.True(headers.SequenceEqual(DefaultStatusHeaders));
                    // Consume the data - which should be empty
                    var data = await stream.ReadAllToArrayWithTimeout();
                    Assert.Equal(0, data.Length);
                }
                else if (state == StreamState.Reset)
                {
                    await iPipe.WriteResetStream(1u, ErrorCode.Cancel);
                    await Assert.ThrowsAsync<StreamResetException>(
                        () => stream.ReadHeadersAsync());
                }

                return new Result
                {
                    hEncoder = hEncoder,
                    conn = conn,
                    stream = stream,
                };
            }
        }

        [Theory]
        [InlineData(StreamState.Open)]
        [InlineData(StreamState.HalfClosedLocal)]
        [InlineData(StreamState.HalfClosedRemote)]
        [InlineData(StreamState.Closed)]
        [InlineData(StreamState.Reset)]
        public async Task StreamCreatorShouldCreateStreamInCorrectState(
            StreamState state)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var res = await StreamCreator.CreateConnectionAndStream(
                state, loggerProvider, inPipe, outPipe);
            Assert.NotNull(res.stream);
            Assert.Equal(state, res.stream.State);
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

        [Fact]
        public async Task ReceivingDataBeforeHeadersShouldYieldAResetException()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var res = await StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider,
                inPipe, outPipe);

            await inPipe.WriteData(1u, 1);
            await outPipe.AssertResetStreamReception(1u, ErrorCode.ProtocolError);
            var ex = await Assert.ThrowsAsync<AggregateException>(
                () => res.stream.ReadWithTimeout(new ArraySegment<byte>(
                    new byte[1])));
            Assert.IsType<StreamResetException>(ex.InnerException);
            Assert.Equal(StreamState.Reset, res.stream.State);
        }

        [Fact]
        public async Task ReceivingResetShouldYieldAResetException()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var conn = await ConnectionUtils.BuildEstablishedConnection(
                false, inPipe, outPipe, loggerProvider);
            IStream stream = await conn.CreateStream(DefaultGetHeaders);
            await outPipe.ReadAndDiscardHeaders(1u, false);

            var readTask = stream.ReadWithTimeout(new ArraySegment<byte>(new byte[1]));
            await inPipe.WriteResetStream(1u, ErrorCode.Cancel);
            var ex = await Assert.ThrowsAsync<AggregateException>(
                () => readTask);
            Assert.IsType<StreamResetException>(ex.InnerException);
            Assert.Equal(StreamState.Reset, stream.State);
        }

        [Fact]
        public async Task CreatingStreamsOnServerConnectionShouldYieldException()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var conn = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider);
            var ex = await Assert.ThrowsAsync<NotSupportedException>(
                () => conn.CreateStream(DefaultGetHeaders));
            Assert.Equal("Streams can only be created for clients", ex.Message);
        }
    }
}