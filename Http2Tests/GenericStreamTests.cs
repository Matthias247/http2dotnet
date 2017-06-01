using System;
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
    public class GenericStreamTests
    {
        private ILoggerProvider loggerProvider;

        public GenericStreamTests(ITestOutputHelper outHelper)
        {
            loggerProvider = new XUnitOutputLoggerProvider(outHelper);
        }

        [Fact]
        public async Task EmptyDataFramesShouldNotWakeupPendingReads()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var res = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);

            // Send a 0byte data frame
            await inPipe.WriteData(1u, 0);
            // Try to read - this should not unblock
            var readTask = res.stream.ReadAsync(
                new ArraySegment<byte>(new byte[1])).AsTask();

            var doneTask = await Task.WhenAny(readTask, Task.Delay(100));
            Assert.True(
                doneTask != readTask,
                "Expected read timeout but read finished");
        }

        [Fact]
        public async Task EmptyDataFramesShouldBeValidSeperatorsBeforeTrailers()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var res = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);

            // Send a 0byte data frame
            await inPipe.WriteData(1u, 0);
            // Send trailers
            await inPipe.WriteHeaders(res.hEncoder, 1u, true,
                ServerStreamTests.DefaultTrailingHeaders);
            // Check for no stream error
            await inPipe.WritePing(new byte[8], false);
            await outPipe.ReadAndDiscardPong();
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData(0, false)]
        [InlineData(1, false)]
        [InlineData(255, false)]
        [InlineData(null, true)]
        [InlineData(0, true)]
        [InlineData(1, true)]
        [InlineData(255, true)]
        public async Task ReceivingHeadersWithPaddingAndPriorityShouldBeSupported(
            int? numPadding, bool hasPrio)
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
                    headersOk = ServerStreamTests.DefaultGetHeaders.SequenceEqual(rcvdHeaders);
                    streamIdOk = s.Id == streamId;
                    streamStateOk = s.State == StreamState.Open;
                    handlerDone.Release();
                });
                return true;
            };
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider, listener);

            var hEncoder = new Encoder();
            var outBuf = new byte[Settings.Default.MaxFrameSize];
            for (var i = 0; i < nrStreams; i++)
            {
                headersOk = false;
                streamIdOk = false;
                streamStateOk = false;

                var headerOffset = 0;
                if (numPadding != null)
                {
                    outBuf[0] = (byte)numPadding;
                    headerOffset += 1;
                }
                if (hasPrio)
                {
                    // TODO: Initialize the priority data in this test properly
                    // if priority data checking gets inserted later on
                    headerOffset += 5;
                }
                var result = hEncoder.EncodeInto(
                    new ArraySegment<byte>(outBuf, headerOffset, outBuf.Length-headerOffset),
                    ServerStreamTests.DefaultGetHeaders);
                var totalLength = headerOffset + result.UsedBytes;
                if (numPadding != null) totalLength += numPadding.Value;

                var flags = (byte)HeadersFrameFlags.EndOfHeaders;
                if (numPadding != null) flags |= (byte)HeadersFrameFlags.Padded;
                if (hasPrio) flags |= (byte)HeadersFrameFlags.Priority;

                var fh = new FrameHeader
                {
                    Type = FrameType.Headers,
                    Length = totalLength,
                    Flags = (byte)flags,
                    StreamId = streamId,
                };
                await inPipe.WriteFrameHeader(fh);
                await inPipe.WriteAsync(new ArraySegment<byte>(outBuf, 0, totalLength));
                var requestDone = await handlerDone.WaitAsync(ReadableStreamTestExtensions.ReadTimeout);
                Assert.True(requestDone, "Expected handler to complete within timeout");
                Assert.True(headersOk);
                Assert.True(streamIdOk);
                Assert.True(streamStateOk);
                streamId += 2;
            }
            Assert.Equal(nrStreams, nrAcceptedStreams);
        }

        // TODO: Add checks for the cases where HPACK encoding is invalid

        [Theory]
        [InlineData(true, 0, null)]
        [InlineData(true, 1, (byte)1)]
        [InlineData(true, 255, (byte)255)]
        [InlineData(false, 0, null)]
        [InlineData(false, 1, (byte)1)]
        [InlineData(false, 255, (byte)255)]
        public async Task PaddingViolationsOnStreamsShouldLeadToGoAway(
            bool onKnownStream, int frameLength, byte? padData)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var r = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);

            var fh = new FrameHeader
            {
                Type = FrameType.Data,
                StreamId = onKnownStream ? 1u : 2u,
                Flags = (byte)DataFrameFlags.Padded,
                Length = frameLength,
            };
            await inPipe.WriteFrameHeader(fh);
            if (frameLength > 0)
            {
                var data = new byte[frameLength];
                data[0] = padData.Value;
                await inPipe.WriteAsync(new ArraySegment<byte>(data));
            }
            await outPipe.AssertGoAwayReception(ErrorCode.ProtocolError, 1u);
        }

        // TODO: Check if HEADER frames with invalid padding lead to GOAWAY

        [Fact]
        public async Task OutgoingDataShouldRespectMaxFrameSize()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var r = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);
            await r.stream.WriteHeadersAsync(ServerStreamTests.DefaultStatusHeaders, false);
            await outPipe.ReadAndDiscardHeaders(1u, false);

            var dataSize = Settings.Default.MaxFrameSize + 10;
            var writeTask = Task.Run(async () =>
            {
                var data = new byte[dataSize];
                await r.stream.WriteAsync(new ArraySegment<byte>(data), true);
            });

            // Expect to receive the data in fragments
            await outPipe.ReadAndDiscardData(1u, false, (int)Settings.Default.MaxFrameSize);
            await outPipe.ReadAndDiscardData(1u, true, 10);

            var doneTask = await Task.WhenAny(writeTask, Task.Delay(250));
            Assert.True(writeTask == doneTask, "Expected write task to finish");
        }

        [Theory]
        [InlineData(true, 1u)]
        [InlineData(true, 3u)]
        [InlineData(true, 5u)]
        public async Task ADataFrameOnAnUnknownStreamIdShouldTriggerAStreamReset(
            bool isServer, uint streamId)
        {
            // TODO: Add test cases for clients
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            Func<IStream, bool> listener = (s) => true;
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                isServer, inPipe, outPipe, loggerProvider, listener);

            // Establish a high stream ID, which means all below are invalid
            var hEncoder = new Encoder();
            var createdStreamId = 111u;
            if (!isServer)
                throw new Exception("For clients the stream must be created from connection");
            await inPipe.WriteHeaders(
                hEncoder, createdStreamId, false, ServerStreamTests.DefaultGetHeaders);

            await inPipe.WriteData(streamId, 0);
            await outPipe.AssertResetStreamReception(streamId, ErrorCode.StreamClosed);
        }

        [Theory]
        [InlineData(true, 1u)]
        [InlineData(true, 2u)]
        [InlineData(true, 3u)]
        [InlineData(true, 4u)]
        [InlineData(false, 1u)]
        [InlineData(false, 2u)]
        [InlineData(false, 3u)]
        [InlineData(false, 4u)]
        public async Task ADataFrameOnAnIdleStreamIdShouldTriggerAGoAway(
            bool isServer, uint streamId)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            Func<IStream, bool> listener = (s) => true;
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                isServer, inPipe, outPipe, loggerProvider, listener);

            await inPipe.WriteData(streamId, 0);
            await outPipe.AssertGoAwayReception(ErrorCode.StreamClosed, 0u);
        }
    }
}