using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

using Http2;
using Http2.Hpack;

namespace Http2Tests
{
    public class GoAwayTests
    {
        private ILoggerProvider loggerProvider;

        public GoAwayTests(ITestOutputHelper outHelper)
        {
            loggerProvider = new XUnitOutputLoggerProvider(outHelper);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task GoAwayShouldBeSendable(bool withConnectionClose)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var res = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);

            // Start the GoAway process in a background task.
            // As waiting for this will block the current task in case
            // of connection close
            var closeTask = Task.Run(() =>
                res.conn.GoAwayAsync(ErrorCode.InternalError, withConnectionClose));
            // Expect the GoAway message
            await outPipe.AssertGoAwayReception(ErrorCode.InternalError, 1u);
            if (withConnectionClose)
            {
                await outPipe.AssertStreamEnd();
                await inPipe.CloseAsync();
            }
            await closeTask;
        }

        [Fact]
        public async Task InCaseOfManualGoAwayAndConnectionErrorOnlyASingleGoAwayShouldBeSent()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var res = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);

            // Send the manual GoAway
            await res.conn.GoAwayAsync(ErrorCode.NoError, false);
            // Expect to read it
            await outPipe.AssertGoAwayReception(ErrorCode.NoError, 1u);
            // And force a connection error that should not yield a further GoAway
            await inPipe.WriteSettingsAck();
            // Expect end of stream and not GoAway
            await outPipe.AssertStreamEnd();
        }

        [Fact]
        public async Task NewStreamsAfterGoAwayShouldBeRejected()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var res = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);

            // Start the GoAway process
            await res.conn.GoAwayAsync(ErrorCode.NoError, false);
            // Expect the GoAway message
            await outPipe.AssertGoAwayReception(ErrorCode.NoError, 1u);
            // Try to establish a new stream
            var hEncoder = new Http2.Hpack.Encoder();
            await inPipe.WriteHeaders(hEncoder, 3, true, ServerStreamTests.DefaultGetHeaders);
            // Expect a stream rejection
            await outPipe.AssertResetStreamReception(3u, ErrorCode.RefusedStream);
        }

        [Fact]
        public async Task RemoteGoAwayReasonTaskShouldYieldExceptionIfNoGoAwayIsSent()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var res = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);

            var readGoAwayTask = res.conn.RemoteGoAwayReason;
            await inPipe.CloseAsync();

            Assert.True(
                readGoAwayTask == await Task.WhenAny(readGoAwayTask, Task.Delay(200)),
                "Expected readGoAwayTask to finish");
            await Assert.ThrowsAsync<EndOfStreamException>(
                async () => await readGoAwayTask);
        }

        [Theory]
        [InlineData(0u, ErrorCode.Cancel, "")]
        [InlineData(2u, ErrorCode.NoError, "Im going away now")]
        public async Task RemoteGoAwayReasonShouldBeGettableFromTask(
            uint lastStreamId, ErrorCode errc, string debugString)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var res = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);

            var debugData = Encoding.ASCII.GetBytes(debugString);
            await inPipe.WriteGoAway(lastStreamId, errc, debugData);

            var readGoAwayTask = res.conn.RemoteGoAwayReason;
            Assert.True(
                readGoAwayTask == await Task.WhenAny(readGoAwayTask, Task.Delay(200)),
                "Expected to read GoAway data");

            var reason = await readGoAwayTask;
            Assert.Equal(lastStreamId, reason.LastStreamId);
            Assert.Equal(errc, reason.ErrorCode);
            Assert.Equal(debugString,
                Encoding.ASCII.GetString(
                    reason.DebugData.Array,
                    reason.DebugData.Offset,
                    reason.DebugData.Count));
        }

        [Fact]
        public async Task ASecondRemoteGoAwayShouldBeIgnored()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var res = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);

            await inPipe.WriteGoAway(0u, ErrorCode.NoError, null);
            await inPipe.WriteGoAway(2u, ErrorCode.InadequateSecurity, null);

            var readGoAwayTask = res.conn.RemoteGoAwayReason;
            Assert.True(
                readGoAwayTask == await Task.WhenAny(readGoAwayTask, Task.Delay(200)),
                "Expected to read GoAway data");

            var reason = await readGoAwayTask;
            Assert.Equal(0u, reason.LastStreamId);
            Assert.Equal(ErrorCode.NoError, reason.ErrorCode);
        }

        [Theory]
        [InlineData(false, 0)]
        [InlineData(false, 7)]
        [InlineData(false, 65536)]
        [InlineData(true, 0)]
        [InlineData(true, 7)]
        [InlineData(true, 65536)]
        public async Task ConnectionShouldGoAwayOnInvalidGoAwayFrameLength(
            bool isServer, int frameLength)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                isServer, inPipe, outPipe, loggerProvider);

            var fh = new FrameHeader
            {
                Type = FrameType.GoAway,
                Flags = 0,
                Length = frameLength,
                StreamId = 0,
            };
            await inPipe.WriteFrameHeader(fh);

            var expectedErr = frameLength > 65535
                ? ErrorCode.FrameSizeError
                : ErrorCode.ProtocolError;

            await outPipe.AssertGoAwayReception(expectedErr, 0);
            await outPipe.AssertStreamEnd();
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ConnectionShouldGoAwayOnInvalidGoAwayStreamId(
            bool isServer)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                isServer, inPipe, outPipe, loggerProvider);

            var goAwayData = new GoAwayFrameData
            {
                Reason = new GoAwayReason
                {
                    LastStreamId = 0u,
                    ErrorCode = ErrorCode.NoError,
                    DebugData = new ArraySegment<byte>(new byte[0]),
                },
            };

            var fh = new FrameHeader
            {
                Type = FrameType.GoAway,
                Flags = 0,
                StreamId = 1,
                Length = goAwayData.RequiredSize,
            };

            var dataBytes = new byte[goAwayData.RequiredSize];
            goAwayData.EncodeInto(new ArraySegment<byte>(dataBytes));
            await inPipe.WriteFrameHeader(fh);
            await inPipe.WriteAsync(new ArraySegment<byte>(dataBytes));

            await outPipe.AssertGoAwayReception(ErrorCode.ProtocolError, 0);
            await outPipe.AssertStreamEnd();
        }
    }
}