using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

using Http2;
using Http2.Hpack;

namespace Http2Tests
{
    public class ConnectionSettingsTests
    {
        Connection BuildConnection(
            bool isServer,
            Settings settings,
            IReadableByteStream inputStream,
            IWriteAndCloseableByteStream outputStream)
        {
            return new Connection(new Connection.Options
            {
                InputStream = inputStream,
                OutputStream = outputStream,
                IsServer = isServer,
                Settings = settings,
                StreamListener = (s) => false,
                Logger = loggerProvider.CreateLogger("http2Con"),
            });
        }

        private ILoggerProvider loggerProvider;

        public ConnectionSettingsTests(ITestOutputHelper outHelper)
        {
            loggerProvider = new XUnitOutputLoggerProvider(outHelper);
        }

        async Task ValidateSettingReception(
            IReadableByteStream stream, Settings expectedSettings)
        {
            var header = await stream.ReadFrameHeaderWithTimeout();
            Assert.Equal(FrameType.Settings, header.Type);
            Assert.Equal(0, header.Flags);
            Assert.Equal(0u, header.StreamId);
            Assert.Equal(expectedSettings.RequiredSize, header.Length);

            var setBuf = new byte[expectedSettings.RequiredSize];
            await stream.ReadAllWithTimeout(new ArraySegment<byte>(setBuf));
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
            await outPipe.ReadAndDiscardPreface();
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
        public async Task ConnectionShouldAcknowledgeValidSettings()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = BuildConnection(true, Settings.Default, inPipe, outPipe);

            await ClientPreface.WriteAsync(inPipe);
            await inPipe.WriteSettings(Settings.Default);

            await outPipe.ReadAndDiscardSettings();
            await outPipe.AssertSettingsAck();
        }

        [Fact]
        public async Task ConnectionShouldIgnoreAndAcknowledgeUnknownSettings()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = BuildConnection(true, Settings.Default, inPipe, outPipe);

            await ClientPreface.WriteAsync(inPipe);
            await outPipe.ReadAndDiscardSettings();

            var settings = Settings.Default;
            // Create a buffer for normal settings plus 3 unknown ones
            var settingsBuffer = new byte[settings.RequiredSize + 18];
            settings.EncodeInto(new ArraySegment<byte>(
                settingsBuffer, 0, settings.RequiredSize));
            // Use some unknown settings IDs
            settingsBuffer[settings.RequiredSize] = 0;
            settingsBuffer[settings.RequiredSize+1] = 10;
            settingsBuffer[settings.RequiredSize+6] = 10;
            settingsBuffer[settings.RequiredSize+7] = 20;
            settingsBuffer[settings.RequiredSize+12] = 0xFF;
            settingsBuffer[settings.RequiredSize+13] = 0xFF;
            var settingsHeader = new FrameHeader
            {
                Type = FrameType.Settings,
                StreamId = 0,
                Flags = 0,
                Length = settingsBuffer.Length,
            };
            await inPipe.WriteFrameHeader(settingsHeader);
            await inPipe.WriteAsync(new ArraySegment<byte>(settingsBuffer));
            // Check if the connection ACKs these settings
            await outPipe.AssertSettingsAck();
        }

        [Fact]
        public async Task ServersShouldGoAwayIfFirstFrameIsNotSettings()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = BuildConnection(true, Settings.Default, inPipe, outPipe);
            await ClientPreface.WriteAsync(inPipe);
            var fh = new FrameHeader
            {
                Type = FrameType.Headers,
                Length = 0,
                Flags = 0,
                StreamId = 2,
            };
            await inPipe.WriteFrameHeader(fh);

            await outPipe.ReadAndDiscardSettings();
            await outPipe.AssertGoAwayReception(ErrorCode.ProtocolError, 0u);
            await outPipe.AssertStreamEnd();
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
            await inPipe.WriteFrameHeader(fh);

            var expected = Settings.Default;
            expected.EnablePush = false;
            await outPipe.ReadAndDiscardPreface();
            await outPipe.ReadAndDiscardSettings();
            await outPipe.AssertGoAwayReception(ErrorCode.ProtocolError, 0u);
            await outPipe.AssertStreamEnd();
        }

        [Fact]
        public async Task ConnectionShouldGoAwayOnSettingsStreamIdNonZero()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = BuildConnection(true, Settings.Default, inPipe, outPipe);
            await ClientPreface.WriteAsync(inPipe);

            var fh = new FrameHeader
            {
                Type = FrameType.Settings,
                Length = Settings.Default.RequiredSize,
                Flags = 0,
                StreamId = 1,
            };
            await inPipe.WriteFrameHeader(fh);

            await outPipe.ReadAndDiscardSettings();
            await outPipe.AssertGoAwayReception(ErrorCode.ProtocolError, 0u);
            await outPipe.AssertStreamEnd();
        }

        [Fact]
        public async Task ConnectionShouldGoAwayOnInvalidSettingsFrameContent()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = BuildConnection(true, Settings.Default, inPipe, outPipe);
            await ClientPreface.WriteAsync(inPipe);

            var settings = Settings.Default;
            settings.MaxFrameSize = 1; // Invalid
            var settingsData = new byte[settings.RequiredSize];
            var fh = new FrameHeader
            {
                Type = FrameType.Settings,
                Length = settingsData.Length,
                Flags = 0,
                StreamId = 0,
            };
            await inPipe.WriteFrameHeader(fh);
            settings.EncodeInto(new ArraySegment<byte>(settingsData));
            await inPipe.WriteAsync(new ArraySegment<byte>(settingsData));

            await outPipe.ReadAndDiscardSettings();
            await outPipe.AssertGoAwayReception(ErrorCode.ProtocolError, 0u);
            await outPipe.AssertStreamEnd();
        }

        [Fact]
        public async Task ConnectionShouldGoAwayOnInvalidWindowSizeSettingWithFlowControlError()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = BuildConnection(true, Settings.Default, inPipe, outPipe);
            await ClientPreface.WriteAsync(inPipe);

            var settings = Settings.Default;
            settings.InitialWindowSize = (uint)int.MaxValue + 1u; // Invalid
            var settingsData = new byte[settings.RequiredSize];
            var fh = new FrameHeader
            {
                Type = FrameType.Settings,
                Length = settingsData.Length,
                Flags = 0,
                StreamId = 0,
            };
            settings.EncodeInto(new ArraySegment<byte>(settingsData));
            await inPipe.WriteFrameHeader(fh);
            await inPipe.WriteAsync(new ArraySegment<byte>(settingsData));

            await outPipe.ReadAndDiscardSettings();
            await outPipe.AssertGoAwayReception(ErrorCode.FlowControlError, 0u);
            await outPipe.AssertStreamEnd();
        }

        [Fact]
        public async Task ConnectionShouldGoAwayOnInvalidSettingsFrameLength()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = BuildConnection(true, Settings.Default, inPipe, outPipe);
            await ClientPreface.WriteAsync(inPipe);

            var settings = Settings.Default;
            var settingsData = new byte[settings.RequiredSize+1]; // 1 byte extra
            var fh = new FrameHeader
            {
                Type = FrameType.Settings,
                Length = settingsData.Length,
                Flags = 0,
                StreamId = 0,
            };
            settings.EncodeInto(new ArraySegment<byte>(
                settingsData, 0, settingsData.Length - 1));
            await inPipe.WriteFrameHeader(fh);
            await inPipe.WriteAsync(new ArraySegment<byte>(settingsData));

            await outPipe.ReadAndDiscardSettings();
            await outPipe.AssertGoAwayReception(ErrorCode.ProtocolError, 0u);
            await outPipe.AssertStreamEnd();
        }

        [Fact]
        public async Task ConnectionShouldGoAwayOnInvalidSettingsMaxLengthExceeded()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = BuildConnection(true, Settings.Default, inPipe, outPipe);
            await ClientPreface.WriteAsync(inPipe);

            var fh = new FrameHeader
            {
                Type = FrameType.Settings,
                Length = (int)Settings.Default.MaxFrameSize + 1,
                Flags = 0,
                StreamId = 0,
            };
            await inPipe.WriteFrameHeader(fh);

            await outPipe.ReadAndDiscardSettings();
            await outPipe.AssertGoAwayReception(ErrorCode.FrameSizeError, 0u);
            await outPipe.AssertStreamEnd();
        }

        [Fact]
        public async Task ConnectionShouldAcceptSettingsAckAndNotGoAway()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = BuildConnection(true, Settings.Default, inPipe, outPipe);
            await ClientPreface.WriteAsync(inPipe);
            await inPipe.WriteSettings(Settings.Default);
            // Wait for remote settings
            await outPipe.ReadAndDiscardSettings();
            // Wait for ack to our settings
            await outPipe.AssertSettingsAck();
            // Acknowledge remote settings
            await inPipe.WriteSettingsAck();
            // And expect that no GoAway follows - which means a timeout happens on read
            await outPipe.AssertReadTimeout();
        }

        [Fact]
        public async Task ConnectionShouldGoAwayOnUnsolicitedSettingsAck()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = BuildConnection(true, Settings.Default, inPipe, outPipe);
            await ClientPreface.WriteAsync(inPipe);
            await inPipe.WriteSettings(Settings.Default);
            // Wait for remote settings
            await outPipe.ReadAndDiscardSettings();
            // Wait for ack to our settings
            await outPipe.AssertSettingsAck();
            // Acknowledge remote settings 2 times
            await inPipe.WriteSettingsAck();
            await inPipe.WriteSettingsAck();
            // Wait for GoAway due to multiple ACKs
            await outPipe.AssertGoAwayReception(ErrorCode.ProtocolError, 0);
            await outPipe.AssertStreamEnd();
        }

        [Fact]
        public async Task ConnectionShouldGoAwayOnSettingsAckWithInvalidStreamId()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = BuildConnection(true, Settings.Default, inPipe, outPipe);
            await ClientPreface.WriteAsync(inPipe);
            await inPipe.WriteSettings(Settings.Default);
            // Wait for remote settings
            await outPipe.ReadAndDiscardSettings();
            // Wait for ack to our settings
            await outPipe.AssertSettingsAck();
            var fh = new FrameHeader
            {
                Type = FrameType.Settings,
                Flags = (byte)SettingsFrameFlags.Ack,
                StreamId = 1,
                Length = 0,
            };
            await inPipe.WriteFrameHeader(fh);
            // Wait for GoAway due to wrong stream ID
            await outPipe.AssertGoAwayReception(ErrorCode.ProtocolError, 0);
            await outPipe.AssertStreamEnd();
        }

        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(6)]
        public async Task ConnectionShouldGoAwayOnSettingsAckWithNonZeroLength(
            int frameLength)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = BuildConnection(true, Settings.Default, inPipe, outPipe);
            await ClientPreface.WriteAsync(inPipe);
            await inPipe.WriteSettings(Settings.Default);
            // Wait for remote settings
            await outPipe.ReadAndDiscardSettings();
            // Wait for ack to our settings
            await outPipe.AssertSettingsAck();
            var fh = new FrameHeader
            {
                Type = FrameType.Settings,
                Flags = (byte)SettingsFrameFlags.Ack,
                StreamId = 0,
                Length = frameLength,
            };
            await inPipe.WriteFrameHeader(fh);
            // Wait for GoAway due to wrong stream ID
            await outPipe.AssertGoAwayReception(ErrorCode.ProtocolError, 0);
            await outPipe.AssertStreamEnd();
        }

        [Fact]
        public async Task IfSettingsDecreaseHeaderTableNextOutgoingHeadersShouldContainAnUpdate()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            Func<IStream, bool> listener = (s) =>
            {
                Task.Run(() =>
                {
                    var res = new HeaderField[]
                    {
                        new HeaderField { Name = ":status", Value = "200" },
                    };
                    s.WriteHeadersAsync(res, false);
                });
                return true;
            };

            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider, listener);

            // Change remote settings 2 times
            var settings = Settings.Default;
            settings.HeaderTableSize = 8;
            await inPipe.WriteSettings(settings);
            await outPipe.AssertSettingsAck();
            settings.HeaderTableSize = 30;
            await inPipe.WriteSettings(settings);
            await outPipe.AssertSettingsAck();

            // Establish a stream
            // When we send a response through it we should observe the size udpate
            var hEncoder = new Encoder();
            await inPipe.WriteHeaders(hEncoder, 1, false, ServerStreamTests.DefaultGetHeaders);

            // Wait for the incoming status headers with header update
            var fh = await outPipe.ReadFrameHeaderWithTimeout();
            Assert.Equal(FrameType.Headers, fh.Type);
            Assert.Equal((byte)(HeadersFrameFlags.EndOfHeaders), fh.Flags);
            Assert.Equal(1u, fh.StreamId);

            Assert.Equal(3, fh.Length);
            var data = new byte[fh.Length];
            await outPipe.ReadAllWithTimeout(new ArraySegment<byte>(data));
            Assert.Equal(0x28, data[0]); // Size Update to 8
            Assert.Equal(0x3e, data[1]); // Size Update to 30
            Assert.Equal(0x88, data[2]); // :status 200
        }

        [Fact]
        public async Task TheRemoteHeaderTableSizeShouldOnlyBeUsedUpToConfiguredLimit()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var handlerDone = new SemaphoreSlim(0);

            Func<IStream, bool> listener = (s) =>
            {
                Task.Run(() =>
                {
                    var res = new HeaderField[]
                    {
                        new HeaderField { Name = ":status", Value = "200" },
                    };
                    s.WriteHeadersAsync(res, false);
                });
                handlerDone.Release();
                return true;
            };

            // Lower the initial window size so that stream window updates are
            // sent earlier than connection window updates
            var localSettings = Settings.Default;
            localSettings.HeaderTableSize = 20000;
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                true, inPipe, outPipe, loggerProvider, listener,
                localSettings: localSettings);

            // Change remote settings, which grants us a giant header table
            var settings = Settings.Default;
            settings.HeaderTableSize = 50*1024*1024;
            await inPipe.WriteSettings(settings);
            await outPipe.AssertSettingsAck();

            // Establish a stream
            // When we send a response through it we should observe the size udpate
            var hEncoder = new Encoder();
            await inPipe.WriteHeaders(hEncoder, 1, false, ServerStreamTests.DefaultGetHeaders);

            var ok = await handlerDone.WaitAsync(
                ReadableStreamTestExtensions.ReadTimeout);
            if (!ok) throw new Exception("Stream was not created");

            // Wait for the incoming status headers with header update
            var fh = await outPipe.ReadFrameHeaderWithTimeout();
            Assert.Equal(FrameType.Headers, fh.Type);
            Assert.Equal((byte)(HeadersFrameFlags.EndOfHeaders), fh.Flags);
            Assert.Equal(1u, fh.StreamId);

            // Observe headers with status update
            // We should update the header table to 20000
            Assert.Equal(5, fh.Length);
            var data = new byte[fh.Length];
            await outPipe.ReadAllWithTimeout(new ArraySegment<byte>(data));
            Assert.Equal(0x3f, data[0]); // Size Update to 20000 => 20000 - 31 = 19969
            Assert.Equal(0x81, data[1]); // 19969 % 128 + 128 = 0x81, 19969 / 128 = 156
            Assert.Equal(0x9c, data[2]); // 156 % 128 + 128 = 0x9c, 156 / 128 = 1
            Assert.Equal(0x01, data[3]); // 1
            Assert.Equal(0x88, data[4]); // :status 200
        }
    }
}
