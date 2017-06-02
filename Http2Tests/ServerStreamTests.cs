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
    public class ServerStreamTests
    {
        private ILoggerProvider loggerProvider;

        public ServerStreamTests(ITestOutputHelper outHelper)
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
                IStream stream = null;
                var handlerDone = new SemaphoreSlim(0);
                if (state == StreamState.Idle)
                {
                    throw new Exception("Not supported");
                }

                Func<IStream, bool> listener = (s) =>
                {
                    Task.Run(async () =>
                    {
                        stream = s;
                        try
                        {
                            await s.ReadHeadersAsync();
                            if (state == StreamState.Reset)
                            {
                                s.Cancel();
                                return;
                            }

                            if (state == StreamState.HalfClosedRemote ||
                                state == StreamState.Closed)
                            {
                                await s.ReadAllToArrayWithTimeout();
                            }

                            if (state == StreamState.HalfClosedLocal ||
                                state == StreamState.Closed)
                            {
                                await s.WriteHeadersAsync(
                                    DefaultStatusHeaders, true);
                            }
                        }
                        finally
                        {
                            handlerDone.Release();
                        }
                    });
                    return true;
                };
                var conn = await ConnectionUtils.BuildEstablishedConnection(
                    true, iPipe, oPipe, loggerProvider, listener,
                    localSettings: localSettings,
                    remoteSettings: remoteSettings,
                    huffmanStrategy: huffmanStrategy);
                var hEncoder = new Encoder();

                await iPipe.WriteHeaders(
                    hEncoder, 1, false, DefaultGetHeaders);

                if (state == StreamState.HalfClosedRemote ||
                    state == StreamState.Closed)
                {
                    await iPipe.WriteData(1u, 0, endOfStream: true);
                }

                var ok = await handlerDone.WaitAsync(
                    ReadableStreamTestExtensions.ReadTimeout);
                if (!ok) throw new Exception("Stream handler did not finish");

                if (state == StreamState.HalfClosedLocal ||
                    state == StreamState.Closed)
                {
                    // Consume the sent headers and data
                    await oPipe.ReadAndDiscardHeaders(1u, true);
                }
                else if (state == StreamState.Reset)
                {
                    // Consume the sent reset frame
                    await oPipe.AssertResetStreamReception(1, ErrorCode.Cancel);
                }

                return new Result
                {
                    conn = conn,
                    stream = stream,
                    hEncoder = hEncoder,
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
        public async Task ReceivingHeadersShouldCreateNewStreamsAndAllowToReadTheHeaders()
        {
            const int nrStreams = 10;
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            int nrAcceptedStreams = 0;
            var handlerDone = new SemaphoreSlim(0);
            uint streamId = 1;
            var headersOk = false;
            var streamIdOk = false;
            var streamStateOk = false;

            Func<IStream, bool> listener = (s) =>
            {
                Interlocked.Increment(ref nrAcceptedStreams);
                Task.Run(async () =>
                {
                    var rcvdHeaders = await s.ReadHeadersAsync();
                    headersOk = DefaultGetHeaders.SequenceEqual(rcvdHeaders);
                    streamIdOk = s.Id == streamId;
                    streamStateOk = s.State == StreamState.Open;
                    handlerDone.Release();
                });
                return true;
            };
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider, listener);

            var hEncoder = new Encoder();
            for (var i = 0; i < nrStreams; i++)
            {
                headersOk = false;
                streamIdOk = false;
                streamStateOk = false;
                await inPipe.WriteHeaders(hEncoder, streamId, false, DefaultGetHeaders);
                var requestDone = await handlerDone.WaitAsync(ReadableStreamTestExtensions.ReadTimeout);
                Assert.True(requestDone, "Expected handler to complete within timeout");
                Assert.True(headersOk);
                Assert.True(streamIdOk);
                Assert.True(streamStateOk);
                streamId += 2;
            }
            Assert.Equal(nrStreams, nrAcceptedStreams);
        }

        [Fact]
        public async Task ReceivingHeadersWithEndOfStreamShouldAllowToReadEndOfStream()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var handlerDone = new SemaphoreSlim(0);
            var gotEos = false;
            var streamStateOk = false;

            Func<IStream, bool> listener = (s) =>
            {
                Task.Run(async () =>
                {
                    await s.ReadHeadersAsync();
                    var buf = new byte[1024];
                    var res = await s.ReadAsync(new ArraySegment<byte>(buf));
                    gotEos = res.EndOfStream && res.BytesRead == 0;
                    streamStateOk = s.State == StreamState.HalfClosedRemote;
                    handlerDone.Release();
                });
                return true;
            };
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider, listener);

            var hEncoder = new Encoder();
            await inPipe.WriteHeaders(hEncoder, 1, true, DefaultGetHeaders);
            var requestDone = await handlerDone.WaitAsync(ReadableStreamTestExtensions.ReadTimeout);
            Assert.True(requestDone, "Expected handler to complete within timeout");
            Assert.True(gotEos);
            Assert.True(streamStateOk);
        }

        [Fact]
        public async Task ReceivingHeadersAndNoDataShouldBlockTheReader()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var res = await StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);

            var buf = new byte[1024];
            try
            {
                await res.stream.ReadWithTimeout(new ArraySegment<byte>(buf));
            }
            catch (TimeoutException)
            {
                return;
            }
            Assert.True(false, "Expected read timeout, but got data");
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ResponseHeadersShouldBeCorrectlySent(
            bool useEndOfStream)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var r = await StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);
            await r.stream.WriteHeadersAsync(DefaultStatusHeaders, useEndOfStream);

            // Check the received headers
            var fh = await outPipe.ReadFrameHeaderWithTimeout();
            Assert.Equal(FrameType.Headers, fh.Type);
            Assert.Equal(1u, fh.StreamId);
            var expectedFlags = HeadersFrameFlags.EndOfHeaders;
            if (useEndOfStream) expectedFlags |= HeadersFrameFlags.EndOfStream;
            Assert.Equal((byte)expectedFlags, fh.Flags);
            Assert.InRange(fh.Length, 1, 1024);
            var headerData = new byte[fh.Length];
            await outPipe.ReadAllWithTimeout(new ArraySegment<byte>(headerData));
            Assert.Equal(EncodedDefaultStatusHeaders, headerData);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task RespondingDataWithoutHeadersShouldThrowAnException(
            bool useEndOfStream)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var r = await StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);

            var ex = await Assert.ThrowsAsync<Exception>(async () =>
                await r.stream.WriteAsync(
                    new ArraySegment<byte>(new byte[8]), useEndOfStream));

            Assert.Equal("Attempted to write data before headers", ex.Message);
        }

        [Fact]
        public async Task RespondingTrailersWithoutHeadersShouldThrowAnException()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var r = await StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);

            var ex = await Assert.ThrowsAsync<Exception>(async () =>
                await r.stream.WriteTrailersAsync(DefaultTrailingHeaders));

            Assert.Equal("Attempted to write trailers without data", ex.Message);
        }

        [Fact]
        public async Task ItShouldBePossibleToSendInformationalHeaders()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var r = await StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);
            var infoHeaders = new HeaderField[]
            {
                new HeaderField { Name = ":status", Value = "100" },
                new HeaderField { Name = "extension-field", Value = "bar" },
            };
            await r.stream.WriteHeadersAsync(infoHeaders, false);
            await r.stream.WriteHeadersAsync(DefaultStatusHeaders, false);
            await r.stream.WriteAsync(new ArraySegment<byte>(new byte[0]), true);
            await outPipe.ReadAndDiscardHeaders(1, false);
            await outPipe.ReadAndDiscardHeaders(1, false);
            await outPipe.ReadAndDiscardData(1, true, 0);
        }

        [Theory]
        [InlineData(StreamState.HalfClosedRemote, true, false, false)]
        [InlineData(StreamState.HalfClosedRemote, false, true, false)]
        [InlineData(StreamState.HalfClosedRemote, false, false, true)]
        // Situations where connection is full closed - not half
        [InlineData(StreamState.Closed, true, false, false)]
        [InlineData(StreamState.Closed, false, true, false)]
        [InlineData(StreamState.Closed, false, false, true)]
        public async Task ReceivingHeadersOrDataOnAClosedStreamShouldTriggerAStreamReset(
            StreamState streamState,
            bool sendHeaders, bool sendData, bool sendTrailers)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var localCloseDone = new SemaphoreSlim(0);

            var res = await StreamCreator.CreateConnectionAndStream(
                streamState, loggerProvider, inPipe, outPipe);

            if (sendHeaders)
            {
                await inPipe.WriteHeaders(res.hEncoder, 1, false, DefaultGetHeaders);
            }
            if (sendData)
            {
                await inPipe.WriteData(1u, 0);
            }
            if (sendTrailers)
            {
                await inPipe.WriteHeaders(res.hEncoder, 1, true, DefaultGetHeaders);
            }

            await outPipe.AssertResetStreamReception(1u, ErrorCode.StreamClosed);
            var expectedState =
                streamState == StreamState.Closed
                ? StreamState.Closed
                : StreamState.Reset;
            Assert.Equal(expectedState, res.stream.State);
        }

        [Theory]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(false, false, true)]
        [InlineData(false, true, true)]
        public async Task ReceivingHeadersOrDataOnAResetStreamShouldProduceAClosedStreamError(
            bool sendHeaders, bool sendData, bool sendTrailers)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var r = await StreamCreator.CreateConnectionAndStream(
                StreamState.Reset, loggerProvider, inPipe, outPipe);

            if (sendHeaders)
            {
                await inPipe.WriteHeaders(r.hEncoder, 1, false, DefaultGetHeaders);
            }
            if (sendData)
            {
                await inPipe.WriteData(1u, 0);
            }
            if (sendTrailers)
            {
                await inPipe.WriteHeaders(r.hEncoder, 1, true, DefaultGetHeaders);
            }
            await outPipe.AssertResetStreamReception(1, ErrorCode.StreamClosed);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ReceivingHeadersWithNoAttachedListenerOrRefusingListenerShouldTriggerStreamReset(
            bool noListener)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            Func<IStream, bool> listener = null;
            if (!noListener) listener = (s) => false;
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider, listener);

            var hEncoder = new Encoder();
            await inPipe.WriteHeaders(hEncoder, 1, false, DefaultGetHeaders);
            await outPipe.AssertResetStreamReception(1, ErrorCode.RefusedStream);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ReceivingHeadersWithATooSmallStreamIdShouldTriggerAStreamReset(
            bool noListener)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            Func<IStream, bool> listener = (s) => true;
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider, listener);

            var hEncoder = new Encoder();
            await inPipe.WriteHeaders(hEncoder, 33, false, DefaultGetHeaders);
            await inPipe.WriteHeaders(hEncoder, 31, false, DefaultGetHeaders);
            // Remark: The specification actually wants a connection error / GOAWAY
            // to be emitted. However as the implementation can not safely determine
            // if that stream ID was never used and valid we send and check for
            // a stream reset.
            await outPipe.AssertResetStreamReception(31, ErrorCode.StreamClosed);
        }

        [Theory]
        [InlineData(20)]
        public async Task IncomingStreamsAfterMaxConcurrentStreamsShouldBeRejected(
            int maxConcurrentStreams)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var acceptedStreams = new List<IStream>();

            Func<IStream, bool> listener = (s) =>
            {
                lock (acceptedStreams)
                {
                    acceptedStreams.Add(s);
                }
                return true;
            };
            var settings = Settings.Default;
            settings.MaxConcurrentStreams = (uint)maxConcurrentStreams;
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider, listener,
                localSettings: settings);

            var hEncoder = new Encoder();
            // Open maxConcurrentStreams
            var streamId = 1u;
            for (var i = 0; i < maxConcurrentStreams; i++)
            {
                await inPipe.WriteHeaders(hEncoder, streamId, false, DefaultGetHeaders);
                streamId += 2;
            }
            // Assert no rejection and response so far
            await inPipe.WritePing(new byte[8], false);
            await outPipe.ReadAndDiscardPong();
            lock (acceptedStreams)
            {
                Assert.Equal(maxConcurrentStreams, acceptedStreams.Count);
            }
            // Try to open an additional stream
            await inPipe.WriteHeaders(hEncoder, streamId, false, DefaultGetHeaders);
            // This one should be rejected
            await outPipe.AssertResetStreamReception(streamId, ErrorCode.RefusedStream);
            lock (acceptedStreams)
            {
                Assert.Equal(maxConcurrentStreams, acceptedStreams.Count);
            }

            // Once a stream is closed a new one should be acceptable
            await inPipe.WriteResetStream(streamId-2, ErrorCode.Cancel);
            streamId += 2;
            await inPipe.WriteHeaders(hEncoder, streamId, false, DefaultGetHeaders);
            // Assert no error response
            await inPipe.WritePing(new byte[8], false);
            await outPipe.ReadAndDiscardPong();
            lock (acceptedStreams)
            {
                // +1 because the dead stream isn't removed
                Assert.Equal(maxConcurrentStreams+1, acceptedStreams.Count);
                // Check if the reset worked
                Assert.Equal(
                    StreamState.Reset,
                    acceptedStreams[acceptedStreams.Count-2].State);
            }
        }
    }
}