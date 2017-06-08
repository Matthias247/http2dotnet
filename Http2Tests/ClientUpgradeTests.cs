using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
    public class ClientUpgradeTests
    {
        private ILoggerProvider loggerProvider;

        public ClientUpgradeTests(ITestOutputHelper outHelper)
        {
            loggerProvider = new XUnitOutputLoggerProvider(outHelper);
        }

        public static IEnumerable<object[]> SettingsEncodings
        {
            get
            {
                var settings = Settings.Default;
                settings.EnablePush = false;

                yield return new object[]{
                    settings, "AAIAAAAAAAEAABAAAAQAAP__AAP_____AAUAAEAAAAb_____"
                };

                settings.HeaderTableSize = 0x09080706;
                settings.EnablePush = false; // Will be reset by encoder anyway
                settings.MaxConcurrentStreams = 0x71727374;
                settings.InitialWindowSize = 0x10203040;
                settings.MaxFrameSize = 0x00332211;
                settings.MaxHeaderListSize = 0x01020304;
                yield return new object[]{
                    settings, "AAIAAAAAAAEJCAcGAAQQIDBAAANxcnN0AAUAMyIRAAYBAgME"
                };
            }
        }

        [Theory]
        [MemberData(nameof(SettingsEncodings))]
        public void UpgradeRequestShouldBase64EncodeSettingsCorrectly(
            Settings settings, string expectedEncoding)
        {
            var upgrade =
                new ClientUpgradeRequestBuilder()
                .SetHttp2Settings(settings)
                .Build();

            Assert.Equal(expectedEncoding, upgrade.Base64EncodedSettings);
        }

        [Fact]
        public async Task ClientUpgradeRequestShouldYieldStream1()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var upgrade = new ClientUpgradeRequestBuilder().Build();
            var config = new ConnectionConfigurationBuilder(false)
                .Build();

            var conn = new Connection(
                config, inPipe, outPipe,
                new Connection.Options
                {
                    Logger = loggerProvider.CreateLogger("http2Con"),
                    ClientUpgradeRequest = upgrade,
                });

            await conn.PerformHandshakes(inPipe, outPipe);
            var stream = await upgrade.UpgradeRequestStream;
            Assert.Equal(1u, stream.Id);
            Assert.Equal(1, conn.ActiveStreamCount);
            Assert.Equal(StreamState.HalfClosedLocal, stream.State);

            var readHeadersTask = stream.ReadHeadersAsync();
            Assert.False(readHeadersTask.IsCompleted);

            var hEncoder = new Http2.Hpack.Encoder();
            await inPipe.WriteHeaders(hEncoder, 1u, false, DefaultStatusHeaders);
            Assert.True(
                await Task.WhenAny(
                    readHeadersTask,
                    Task.Delay(ReadableStreamTestExtensions.ReadTimeout))
                == readHeadersTask,
                "Expected to read headers, got timeout");
            var headers = await readHeadersTask;
            Assert.True(headers.SequenceEqual(DefaultStatusHeaders));
            Assert.Equal(StreamState.HalfClosedLocal, stream.State);

            await inPipe.WriteData(1u, 100, 5, true);
            var data = await stream.ReadAllToArrayWithTimeout();
            Assert.True(data.Length == 100);
            Assert.Equal(StreamState.Closed, stream.State);
            Assert.Equal(0, conn.ActiveStreamCount);
        }

        [Fact]
        public async Task TheNextOutgoingStreamAfterUpgradeShouldUseId3()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var upgrade = new ClientUpgradeRequestBuilder().Build();
            var config = new ConnectionConfigurationBuilder(false)
                .Build();

            var conn = new Connection(
                config, inPipe, outPipe,
                new Connection.Options
                {
                    Logger = loggerProvider.CreateLogger("http2Con"),
                    ClientUpgradeRequest = upgrade,
                });

            await conn.PerformHandshakes(inPipe, outPipe);
            var stream = await upgrade.UpgradeRequestStream;
            Assert.Equal(1u, stream.Id);

            var readHeadersTask = stream.ReadHeadersAsync();
            Assert.False(readHeadersTask.IsCompleted);

            var nextStream = await conn.CreateStreamAsync(DefaultGetHeaders);
            await outPipe.ReadAndDiscardHeaders(3u, false);
            Assert.Equal(3u, nextStream.Id);
            Assert.True(stream != nextStream);
            Assert.Equal(StreamState.HalfClosedLocal, stream.State);
            Assert.Equal(StreamState.Open, nextStream.State);

            var hEncoder = new Http2.Hpack.Encoder();
            await inPipe.WriteHeaders(hEncoder, 3u, true, DefaultStatusHeaders);
            var nextStreamHeaders = await nextStream.ReadHeadersAsync();
            Assert.True(nextStreamHeaders.SequenceEqual(DefaultStatusHeaders));
            Assert.False(readHeadersTask.IsCompleted);
            Assert.Equal(StreamState.HalfClosedRemote, nextStream.State);
            Assert.Equal(StreamState.HalfClosedLocal, stream.State);
            Assert.Equal(2, conn.ActiveStreamCount);

            await nextStream.WriteAsync(new ArraySegment<byte>(new byte[0]), true);
            await outPipe.ReadAndDiscardData(3u, true, 0);
            Assert.Equal(StreamState.Closed, nextStream.State);
            Assert.Equal(1, conn.ActiveStreamCount);

            var headers2 = DefaultStatusHeaders.Append(
                new HeaderField(){ Name="hh", Value = "vv" });
            await inPipe.WriteHeaders(hEncoder, 1u, false, headers2);
            var streamHeaders = await readHeadersTask;
            Assert.True(streamHeaders.SequenceEqual(headers2));
            await inPipe.WriteData(1u, 10, 0, true);
            var data = await stream.ReadAllToArrayWithTimeout();
            Assert.True(data.Length == 10);
            Assert.Equal(StreamState.Closed, stream.State);
            Assert.Equal(0, conn.ActiveStreamCount);
        }
    }
}