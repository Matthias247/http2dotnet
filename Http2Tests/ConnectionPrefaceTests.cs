using System;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

using Xunit;
using Xunit.Abstractions;

using Http2;

namespace Http2Tests
{
    public class ConnectionPrefaceTests
    {
        private Connection BuildConnection(
            bool isServer,
            IReadableByteStream inputStream,
            IWriteAndCloseableByteStream outputStream)
        {
            ILogger logger = null;
            if (loggerProvider != null)
            {
                logger = loggerProvider.CreateLogger("http2Con");
            }

            // Decrease the timeout for the preface,
            // as this speeds the test up
            var config =
                new ConnectionConfigurationBuilder(isServer)
                .UseStreamListener((s) => false)
                .UseClientPrefaceTimeout(200)
                .Build();

            return new Connection(
                config, inputStream, outputStream,
                new Connection.Options
                {
                    Logger = logger,
                });
        }

        private readonly ILoggerProvider loggerProvider;

        public ConnectionPrefaceTests(ITestOutputHelper outputHelper)
        {
            this.loggerProvider = new XUnitOutputLoggerProvider(outputHelper);
        }

        [Fact]
        public async Task ClientShouldSendPrefaceAtStartup()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = BuildConnection(false, inPipe, outPipe);

            var b = new byte[ClientPreface.Length];
            await outPipe.ReadAllWithTimeout(new ArraySegment<byte>(b));
            Assert.Equal(ClientPreface.Bytes, b);
        }

        [Fact]
        public async Task ServerShouldCloseTheConnectionIfCorrectPrefaceIsNotReceived()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = BuildConnection(true, inPipe, outPipe);

            var b = new byte[ClientPreface.Length];
            // Initialize with non-preface data
            for (var i = 0; i < b.Length; i++) b[i] = 10;
            await inPipe.WriteAsync(new ArraySegment<byte>(b));

            // Wait for the response - a settings frame is expected first
            // But as there's a race condition the connection could be closed
            // before or after the settings frame was fully received
            try
            {
                await outPipe.ReadAndDiscardSettings();
                var hdrBuf = new byte[FrameHeader.HeaderSize + 50];
                var header = await FrameHeader.ReceiveAsync(outPipe, hdrBuf);
                Assert.Equal(FrameType.GoAway, header.Type);
            }
            catch (Exception e)
            {
                Assert.IsType<System.IO.EndOfStreamException>(e);
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(23)]
        public async Task ServerShouldCloseTheConnectionIfNoPrefaceIsSent(
            int nrDummyPrefaceData)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = BuildConnection(true, inPipe, outPipe);

            // Write some dummy data
            // All this data is not long enough to be a preface, so the
            // preface reception should time out
            if (nrDummyPrefaceData != 0)
            {
                var b = new byte[nrDummyPrefaceData];
                for (var i = 0; i < b.Length; i++) b[i] = 10;
                await inPipe.WriteAsync(new ArraySegment<byte>(b));
            }

            // Settings will be sent by connection before the preface is
            // checked - so they must be discarded
            await outPipe.ReadAndDiscardSettings();

            // Wait for the stream to end within 400ms.
            // This is longer than the timeout in the connection waiting for the
            // preface

            var buf = new byte[1];
            var readTask = outPipe.ReadAsync(new ArraySegment<byte>(buf)).AsTask();
            var timeoutTask = Task.Delay(400);
            var finishedTask = await Task.WhenAny(
                new Task[]{ readTask, timeoutTask });
            if (ReferenceEquals(finishedTask, readTask))
            {
                var res = readTask.Result;
                Assert.Equal(true, res.EndOfStream);
                Assert.Equal(0, res.BytesRead);
                // Received end of stream
                return;
            }
            Assert.True(false,
                "Expected connection to close outgoing stream. " +
                "Got timeout");
        }
    }
}