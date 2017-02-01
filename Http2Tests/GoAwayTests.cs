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
            var hEncoder = new Encoder();
            await inPipe.WriteHeaders(hEncoder, 3, true, ServerStreamTests.DefaultGetHeaders);
            // Expect a stream rejection
            await outPipe.AssertResetStreamReception(3u, ErrorCode.RefusedStream);
        }
    }
}