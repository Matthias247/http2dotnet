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

            public Task WriteAsync(ArraySegment<byte> buffer)
            {
                if (!FailNextWrite)
                {
                    return inner.WriteAsync(buffer);
                }
                return Task.FromException(new Exception("Write should fail"));
            }

            public Task CloseAsync()
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

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ConnectionShouldCloseAndSignalDoneInCaseOfAProtocolError(bool isServer)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                isServer, inPipe, outPipe, loggerProvider);

            // Cause a protocol error
            var fh = new FrameHeader
            {
                Type = FrameType.Data,
                StreamId = 0u,
                Flags = 0,
                Length = 0,
            };
            await inPipe.WriteFrameHeader(fh);
            await outPipe.AssertGoAwayReception(ErrorCode.ProtocolError, 0u);
            await outPipe.AssertStreamEnd();
            await inPipe.CloseAsync();
            
            // Expect the connection to close within timeout
            var closed = http2Con.Done;
            Assert.True(
                closed == await Task.WhenAny(closed, Task.Delay(1000)),
                "Expected connection to close");
        }

        [Fact]
        public async Task ConnectionShouldCloseAndStreamsShouldGetResetWhenExternalCloseIsRequested()
        {
            // TODO: Add a variant of this test for clients as soon as they are supported
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var res = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);

            // Close the connection
            var closeTask = res.conn.CloseNow();
            // Expect end of stream
            await outPipe.AssertStreamEnd();
            // If the connection was successfully closed close the incoming data
            // stream, since this is expected from a bidirectional stream implementation
            await inPipe.CloseAsync();
            // Close should now be completed
            await closeTask;
            // The stream should be reset
            Assert.Equal(StreamState.Reset, res.stream.State);
            // Which also means that further writes/reads should fail
            await Assert.ThrowsAsync<StreamResetException>(async () =>
            {
                await res.stream.WriteHeadersAsync(
                    ServerStreamTests.DefaultStatusHeaders, true);
            });
            await Assert.ThrowsAsync<StreamResetException>(async () =>
            {
                await res.stream.ReadAllToArray();
            });
        }
    }
}