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

namespace Http2Tests
{
    public class ServerStreamTests
    {
        private ILoggerProvider loggerProvider;

        public static readonly HeaderField[] DefaultGetHeaders = new HeaderField[]
        {
            new HeaderField { Name = ":method", Value = "GET" },
            new HeaderField { Name = ":scheme", Value = "http" },
            new HeaderField { Name = ":path", Value = "/" },
            new HeaderField { Name = "abc", Value = "def" },
        };

        public static readonly HeaderField[] DefaultStatusHeaders = new HeaderField[]
        {
            new HeaderField { Name = ":status", Value = "200" },
            new HeaderField { Name = "xyz", Value = "ghi" },
        };

        public static byte[] EncodedDefaultStatusHeaders = new byte[]
        {
            0x88, 0x40, 0x03, 0x78, 0x79, 0x7a, 0x03, 0x67, 0x68, 0x69,
        };

        public static readonly HeaderField[] DefaultTrailingHeaders = new HeaderField[]
        {
            new HeaderField { Name = "trai", Value = "ler" },
        };

        public static byte[] EncodedDefaultTrailingHeaders = new byte[]
        {
            0x40, 0x04, 0x74, 0x72, 0x61, 0x69, 0x03, 0x6c, 0x65, 0x72,
        };

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
                IBufferedPipe iPipe, IBufferedPipe oPipe)
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
                            await s.ReadHeaders();
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
                                await s.WriteHeaders(
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
                    true, iPipe, oPipe, loggerProvider, listener);
                var hEncoder = new Encoder();

                await iPipe.WriteHeaders(
                    hEncoder, 1, false, DefaultGetHeaders);

                if (state == StreamState.HalfClosedRemote ||
                    state == StreamState.Closed)
                {
                    var fh = new FrameHeader
                    {
                        Type = FrameType.Data,
                        Length = 0,
                        StreamId = 1,
                        Flags = (byte)DataFrameFlags.EndOfStream,
                    };
                    await iPipe.WriteFrameHeader(fh);
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
        public async Task SendingHeadersShouldCreateNewStreamsAndAllowToReadTheHeaders()
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
                    var rcvdHeaders = await s.ReadHeaders();
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

        [Theory]
        [InlineData(null, false)]
        [InlineData(0, false)]
        [InlineData(1, false)]
        [InlineData(255, false)]
        [InlineData(null, true)]
        [InlineData(0, true)]
        [InlineData(1, true)]
        [InlineData(255, true)]
        public async Task SendingHeadersWithPaddingAndPriorityShouldBeSupported(
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
                    var rcvdHeaders = await s.ReadHeaders();
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

        // TODO: Add checks for the cases were there's no header data left after padding
        // TODO: Add checks for the cases were HPACK encoding is invalid
        // TODO: Add checks for whether encoded header size exceeds the max setting

        [Fact]
        public async Task SendingHeadersWithEndOfStreamShouldAllowToReadEndOfStream()
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
                    await s.ReadHeaders();
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
        public async Task SendingHeadersAndNoDataShouldBlockTheReader()
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
        [InlineData(new int[]{0})]
        [InlineData(new int[]{1})]
        [InlineData(new int[]{100})]
        [InlineData(new int[]{99, 784})]
        [InlineData(new int[]{12874, 16384, 16383})]
        public async Task DataFromDataFramesShouldBeReceived(
            int[] dataLength)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var handlerDone = new SemaphoreSlim(0);
            byte[] receivedData = null;

            Func<IStream, bool> listener = (s) =>
            {
                Task.Run(async () =>
                {
                    await s.ReadHeaders();
                    var data = await s.ReadAllToArrayWithTimeout();
                    receivedData = data;
                    handlerDone.Release();
                });
                return true;
            };
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider, listener);

            var hEncoder = new Encoder();
            await inPipe.WriteHeaders(hEncoder, 1, false, DefaultGetHeaders);

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

            var requestDone = await handlerDone.WaitAsync(ReadableStreamTestExtensions.ReadTimeout);
            Assert.True(requestDone, "Expected handler to complete within timeout");
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
        [InlineData(0, 0)]
        [InlineData(1, 0)]
        [InlineData(0, 1024)]
        [InlineData(1, 1024)]
        [InlineData(255, 0)]
        [InlineData(255, 1024)]
        public async Task DataFramesWithPaddingShouldBeCorrectlyReceived(
            byte numPadding, int bytesToSend)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var receiveOk = false;

            var r = await StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);

            var readTask = Task.Run(async () =>
            {
                for (var nrSends = 0; nrSends < 2; nrSends++)
                {
                    var buf1 = new byte[bytesToSend];
                    await r.stream.ReadAllWithTimeout(new ArraySegment<byte>(buf1));
                    var expected = 0;
                    for (var j = 0; j < bytesToSend; j++)
                    {
                        if (buf1[j] != expected) return;
                        expected++;
                        if (expected > 123) expected = 0;
                    }
                }
                // Received everything and everything was like expected
                receiveOk = true;
            });

            for (var nrSends = 0; nrSends < 2; nrSends++)
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
                await inPipe.WriteAsync(new ArraySegment<byte>(buf));
            }

            var doneTask = await Task.WhenAny(readTask, Task.Delay(250));
            Assert.True(readTask == doneTask, "Expected read task to finish");
            Assert.True(receiveOk, "Expected to receive correct data");
        }

        [Theory]
        [InlineData(0, null)]
        [InlineData(1, (byte)1)]
        [InlineData(255, (byte)255)]
        public async Task PaddingViolationsOnOpenStreamsShouldLeadToGoAway(
            int frameLength, byte? padData)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var r = await StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);

            var fh = new FrameHeader
            {
                Type = FrameType.Data,
                StreamId = 1u,
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

        [Theory]
        [InlineData(0, null)]
        [InlineData(1, (byte)1)]
        [InlineData(255, (byte)255)]
        public async Task PaddingViolationsOnUnknownStreamsShouldLeadToGoAway(
            int frameLength, byte? padData)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var r = await StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);

            var fh = new FrameHeader
            {
                Type = FrameType.Data,
                StreamId = 2u,
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
        public async Task SendingTrailersShouldUnblockDataReceptionAndPresentThem()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var res = await StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);

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

            var bytes = await res.stream.ReadAllToArrayWithTimeout();
            Assert.Equal(4, bytes.Length);
            Assert.Equal("ABCD", System.Text.Encoding.ASCII.GetString(bytes));
            Assert.Equal(StreamState.HalfClosedRemote, res.stream.State);
            var rcvdTrailers = await res.stream.ReadTrailers();
            Assert.Equal(trailers, rcvdTrailers);
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
            await r.stream.WriteHeaders(DefaultStatusHeaders, useEndOfStream);

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

        // TODO: Add a test with CONTINUATION frames

        [Theory]
        [InlineData(new int[]{0})]
        [InlineData(new int[]{1})]
        [InlineData(new int[]{100})]
        [InlineData(new int[]{99, 784})]
        [InlineData(new int[]{12874, 16384, 16383})]
        public async Task ResponseDataShouldBeCorrectlySent(
            int[] dataLength)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var r = await StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);
            await r.stream.WriteHeaders(DefaultStatusHeaders, false);
            await outPipe.ReadAndDiscardHeaders(1u, false);

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

        [Fact]
        public async Task ResponseDataShouldRespectMaxFrameSize()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var r = await StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);
            await r.stream.WriteHeaders(DefaultStatusHeaders, false);
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

        [Fact]
        public async Task ResponseTrailersShouldBeCorrectlySent()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var r = await StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);
            await r.stream.WriteHeaders(DefaultStatusHeaders, false);
            await outPipe.ReadAndDiscardHeaders(1u, false);
            await r.stream.WriteAsync(new ArraySegment<byte>(new byte[0]));
            await outPipe.ReadAndDiscardData(1u, false, 0);

            // Send trailers
            await r.stream.WriteTrailers(DefaultTrailingHeaders);

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

        // TODO: Check if not more data than maxFrameSize is sent

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
                await r.stream.WriteTrailers(DefaultTrailingHeaders));

            Assert.Equal("Attempted to write trailers without data", ex.Message);
        }

        [Fact]
        public async Task RespondingTrailersWithoutDataShouldThrowAnException()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var r = await StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);
            await r.stream.WriteHeaders(DefaultStatusHeaders, false);
            var ex = await Assert.ThrowsAsync<Exception>(async () =>
                await r.stream.WriteTrailers(DefaultTrailingHeaders));

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
            await r.stream.WriteHeaders(infoHeaders, false);
            await r.stream.WriteHeaders(DefaultStatusHeaders, false);
            await r.stream.WriteAsync(new ArraySegment<byte>(new byte[0]), true);
            await outPipe.ReadAndDiscardHeaders(1, false);
            await outPipe.ReadAndDiscardHeaders(1, false);
            await outPipe.ReadAndDiscardData(1, true, 0);
        }

        [Fact]
        public async Task CancellingAStreamShouldSendAResetFrame()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            IStream stream = null;

            Func<IStream, bool> listener = (s) =>
            {
                stream = s;
                Task.Run(() => s.Cancel());
                return true;
            };
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider, listener);

            var hEncoder = new Encoder();
            await inPipe.WriteHeaders(hEncoder, 1, false, DefaultGetHeaders);
            await outPipe.AssertResetStreamReception(1, ErrorCode.Cancel);
            Assert.Equal(StreamState.Reset, stream.State);
        }

        [Fact]
        public async Task SendingInvalidHeadersShouldTriggerAStreamReset()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var headers = new HeaderField[]
            {
                new HeaderField { Name = "method", Value = "GET" },
                new HeaderField { Name = ":scheme", Value = "http" },
                new HeaderField { Name = ":path", Value = "/" },
            };

            Func<IStream, bool> listener = (s) => true;
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider, listener);

            var hEncoder = new Encoder();
            await inPipe.WriteHeaders(hEncoder, 1, false, headers);
            await outPipe.AssertResetStreamReception(1, ErrorCode.ProtocolError);
        }

        [Fact]
        public async Task RespondingInvalidHeadersShouldTriggerAnException()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var r = await StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);

            var headers = new HeaderField[]
            {
                new HeaderField { Name = "status", Value = "200" },
            };
            var ex = await Assert.ThrowsAsync<Exception>(async () =>
                await r.stream.WriteHeaders(headers, false));
            Assert.Equal("ErrorInvalidPseudoHeader", ex.Message);
        }

        [Fact]
        public async Task RespondingInvalidTrailersShouldTriggerAnException()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var r = await StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);

            var headers = new HeaderField[]
            {
                new HeaderField { Name = ":asdf", Value = "200" },
            };
            var ex = await Assert.ThrowsAsync<Exception>(async () =>
                await r.stream.WriteHeaders(headers, false));
            Assert.Equal("ErrorInvalidPseudoHeader", ex.Message);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task SendingHeaders2TimesShouldTriggerAStreamReset(
            bool headersAreEndOfStream)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var res = await StreamCreator.CreateConnectionAndStream(
                StreamState.Open, loggerProvider, inPipe, outPipe);

            // Write a second header
            await inPipe.WriteHeaders(
                res.hEncoder, 1, headersAreEndOfStream, DefaultGetHeaders);

            await outPipe.AssertResetStreamReception(1, ErrorCode.ProtocolError);
            Assert.Equal(StreamState.Reset, res.stream.State);
        }

        [Theory]
        [InlineData(StreamState.HalfClosedRemote, true, false, false)]
        [InlineData(StreamState.HalfClosedRemote, false, true, false)]
        [InlineData(StreamState.HalfClosedRemote, false, false, true)]
        // Situations where connection is full closed - not half
        [InlineData(StreamState.Closed, true, false, false)]
        //[InlineData(StreamState.Closed, false, true, false)] // Data frames don't cause rst
        [InlineData(StreamState.Closed, false, false, true)]
        public async Task SendingHeadersOrDataOnAClosedStreamShouldTriggerAStreamReset(
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
                var fh = new FrameHeader
                {
                    Type = FrameType.Data,
                    Length = 0,
                    Flags = 0,
                    StreamId = 1,
                };
                await inPipe.WriteFrameHeader(fh);
            }
            if (sendTrailers)
            {
                await inPipe.WriteHeaders(res.hEncoder, 1, true, DefaultGetHeaders);
            }
            var expectedErr =
                streamState == StreamState.Closed
                ? ErrorCode.RefusedStream
                : ErrorCode.StreamClosed;
            await outPipe.AssertResetStreamReception(1u, expectedErr);
            var expectedState =
                streamState == StreamState.Closed
                ? StreamState.Closed
                : StreamState.Reset;
            Assert.Equal(expectedState, res.stream.State);
        }

        [Theory]
        [InlineData(true, false, false)]
        // [InlineData(false, true, false)] // TODO: SendingDataAloneCurrentlyDoesntReturnError
        [InlineData(false, false, true)]
        [InlineData(false, true, true)]
        public async Task SendingHeadersOrDataOnAResetStreamShouldProduceARefusedStreamError(
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
                var fh = new FrameHeader
                {
                    Type = FrameType.Data,
                    Length = 0,
                    Flags = 0,
                    StreamId = 1,
                };
                await inPipe.WriteFrameHeader(fh);
            }
            if (sendTrailers)
            {
                await inPipe.WriteHeaders(r.hEncoder, 1, true, DefaultGetHeaders);
            }
            await outPipe.AssertResetStreamReception(1, ErrorCode.RefusedStream);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SendingHeadersWithNoAttachedListenerOrRefusingListenerShouldTriggerStreamReset(
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
        public async Task SendingHeadersWithATooSmallStreamIdShouldTriggerAStreamReset(
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
            await outPipe.AssertResetStreamReception(31, ErrorCode.RefusedStream);
        }

        [Theory]
        [InlineData(2)]
        [InlineData(4)]
        public async Task SendingHeadersWithAnEvenStreamIdShouldTriggerAStreamReset(
            uint streamId)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            Func<IStream, bool> listener = (s) => true;
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider, listener);

            var hEncoder = new Encoder();
            await inPipe.WriteHeaders(hEncoder, streamId, false, DefaultGetHeaders);
            await outPipe.AssertResetStreamReception(streamId, ErrorCode.RefusedStream);
        }

        [Fact]
        public async Task SendingHeadersWithStream0ShouldTriggerAGoAway()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            Func<IStream, bool> listener = (s) => true;
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider, listener);

            var hEncoder = new Encoder();
            await inPipe.WriteHeaders(hEncoder, 0, false, DefaultGetHeaders);
            await outPipe.AssertGoAwayReception(ErrorCode.ProtocolError, 0);
            await outPipe.AssertStreamEnd();
        }
    }
}