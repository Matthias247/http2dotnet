using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

using Http2;

namespace Http2Tests
{
    public static class ConnectionUtils
    {
        public static async Task<Connection> BuildEstablishedConnection(
            bool isServer,
            IBufferedPipe inputStream,
            IBufferedPipe outputStream,
            ILoggerProvider loggerProvider,
            Func<IStream, bool> streamListener = null)
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

            var conn = new Connection(new Connection.Options
            {
                InputStream = inputStream,
                OutputStream = outputStream,
                IsServer = isServer,
                Settings = Settings.Default,
                Logger = logger,
                StreamListener = streamListener,
                HuffmanStrategy = Http2.Hpack.HuffmanStrategy.Never,
            });
            await PerformHandshakes(conn, inputStream, outputStream);

            return conn;
        }

        public static async Task PerformHandshakes(
            this Connection connection,
            IBufferedPipe inputStream,
            IBufferedPipe outputStream)
        {
            if (connection.IsServer)
            {
                await ClientPreface.WriteAsync(inputStream);
            }
            await inputStream.WriteSettings(Settings.Default);

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