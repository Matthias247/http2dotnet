using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

using Http2;
using Http2.Hpack;

namespace Http2Tests
{
    public static class ConnectionUtils
    {
        public static async Task<Connection> BuildEstablishedConnection(
            bool isServer,
            IBufferedPipe inputStream,
            IBufferedPipe outputStream,
            ILoggerProvider loggerProvider,
            Func<IStream, bool> streamListener = null,
            Settings? localSettings = null,
            Settings? remoteSettings = null,
            HuffmanStrategy huffmanStrategy = HuffmanStrategy.Never)
        {
            ILogger logger = null;
            if (loggerProvider != null)
            {
                logger = loggerProvider.CreateLogger("http2Con");
            }
            if (streamListener == null)
            {
                streamListener = (s) => false;
            }

            var lSettings = localSettings ?? Settings.Default;
            var conn = new Connection(new Connection.Options
            {
                InputStream = inputStream,
                OutputStream = outputStream,
                IsServer = isServer,
                Settings = lSettings,
                Logger = logger,
                StreamListener = streamListener,
                HuffmanStrategy = huffmanStrategy,
            });
            await PerformHandshakes(
                conn,
                inputStream, outputStream,
                remoteSettings);

            return conn;
        }

        public static async Task PerformHandshakes(
            this Connection connection,
            IBufferedPipe inputStream,
            IBufferedPipe outputStream,
            Settings? remoteSettings = null)
        {
            if (connection.IsServer)
            {
                await ClientPreface.WriteAsync(inputStream);
            }
            var rsettings = remoteSettings ?? Settings.Default;
            await inputStream.WriteSettings(rsettings);

            if (!connection.IsServer)
            {
                await outputStream.ReadAndDiscardPreface();
            }
            await outputStream.ReadAndDiscardSettings();
            await outputStream.AssertSettingsAck();
            await inputStream.WriteSettingsAck();
        }
    }
}