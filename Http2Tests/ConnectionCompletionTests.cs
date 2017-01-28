using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

using Http2;

namespace Http2Tests
{
    public class ConnectionCompletionTests
    {
        private ILoggerProvider loggerProvider;

        public ConnectionCompletionTests(ITestOutputHelper outHelper)
        {
            loggerProvider = new XUnitOutputLoggerProvider(outHelper);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ConnectionShouldSignalDoneWhenInputIsClosed(bool isServer)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                isServer, inPipe, outPipe, loggerProvider);

            await inPipe.CloseAsync();
            // Expect the connection to close within timeout
            var closed = http2Con.Done;
            Assert.True(
                closed == await Task.WhenAny(closed, Task.Delay(1000)),
                "Expected connection to close");
        }

        class FailingPipe
            : IWriteAndCloseableByteStream, IReadableByteStream, IBufferedPipe
        {
            public bool FailNextRead = false;
            public bool FailNextWrite = false;
            public bool CloseCalled = false;
            IBufferedPipe inner;

            public FailingPipe(IBufferedPipe inner)
            {
                this.inner = inner;
            }

            public ValueTask<object> WriteAsync(ArraySegment<byte> buffer)
            {
                if (!FailNextWrite)
                {
                    return inner.WriteAsync(buffer);
                }
                throw new Exception("Write should fail");
            }

            public ValueTask<object> CloseAsync()
            { 
                this.CloseCalled = true;
                return inner.CloseAsync();
            }

            public ValueTask<StreamReadResult> ReadAsync(ArraySegment<byte> buffer)
            {
                if (!FailNextRead)
                {
                    return inner.ReadAsync(buffer);
                }
                throw new Exception("Read should fail");
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ConnectionShouldCloseAndSignalDoneWhenReadingFromInputFails(bool isServer)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var failableInPipe = new FailingPipe(inPipe);
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                isServer, failableInPipe, outPipe, loggerProvider);

            // Make the next write attempt fail
            failableInPipe.FailNextRead = true;
            // Send something which triggers no response but will start a new read call
            await inPipe.WriteWindowUpdate(0, 128);
            // Wait for the connection to close the outgoing part
            await outPipe.AssertStreamEnd();
            // If the connection was successfully closed close the incoming data
            // stream, since this is expected from a bidirectional stream implementation
            await inPipe.CloseAsync();
            // Expect the connection to close within timeout
            var closed = http2Con.Done;
            Assert.True(
                closed == await Task.WhenAny(closed, Task.Delay(1000)),
                "Expected connection to close");
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ConnectionShouldCloseAndSignalDoneWhenWritingToOutputFails(bool isServer)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var failableOutPipe = new FailingPipe(outPipe);
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                isServer, inPipe, failableOutPipe, loggerProvider);

            // Make the next write attempt fail
            failableOutPipe.FailNextWrite = true;
            // Send something which triggers a response
            await inPipe.WritePing(new byte[8], false);
            // Wait for the connection to close the outgoing part
            await outPipe.AssertStreamEnd();
            Assert.True(failableOutPipe.CloseCalled);
            // If the connection was successfully closed close the incoming data
            // stream, since this is expected from a bidirectional stream implementation
            await inPipe.CloseAsync();
            // Expect the connection to close within timeout
            var closed = http2Con.Done;
            Assert.True(
                closed == await Task.WhenAny(closed, Task.Delay(1000)),
                "Expected connection to close");
        }

        // TODO: Add a test if connection closes if external close is requested
        // as soon as a suitable API was integrated
    }
}