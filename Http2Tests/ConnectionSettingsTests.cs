using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Text;

using Xunit;

using Http2;

namespace Http2Tests
{
    public class ConnectionSettingsTests
    {
        Connection BuildConnection(
            bool isServer,
            Settings settings,
            IStreamReader inputStream,
            IStreamWriterCloser outputStream)
        {
            return new Connection(new Connection.Options
            {
                InputStream = inputStream,
                OutputStream = outputStream,
                IsServer = isServer,
                Settings = settings,
                StreamListener = (s) => false,
            });
        }

        public async Task ReadAndDiscardPreface(IStreamReader stream)
        {
            var b = new byte[ClientPreface.Length];
            await stream.ReadAll(new ArraySegment<byte>(b));
        }

        public async Task ReadAndDiscardSettings(IStreamReader stream)
        {
            var hdrBuf = new byte[FrameHeader.HeaderSize];
            var header = await FrameHeader.ReceiveAsync(stream, hdrBuf);
            Assert.Equal(FrameType.Settings, header.Type);
            Assert.InRange(header.Length, 0, 256);
            await stream.ReadAll(new ArraySegment<byte>(new byte[header.Length]));
        }

        async Task ValidateSettingReception(IStreamReader stream, Settings expectedSettings)
        {
            var hdrBuf = new byte[FrameHeader.HeaderSize];
            var header = await FrameHeader.ReceiveAsync(stream, hdrBuf);
            Assert.Equal(FrameType.Settings, header.Type);
            Assert.Equal(0, header.Flags);
            Assert.Equal(0u, header.StreamId);
            Assert.Equal(expectedSettings.RequiredSize, header.Length);

            var setBuf = new byte[expectedSettings.RequiredSize];
            await stream.ReadAll(new ArraySegment<byte>(setBuf));
            var settings = new Settings
            {
                EnablePush = false,
                HeaderTableSize = 55,
                InitialWindowSize = 55,
                MaxConcurrentStreams = 55,
                MaxFrameSize = 55,
                MaxHeaderListSize = 55,
            };
            var err = settings.UpdateFromData(new ArraySegment<byte>(setBuf));
            Assert.Null(err);
            Assert.Equal(expectedSettings, settings);
        }

        public static IEnumerable<object[]> ValidSettings
        {
            get
            {
                yield return new object[] { Settings.Default };
                var min = Settings.Min;
                // smaller header table size is not allowed
                // for implementation
                min.HeaderTableSize = 4096;
                yield return new object[] { min };
                // Max settings are not possible, since the dynamic
                // table limit is currently hardcoded
                var max = Settings.Max;
                yield return new object[] { max };
            }
        }

        [Theory]
        [MemberData(nameof(ValidSettings))]
        public async Task ClientsShouldSendSettingsAfterPreface(Settings settings)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = BuildConnection(false, settings, inPipe, outPipe);

            var expected = settings;
            expected.EnablePush = false;
            await ReadAndDiscardPreface(outPipe);
            await ValidateSettingReception(outPipe, expected);
        }

        [Theory]
        [MemberData(nameof(ValidSettings))]
        public async Task ServersShouldSendSettingsUponConnection(Settings settings)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = BuildConnection(true, settings, inPipe, outPipe);

            var expected = settings;
            expected.EnablePush = false;
            await ValidateSettingReception(outPipe, expected);
        }

        [Fact]
        public async Task ServersShouldGoAwayIfFirstFrameIsNotSettings()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = BuildConnection(true, Settings.Default, inPipe, outPipe);
            await ClientPreface.WriteAsync(inPipe);
            var fh = new FrameHeader {
                Type = FrameType.Headers, Length = 0, Flags = 0, StreamId = 2
            };
            var headerBytes = new byte[FrameHeader.HeaderSize];
            fh.EncodeInto(new ArraySegment<byte>(headerBytes));
            await inPipe.WriteAsync(new ArraySegment<byte>(headerBytes));

            var expected = Settings.Default;
            expected.EnablePush = false;
            await ValidateSettingReception(outPipe, expected);
            await ValidateGoAwayReception(outPipe, ErrorCode.ProtocolError, 0u);
            await AssertStreamEnd(outPipe);
        }

        [Fact]
        public async Task ClientShouldGoAwayIfFirstFrameIsNotSettings()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = BuildConnection(false, Settings.Default, inPipe, outPipe);
            var fh = new FrameHeader
            {
                Type = FrameType.Headers,
                Length = 0,
                Flags = 0,
                StreamId = 2
            };
            var headerBytes = new byte[FrameHeader.HeaderSize];
            fh.EncodeInto(new ArraySegment<byte>(headerBytes));
            await inPipe.WriteAsync(new ArraySegment<byte>(headerBytes));

            var expected = Settings.Default;
            expected.EnablePush = false;
            await ReadAndDiscardPreface(outPipe);
            await ValidateSettingReception(outPipe, expected);
            await ValidateGoAwayReception(outPipe, ErrorCode.ProtocolError, 0u);
            await AssertStreamEnd(outPipe);
        }

        // More tests:
        // - GoAway on invalid Settings frame data
        // - GoAway on invalid Settings frame length
        // - GoAway on invalid Settings ack
        // - Check if settings Acks are sent

        public async Task AssertStreamEnd(IStreamReader stream)
        {
            var headerBytes = new byte[FrameHeader.HeaderSize];
            var res = await stream.ReadAsync(new ArraySegment<byte>(headerBytes));
            Assert.True(res.EndOfStream, "Expected end of stream");
            Assert.Equal(0, res.BytesRead);
        }

        public async Task ValidateGoAwayReception(
            IStreamReader stream,
            ErrorCode expectedErrorCode,
            uint lastStreamId)
        {
            var headerBytes = new byte[FrameHeader.HeaderSize];
            await FrameHeader.ReceiveAsync(stream, headerBytes);
            var hdr = FrameHeader.DecodeFrom(new ArraySegment<byte>(headerBytes));
            Assert.Equal(FrameType.GoAway, hdr.Type);
            Assert.Equal(0u, hdr.StreamId);
            Assert.Equal(0, hdr.Flags);
            Assert.InRange(hdr.Length, 8, 256);
            var goAwayBytes = new byte[hdr.Length];
            await stream.ReadAll(new ArraySegment<byte>(goAwayBytes));
            var goAwayData = GoAwayFrameData.DecodeFrom(new ArraySegment<byte>(goAwayBytes));
            Assert.Equal(lastStreamId, goAwayData.LastStreamId);
            Assert.Equal(expectedErrorCode, goAwayData.ErrorCode);
        }
    }
}