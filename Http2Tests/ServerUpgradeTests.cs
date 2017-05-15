using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

using Http2;
using Http2.Hpack;

namespace Http2Tests
{
    public class ServerUpgradeTests
    {
        private ILoggerProvider loggerProvider;

        public ServerUpgradeTests(ITestOutputHelper outHelper)
        {
            loggerProvider = new XUnitOutputLoggerProvider(outHelper);
        }

        [Theory]
        [InlineData("!§")]
        [InlineData("ab/")]
        public void UpgradesWithInvalidEncodedHttp2SettingsStringShouldBeInvalid(
            string encodedSettings)
        {
            var builder = new ServerUpgradeRequestBuilder();
            builder.SetHeaders(ServerStreamTests.DefaultGetHeaders.ToList());
            builder.SetHttp2Settings(encodedSettings);
            var upgrade = builder.Build();
            Assert.False(upgrade.IsValid);
        }

        [Theory]
        [InlineData(new byte[]{0x01})]
        [InlineData(new byte[]{0x01,0x02,0x03})]
        [InlineData(new byte[]{0x01,0x02,0x03,0x04,0x05})]
        [InlineData(new byte[]{0x01,0x02,0x03,0x04,0x05,0x06,0x07})]
        public void UpgradesWithInvalidHttp2SettingsPayloadShouldBeInvalid(
            byte[] payload)
        {
            var builder = new ServerUpgradeRequestBuilder();
            builder.SetHeaders(ServerStreamTests.DefaultGetHeaders.ToList());
            var base64 = Convert.ToBase64String(payload);
            base64 = base64.Replace('/', '_');
            base64 = base64.Replace('+', '-');
            builder.SetHttp2Settings(base64);
            var upgrade = builder.Build();
            Assert.False(upgrade.IsValid);
        }

        [Theory]
        [InlineData(new byte[]{})]
        [InlineData(new byte[]{0x01,0x02,0x03,0x04,0x05,0x06})]
        public void ValidHttp2SettingsShouldBeAccepted(
            byte[] payload)
        {
            var builder = new ServerUpgradeRequestBuilder();
            builder.SetHeaders(ServerStreamTests.DefaultGetHeaders.ToList());
            var base64 = Convert.ToBase64String(payload);
            base64 = base64.Replace('/', '_');
            base64 = base64.Replace('+', '-');
            builder.SetHttp2Settings(base64);
            var upgrade = builder.Build();
            Assert.True(upgrade.IsValid);
        }

        public static IEnumerable<object[]> InvalidHeaders
        {
            get
            {
                yield return new object[]{
                    new HeaderField[]{},
                };

                yield return new object[]{
                    new HeaderField[]
                    {
                        new HeaderField{Name=":method", Value="GET"},
                        new HeaderField{Name=":path", Value="/"},
                    },
                };

                yield return new object[]{
                    new HeaderField[]
                    {
                        new HeaderField{Name=":method", Value="GET"},
                        new HeaderField{Name=":path", Value="/"},
                        new HeaderField{Name=":Scheme", Value="http"}
                    },
                };

                yield return new object[]{
                    new HeaderField[]
                    {
                        new HeaderField{Name=":method", Value="GET"},
                        new HeaderField{Name=":method", Value="GET"},
                        new HeaderField{Name=":path", Value="/"},
                        new HeaderField{Name=":scheme", Value="http"}
                    },
                };

                yield return new object[]{
                    new HeaderField[]
                    {
                        new HeaderField{Name=":method", Value="GET"},
                        new HeaderField{Name=":path", Value="/"},
                        new HeaderField{Name="dummy", Value="xyz"},
                        new HeaderField{Name=":scheme", Value="http"}
                    },
                };
            }
        }

        [Theory]
        [MemberData(nameof(InvalidHeaders))]
        public void UpgradesWithInvalidHeadersShouldBeInvalid(
            HeaderField[] headers)
        {
            var builder = new ServerUpgradeRequestBuilder();
            builder.SetHttp2Settings("");
            builder.SetHeaders(headers.ToList());
            var upgrade = builder.Build();
            Assert.False(upgrade.IsValid);
        }

        [Theory]
        [InlineData(null, null, true)]
        [InlineData(null, 0, true)]
        [InlineData(0, 0, true)]
        [InlineData(0, null, true)]
        [InlineData(3, 3, true)]
        [InlineData(null, 1, false)]
        [InlineData(0, 100, false)]
        [InlineData(1, null, false)]
        [InlineData(100, 0, false)]
        [InlineData(1, 101, false)]
        public void UpgradesWithPayloadMustHaveMatchingContentLength(
            int? contentLength, int? payloadLength, bool isOk)
        {
            var builder = new ServerUpgradeRequestBuilder();
            builder.SetHttp2Settings("");
            var headers = ServerStreamTests.DefaultGetHeaders.ToList();
            if (contentLength != null)
            {
                headers.Add(new HeaderField(){
                    Name="content-length",
                    Value=contentLength.ToString()});
            }
            builder.SetHeaders(headers);

            if (payloadLength != null)
            {
                const int padding = 3;
                var pl = new byte[padding + payloadLength.Value];
                builder.SetPayload(new ArraySegment<byte>(pl, padding, payloadLength.Value));
            }

            var upgrade = builder.Build();
            Assert.Equal(isOk, upgrade.IsValid);
        }

        [Fact]
        public void CreatingAConnectionWithInvalidUpgradeShouldThrow()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);

            var config = new ConnectionConfigurationBuilder(true)
                .UseStreamListener(s => false)
                .Build();

            var builder = new ServerUpgradeRequestBuilder();
            builder.SetHeaders(ServerStreamTests.DefaultGetHeaders.ToList());
            builder.SetHttp2Settings("!");
            var upgrade = builder.Build();
            Assert.False(upgrade.IsValid);

            var ex = Assert.Throws<ArgumentException>(() =>
            {
                var conn = new Connection(
                    config, inPipe, outPipe,
                    new Connection.Options
                    {
                        Logger = loggerProvider.CreateLogger(""),
                        ServerUpgradeRequest = upgrade,
                    });
            });
            Assert.Equal(
                "The ServerUpgradeRequest is invalid.\n" +
                "Invalid upgrade requests must be denied by the HTTP/1 handler",
                ex.Message);
        }

        // TODO: Tests with an acutal successful upgrade
    }
}