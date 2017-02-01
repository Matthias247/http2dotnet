using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

using Http2;
using Http2.Hpack;

namespace Http2Tests
{
    public class FlowControlTests
    {
        private ILoggerProvider loggerProvider;

        private readonly HeaderField[] DefaultGetHeaders = new HeaderField[]
        {
            new HeaderField { Name = ":method", Value = "GET" },
            new HeaderField { Name = ":scheme", Value = "http" },
            new HeaderField { Name = ":path", Value = "/" },
        };

        public FlowControlTests(ITestOutputHelper outHelper)
        {
            loggerProvider = new XUnitOutputLoggerProvider(outHelper);
        }

        [Theory]
        [InlineData(new int[]{1}, new int[]{0}, new int[]{0})]
        [InlineData(new int[]{7999}, new int[]{0}, new int[]{0})]
        [InlineData(new int[]{7999,0}, new int[]{0,0}, new int[]{0,0})]
        [InlineData(new int[]{8001}, new int[]{0}, new int[]{0})]
        [InlineData(new int[]{8001,0}, new int[]{8001,0}, new int[]{0,0})]
        [InlineData(new int[]{16000}, new int[]{0}, new int[]{0})]
        [InlineData(new int[]{16000,0}, new int[]{16000,0}, new int[]{0,0})]
        [InlineData(new int[]{16000,16000}, new int[]{16000,0}, new int[]{0,0})]
        [InlineData(new int[]{16000,16000,0}, new int[]{16000,16000,0}, new int[]{0,0,0})]
        [InlineData(
            new int[]{16000,16000,2000},
            new int[]{16000,16000,0},
            new int[]{0,0,34000})]
        [InlineData(
            new int[]{16000,16000,2000,14000,16000,4000},
            new int[]{16000,16000,0,16000,16000,0},
            new int[]{0,0,34000,0,0,34000})]
        public async Task SendingLargeAmountOfDataShouldTriggerWindowUpdates(
            int[] dataLength,
            int[] expectedStreamWindowUpdates,
            int[] expectedConnWindowUpdates)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            Func<IStream, bool> listener = (s) =>
            {
                Task.Run(async () =>
                {
                    await s.ReadHeadersAsync();
                    var data = await s.ReadAllToArrayWithTimeout();
                });
                return true;
            };

            ILogger logger = null;
            if (loggerProvider != null)
            {
                logger = loggerProvider.CreateLogger("http2Con");
            }

            // Lower the initial window size so that stream window updates are
            // sent earlier than connection window updates
            var settings = Settings.Default;
            settings.InitialWindowSize = 16000;
            var http2Con = new Connection(new Connection.Options
            {
                InputStream = inPipe,
                OutputStream = outPipe,
                IsServer = true,
                Settings = settings,
                Logger = logger,
                StreamListener = listener,
            });
            await http2Con.PerformHandshakes(inPipe, outPipe);

            var hEncoder = new Encoder();
            await inPipe.WriteHeaders(hEncoder, 1, false, DefaultGetHeaders);

            for (var i = 0; i < dataLength.Length; i++)
            {
                var toSend = dataLength[i];
                var isEndOfStream = i == (dataLength.Length - 1);
                var flags = isEndOfStream ? DataFrameFlags.EndOfStream : 0;
                var fh = new FrameHeader
                {
                    Type = FrameType.Data,
                    StreamId = 1,
                    Flags = (byte)flags,
                    Length = toSend,
                };
                await inPipe.WriteFrameHeaderWithTimeout(fh);
                var fdata = new byte[toSend];
                await inPipe.WriteWithTimeout(new ArraySegment<byte>(fdata));
                // Wait for a short amount of time between DATA frames
                if (!isEndOfStream) await Task.Delay(5);
                // Check if window updates were received if required
                if (expectedConnWindowUpdates[i] != 0)
                {
                    await outPipe.AssertWindowUpdate(0, expectedConnWindowUpdates[i]);
                }
                if (expectedStreamWindowUpdates[i] != 0)
                {
                    await outPipe.AssertWindowUpdate(1, expectedStreamWindowUpdates[i]);
                }
            }
        }

        [Fact]
        public async Task WritesShouldRespectStreamFlowControlWindow()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            // Create a stream. It will have a flow control window of 64kb
            // for stream and connection
            var res = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);
            // Initiate response by sending and consuming headers
            await res.stream.WriteHeadersAsync(
                ServerStreamTests.DefaultStatusHeaders, false);
            await outPipe.ReadAndDiscardHeaders(1u, false);
            // Increase flow control window for connection so that write is blocked
            // on stream window
            await inPipe.WriteWindowUpdate(0, 1024*1024);
            var writeTask = Task.Run(async () =>
            {
                await res.stream.WriteAsync(new ArraySegment<byte>(new byte[32000]));
                await res.stream.WriteAsync(new ArraySegment<byte>(new byte[33535 + 1024]));
                await res.stream.WriteAsync(new ArraySegment<byte>(new byte[1024]));
            });
            // Expect to read the stream flow control window amount of data
            await outPipe.ReadAndDiscardData(1u, false, 16384);
            await outPipe.ReadAndDiscardData(1u, false, 15616);
            await outPipe.ReadAndDiscardData(1u, false, 16384);
            await outPipe.ReadAndDiscardData(1u, false, 16384);
            await outPipe.ReadAndDiscardData(1u, false, 767);
            // Give a bigger flow control window for stream and expect more data
            await inPipe.WriteWindowUpdate(1u, 512);
            await outPipe.ReadAndDiscardData(1u, false, 512);
            await inPipe.WriteWindowUpdate(1u, 1024);
            await outPipe.ReadAndDiscardData(1u, false, 512);
            await outPipe.ReadAndDiscardData(1u, false, 512);
            await inPipe.WriteWindowUpdate(1u, 512);
            await outPipe.ReadAndDiscardData(1u, false, 512);
            // Expect the writer to finish
            var doneTask = await Task.WhenAny(writeTask, Task.Delay(250));
            Assert.True(writeTask == doneTask, "Expected write task to finish");
        }

        [Fact]
        public async Task WritesShouldRespectConnectionFlowControlWindow()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            // Create a stream. It will have a flow control window of 64kb
            // for stream and connection
            var res = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);
            // Initiate response by sending and consuming headers
            await res.stream.WriteHeadersAsync(
                ServerStreamTests.DefaultStatusHeaders, false);
            await outPipe.ReadAndDiscardHeaders(1u, false);
            // Increase flow control window for stream so that write is blocked
            // on connection window
            await inPipe.WriteWindowUpdate(1, 1024*1024);
            var writeTask = Task.Run(async () =>
            {
                await res.stream.WriteAsync(new ArraySegment<byte>(new byte[32000]));
                await res.stream.WriteAsync(new ArraySegment<byte>(new byte[33535+1024]));
                await res.stream.WriteAsync(new ArraySegment<byte>(new byte[1024]));
            });
            // Expect to read the stream flow control window amount of data
            await outPipe.ReadAndDiscardData(1u, false, 16384);
            await outPipe.ReadAndDiscardData(1u, false, 15616);
            await outPipe.ReadAndDiscardData(1u, false, 16384);
            await outPipe.ReadAndDiscardData(1u, false, 16384);
            await outPipe.ReadAndDiscardData(1u, false, 767);
            // Give a bigger flow control window for connection and expect more data
            await inPipe.WriteWindowUpdate(0u, 512);
            await outPipe.ReadAndDiscardData(1u, false, 512);
            await inPipe.WriteWindowUpdate(0u, 1024);
            await outPipe.ReadAndDiscardData(1u, false, 512);
            await outPipe.ReadAndDiscardData(1u, false, 512);
            await inPipe.WriteWindowUpdate(0u, 512);
            await outPipe.ReadAndDiscardData(1u, false, 512);
            // Expect the writer to finish
            var doneTask = await Task.WhenAny(writeTask, Task.Delay(250));
            Assert.True(writeTask == doneTask, "Expected write task to finish");
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task SendingEmptyDataFramesShouldBePossibleWithoutFlowWindow(
            bool emptyFrameIsEndOfStream)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            // Create a stream. It will have a flow control window of 64kb
            // for stream and connection
            var res = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);
            // Initiate response by sending and consuming headers
            await res.stream.WriteHeadersAsync(
                ServerStreamTests.DefaultStatusHeaders, false);
            await outPipe.ReadAndDiscardHeaders(1u, false);
            var writeTask = Task.Run(async () =>
            {
                // Consume the complete flow control window
                await res.stream.WriteAsync(new ArraySegment<byte>(new byte[65535]));
                // And try to send an empty data frame
                await res.stream.WriteAsync(
                    new ArraySegment<byte>(new byte[0]), emptyFrameIsEndOfStream);
            });
            // Expect to read the sent frames
            await outPipe.ReadAndDiscardData(1u, false, 16384);
            await outPipe.ReadAndDiscardData(1u, false, 16384);
            await outPipe.ReadAndDiscardData(1u, false, 16384);
            await outPipe.ReadAndDiscardData(1u, false, 16383);
            await outPipe.ReadAndDiscardData(1u, emptyFrameIsEndOfStream, 0);
            // Expect the writer to finish
            var doneTask = await Task.WhenAny(writeTask, Task.Delay(250));
            Assert.True(writeTask == doneTask, "Expected write task to finish");
        }

        [Fact]
        public async Task BlockedWritesShouldUnblockOnExternalStreamReset()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            // Create a stream. It will have a flow control window of 64kb
            // for stream and connection
            var res = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);
            // Initiate response by sending and consuming headers
            await res.stream.WriteHeadersAsync(
                ServerStreamTests.DefaultStatusHeaders, false);
            await outPipe.ReadAndDiscardHeaders(1u, false);
            // Start discarding all ougoing data -> Not needed for this test
            // and pipe may not be blocked
            var readTask = Task.Run(async () =>
            {
                await outPipe.ReadAllToArrayWithTimeout();
            });
            // Write flow control window amount of data
            await res.stream.WriteWithTimeout(new ArraySegment<byte>(new byte[65535]));
            var resetTask = Task.Run(async () =>
            {
                await Task.Delay(20);
                await inPipe.WriteResetStream(1u, ErrorCode.Cancel);
            });
            // Write additional bytes. This should block and cause a streamreset
            // exception when the cancel arrives
            await Assert.ThrowsAsync<StreamResetException>(async () =>
            {
                await res.stream.WriteAsync(new ArraySegment<byte>(new byte[1024]));
            });
        }

        [Fact]
        public async Task BlockedWritesShouldUnblockOnStreamCancel()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            // Create a stream. It will have a flow control window of 64kb
            // for stream and connection
            var res = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);
            // Initiate response by sending and consuming headers
            await res.stream.WriteHeadersAsync(
                ServerStreamTests.DefaultStatusHeaders, false);
            await outPipe.ReadAndDiscardHeaders(1u, false);
            // Start discarding all ougoing data -> Not needed for this test
            // and pipe may not be blocked
            var readTask = Task.Run(async () =>
            {
                await outPipe.ReadAllToArrayWithTimeout();
            });
            // Write flow control window amount of data
            await res.stream.WriteWithTimeout(new ArraySegment<byte>(new byte[65535]));
            var resetTask = Task.Run(async () =>
            {
                await Task.Delay(20);
                res.stream.Cancel();
            });
            // Write additional bytes. This should block and cause a streamreset
            // exception when the cancel arrives
            await Assert.ThrowsAsync<StreamResetException>(async () =>
            {
                await res.stream.WriteAsync(new ArraySegment<byte>(new byte[1024]));
            });
        }

        [Fact]
        public async Task ShouldAllowToSetTheMaxPossibleConnectionFlowControlWindowSize()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider, null);

            var amount = int.MaxValue - 65535;
            await inPipe.WriteWindowUpdate(0, amount);
            // Check aliveness with ping/pong
            await inPipe.WritePing(new byte[8], false);
            await outPipe.ReadAndDiscardPong();
        }

        [Theory]
        [InlineData(int.MaxValue - 65535 + 1)]
        [InlineData(int.MaxValue)]
        public async Task ShouldGoAwayWhenConnectionFlowControlWindowIsOverloaded(
            int amount)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider, null);

            await inPipe.WriteWindowUpdate(0, amount);
            await outPipe.AssertGoAwayReception(ErrorCode.FlowControlError, 0);
            await outPipe.AssertStreamEnd();
        }

        [Fact]
        public async Task ShouldAllowToSetTheMaxPossibleStreamFlowControlWindowSize()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            Func<IStream, bool> listener = (s) => true;
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider, listener);

            var hEncoder = new Encoder();
            await inPipe.WriteHeaders(hEncoder, 1, false, DefaultGetHeaders);

            var amount = int.MaxValue - 65535;
            await inPipe.WriteWindowUpdate(1, amount);
            // Check aliveness with ping/pong
            await inPipe.WritePing(new byte[8], false);
            await outPipe.ReadAndDiscardPong();
        }

        [Theory]
        [InlineData(int.MaxValue - 65535 + 1)]
        [InlineData(int.MaxValue)]
        public async Task ShouldResetStreamWhenStreamFlowControlWindowIsOverloaded(
            int amount)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            Func<IStream, bool> listener = (s) => true;
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider, listener);

            var hEncoder = new Encoder();
            await inPipe.WriteHeaders(hEncoder, 1, false, DefaultGetHeaders);

            await inPipe.WriteWindowUpdate(1, amount);
            await outPipe.AssertResetStreamReception(1, ErrorCode.FlowControlError);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        public async Task ReceivingWindowUpdatesWith0AmountShouldTriggerGoAwayOrReset(
            uint streamId)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var res = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);

            await inPipe.WriteWindowUpdate(streamId, 0);
           if (streamId == 0)
           {
               await outPipe.AssertGoAwayReception(ErrorCode.ProtocolError, 1);
               await outPipe.AssertStreamEnd();
           }
           else
           {
               await outPipe.AssertResetStreamReception(1u, ErrorCode.ProtocolError);
           }
        }
    }
}