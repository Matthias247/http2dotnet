using System;
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
    public class GenericStreamTests
    {
        private ILoggerProvider loggerProvider;

        public GenericStreamTests(ITestOutputHelper outHelper)
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

            /// <summary>
            /// Creates a bidirectionally open stream,
            /// where the headers in both directions have already been sent but
            /// no data.
            /// </summary>
            public static async Task<Result> CreateConnectionAndStream(
                bool isServer,
                ILoggerProvider loggerProvider,
                IBufferedPipe iPipe, IBufferedPipe oPipe,
                Settings? localSettings = null,
                Settings? remoteSettings = null,
                HuffmanStrategy huffmanStrategy = HuffmanStrategy.Never)
            {
                var result = new Result();

                if (isServer)
                {
                    var r1 = await ServerStreamTests.StreamCreator.CreateConnectionAndStream(
                        StreamState.Open, loggerProvider,
                        iPipe, oPipe,
                        localSettings, remoteSettings,
                        huffmanStrategy);

                    // Headers have been received but not sent
                    await r1.stream.WriteHeadersAsync(DefaultStatusHeaders, false);
                    await oPipe.ReadAndDiscardHeaders(1u, false);

                    result.conn = r1.conn;
                    result.hEncoder = r1.hEncoder;
                    result.stream = r1.stream;
                }
                else
                {
                    var r1 = await ClientStreamTests.StreamCreator.CreateConnectionAndStream(
                        StreamState.Open, loggerProvider,
                        iPipe, oPipe,
                        localSettings, remoteSettings,
                        huffmanStrategy);

                    // Headers have been sent but not yet received
                    await iPipe.WriteHeaders(r1.hEncoder, 1u, false, DefaultStatusHeaders, true);

                    result.conn = r1.conn;
                    result.hEncoder = r1.hEncoder;
                    result.stream = r1.stream;
                }

                return result;
            }
        }

        [Theory]
        [InlineData(true, new int[]{0})]
        [InlineData(true, new int[]{1})]
        [InlineData(true, new int[]{100})]
        [InlineData(true, new int[]{99, 784})]
        [InlineData(true, new int[]{12874, 16384, 16383})]
        [InlineData(false, new int[]{0})]
        [InlineData(false, new int[]{1})]
        [InlineData(false, new int[]{100})]
        [InlineData(false, new int[]{99, 784})]
        [InlineData(false, new int[]{12874, 16384, 16383})]
        public async Task DataShouldBeCorrectlySent(
            bool isServer, int[] dataLength)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var r = await StreamCreator.CreateConnectionAndStream(
                isServer, loggerProvider, inPipe, outPipe);

            var totalToSend = dataLength.Aggregate(0, (sum, n) => sum+n);

            var writeTask = Task.Run(async () =>
            {
                byte nr = 0;
                for (var i = 0; i < dataLength.Length; i++)
                {
                    var toSend = dataLength[i];
                    var isEndOfStream = i == (dataLength.Length - 1);
                    var buffer = new byte[toSend];
                    for (var j = 0; j < toSend; j++)
                    {
                        buffer[j] = nr;
                        nr++;
                        if (nr > 122) nr = 0;
                    }
                    await r.stream.WriteAsync(
                        new ArraySegment<byte>(buffer), isEndOfStream);
                }
            });

            var data = new byte[totalToSend];
            var offset = 0;
            for (var i = 0; i < dataLength.Length; i++)
            {
                var fh = await outPipe.ReadFrameHeaderWithTimeout();
                Assert.Equal(FrameType.Data, fh.Type);
                Assert.Equal(1u, fh.StreamId);
                var expectEOS = i == dataLength.Length - 1;
                var gotEOS = (fh.Flags & (byte)DataFrameFlags.EndOfStream) != 0;
                Assert.Equal(expectEOS, gotEOS);
                Assert.Equal(dataLength[i], fh.Length);

                var part = new byte[fh.Length];
                await outPipe.ReadAllWithTimeout(new ArraySegment<byte>(part));
                Array.Copy(part, 0, data, offset, fh.Length);
                offset += fh.Length;
            }

            var doneTask = await Task.WhenAny(writeTask, Task.Delay(250));
            Assert.True(writeTask == doneTask, "Expected write task to finish");

            // Check if the correct data was received
            var expected = 0;
            for (var j = 0; j < totalToSend; j++)
            {
                Assert.Equal(expected, data[j]);
                expected++;
                if (expected > 122) expected = 0;
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task EmptyDataFramesShouldNotWakeupPendingReads(
            bool isServer)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var res = await StreamCreator.CreateConnectionAndStream(
                isServer, loggerProvider, inPipe, outPipe);

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

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task EmptyDataFramesShouldBeValidSeperatorsBeforeTrailers(
            bool isServer)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var res = await StreamCreator.CreateConnectionAndStream(
                isServer, loggerProvider, inPipe, outPipe);

            // Send a 0byte data frame
            await inPipe.WriteData(1u, 0);
            // Send trailers
            await inPipe.WriteHeaders(res.hEncoder, 1u, true,
                DefaultTrailingHeaders);
            // Check for no stream error
            await inPipe.WritePing(new byte[8], false);
            await outPipe.ReadAndDiscardPong();
        }

        [Theory]
        [InlineData(true, new int[]{0})]
        [InlineData(true, new int[]{1})]
        [InlineData(true, new int[]{100})]
        [InlineData(true, new int[]{99, 784})]
        [InlineData(true, new int[]{12874, 16384, 16383})]
        [InlineData(false, new int[]{0})]
        [InlineData(false, new int[]{1})]
        [InlineData(false, new int[]{100})]
        [InlineData(false, new int[]{99, 784})]
        [InlineData(false, new int[]{12874, 16384, 16383})]
        public async Task DataFromDataFramesShouldBeReceived(
            bool isServer, int[] dataLength)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var res = await StreamCreator.CreateConnectionAndStream(
                isServer, loggerProvider, inPipe, outPipe);

            var readTask = res.stream.ReadAllToArrayWithTimeout();

            var totalToSend = dataLength.Aggregate(0, (sum, n) => sum+n);

            byte nr = 0;
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
                for (var j = 0; j < toSend; j++)
                {
                    fdata[j] = nr;
                    nr++;
                    if (nr > 122) nr = 0;
                }
                await inPipe.WriteWithTimeout(new ArraySegment<byte>(fdata));
                // Wait for a short amount of time between DATA frames
                if (!isEndOfStream) await Task.Delay(10);
            }

            var doneTask = await Task.WhenAny(
                readTask, Task.Delay(ReadableStreamTestExtensions.ReadTimeout));
            Assert.True(doneTask == readTask, "Expected read task to complete within timeout");

            byte[] receivedData = await readTask;
            Assert.NotNull(receivedData);
            Assert.Equal(totalToSend, receivedData.Length);
            var expected = 0;
            for (var j = 0; j < totalToSend; j++)
            {
                Assert.Equal(expected, receivedData[j]);
                expected++;
                if (expected > 122) expected = 0;
            }
        }

        [Theory]
        [InlineData(true, 20, 0, 0)]
        [InlineData(true, 20, 1, 0)]
        [InlineData(true, 20, 0, 1024)]
        [InlineData(true, 20, 1, 1024)]
        [InlineData(true, 20, 255, 0)]
        [InlineData(true, 20, 255, 1024)]
        [InlineData(true, 5, 255, 64*1024 - 1)]
        [InlineData(false, 20, 0, 0)]
        [InlineData(false, 20, 1, 0)]
        [InlineData(false, 20, 0, 1024)]
        [InlineData(false, 20, 1, 1024)]
        [InlineData(false, 20, 255, 0)]
        [InlineData(false, 20, 255, 1024)]
        [InlineData(false, 5, 255, 64*1024 - 1)]
        public async Task DataFramesWithPaddingShouldBeCorrectlyReceived(
            bool isServer, int nrFrames, byte numPadding, int bytesToSend)
        {
            var inPipe = new BufferedPipe(10*1024);
            var outPipe = new BufferedPipe(10*1024);
            var receiveOk = true;

            var settings = Settings.Default;
            settings.MaxFrameSize = 100 * 1024;
            settings.InitialWindowSize = int.MaxValue;
            var r = await StreamCreator.CreateConnectionAndStream(
                isServer, loggerProvider, inPipe, outPipe,
                localSettings: settings);

            for (var nrSends = 0; nrSends < nrFrames; nrSends++)
            {
                var buf = new byte[1 + bytesToSend + numPadding];
                buf[0] = numPadding;
                byte nr = 0;
                for (var i = 0; i < bytesToSend; i++)
                {
                    buf[1+i] = nr;
                    nr++;
                    if (nr > 123) nr = 0;
                }
                var fh = new FrameHeader
                {
                    Type = FrameType.Data,
                    StreamId = 1,
                    Flags = (byte)DataFrameFlags.Padded,
                    Length = buf.Length,
                };
                await inPipe.WriteFrameHeaderWithTimeout(fh);
                await inPipe.WriteWithTimeout(new ArraySegment<byte>(buf));
            }

            for (var nrSends = 0; nrSends < nrFrames; nrSends++)
            {
                var buf1 = new byte[bytesToSend];
                await r.stream.ReadAllWithTimeout(new ArraySegment<byte>(buf1));
                var expected = 0;
                for (var j = 0; j < bytesToSend; j++)
                {
                    if (buf1[j] != expected) {
                        receiveOk = false;
                    }
                    expected++;
                    if (expected > 123) expected = 0;
                }
            }

            Assert.True(receiveOk, "Expected to receive correct data");
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
                    DefaultGetHeaders);
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

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ReceivingTrailersShouldUnblockDataReceptionAndPresentThem(
            bool isServer)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var res = await StreamCreator.CreateConnectionAndStream(
                isServer, loggerProvider, inPipe, outPipe);

            var readDataTask = res.stream.ReadAllToArrayWithTimeout();

            var fh = new FrameHeader
            {
                Type = FrameType.Data,
                Length = 4,
                StreamId = 1,
                Flags = (byte)0,
            };
            await inPipe.WriteFrameHeader(fh);
            await inPipe.WriteAsync(
                new ArraySegment<byte>(
                    System.Text.Encoding.ASCII.GetBytes("ABCD")));

            var trailers = new HeaderField[] {
                new HeaderField { Name = "trai", Value = "ler" },
            };
            await inPipe.WriteHeaders(res.hEncoder, 1, true, trailers);

            var bytes = await readDataTask;
            Assert.Equal(4, bytes.Length);
            Assert.Equal("ABCD", System.Text.Encoding.ASCII.GetString(bytes));
            Assert.Equal(StreamState.HalfClosedRemote, res.stream.State);
            var rcvdTrailers = await res.stream.ReadTrailersAsync();
            Assert.Equal(trailers, rcvdTrailers);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task TrailersShouldBeCorrectlySent(bool isServer)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var r = await StreamCreator.CreateConnectionAndStream(
                isServer, loggerProvider, inPipe, outPipe);
            await r.stream.WriteAsync(new ArraySegment<byte>(new byte[0]));
            await outPipe.ReadAndDiscardData(1u, false, 0);

            // Send trailers
            await r.stream.WriteTrailersAsync(DefaultTrailingHeaders);

            // Check the received trailers
            var fh = await outPipe.ReadFrameHeaderWithTimeout();
            Assert.Equal(FrameType.Headers, fh.Type);
            Assert.Equal(1u, fh.StreamId);
            var expectedFlags =
                HeadersFrameFlags.EndOfHeaders | HeadersFrameFlags.EndOfStream;
            Assert.Equal((byte)expectedFlags, fh.Flags);
            Assert.InRange(fh.Length, 1, 1024);
            var headerData = new byte[fh.Length];
            await outPipe.ReadAllWithTimeout(new ArraySegment<byte>(headerData));
            Assert.Equal(EncodedDefaultTrailingHeaders, headerData);
        }

        // TODO: Add a test with trailing CONTINUATION frames

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task RespondingTrailersWithoutDataShouldThrowAnException(
            bool isServer)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var r = await StreamCreator.CreateConnectionAndStream(
                isServer, loggerProvider, inPipe, outPipe);
            var ex = await Assert.ThrowsAsync<Exception>(async () =>
                await r.stream.WriteTrailersAsync(DefaultTrailingHeaders));

            Assert.Equal("Attempted to write trailers without data", ex.Message);
        }

        [Theory]
        [InlineData(true, true)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(false, false)]
        public async Task ReceivingHeaders2TimesShouldTriggerAStreamReset(
            bool isServer, bool headersAreEndOfStream)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            // Establish open streams, which means headers are sent in both
            // directions
            var res = await StreamCreator.CreateConnectionAndStream(
                isServer, loggerProvider, inPipe, outPipe);

            // Write a second header
            await inPipe.WriteHeaders(
                res.hEncoder, 1, headersAreEndOfStream, DefaultGetHeaders);

            await outPipe.AssertResetStreamReception(1, ErrorCode.ProtocolError);
            Assert.Equal(StreamState.Reset, res.stream.State);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task CancellingAStreamShouldSendAResetFrame(
            bool isServer)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var r = await StreamCreator.CreateConnectionAndStream(
                isServer, loggerProvider, inPipe, outPipe);
            r.stream.Cancel();
            await outPipe.AssertResetStreamReception(1, ErrorCode.Cancel);
            Assert.Equal(StreamState.Reset, r.stream.State);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task OutgoingDataShouldRespectMaxFrameSize(
            bool isServer)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var r = await StreamCreator.CreateConnectionAndStream(
                isServer, loggerProvider, inPipe, outPipe);

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
                hEncoder, createdStreamId, false, DefaultGetHeaders);

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

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task HeadersOnStreamId0ShouldTriggerAGoAway(
            bool isServer)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            Func<IStream, bool> listener = (s) => true;
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                isServer, inPipe, outPipe, loggerProvider, listener);

            var hEncoder = new Encoder();
            await inPipe.WriteHeaders(hEncoder, 0, false, DefaultGetHeaders);
            await outPipe.AssertGoAwayReception(ErrorCode.ProtocolError, 0);
            await outPipe.AssertStreamEnd();
        }

        [Theory]
        [InlineData(true, 2)]
        [InlineData(true, 4)]
        [InlineData(false, 1)]
        [InlineData(false, 3)]
        public async Task HeadersOnStreamIdWhichCanNotBeRemoteInitiatedShouldTriggerAStreamReset(
            bool isServer, uint streamId)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            Func<IStream, bool> listener = (s) => true;
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                isServer, inPipe, outPipe, loggerProvider, listener);

            var hEncoder = new Encoder();
            await inPipe.WriteHeaders(hEncoder, streamId, false, DefaultGetHeaders);
            await outPipe.AssertResetStreamReception(streamId, ErrorCode.StreamClosed);
        }
    }
}