using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

using Http2;

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
    }
}