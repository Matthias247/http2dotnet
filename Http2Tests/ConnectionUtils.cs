using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Xunit;

using Http2;

namespace Http2Tests
{
    public static class ConnectionUtils
    {
        public static async Task<Connection> BuildEstablishedConnection(
            bool isServer,
            IBufferedPipe inputStream,
            IBufferedPipe outputStream)
        {
            var conn = new Connection(new Connection.Options
            {
                InputStream = inputStream,
                OutputStream = outputStream,
                IsServer = isServer,
                Settings = Settings.Default,
                StreamListener = (s) => false,
            });
            if (isServer)
            {
                await ClientPreface.WriteAsync(inputStream);
            }
            await inputStream.WriteDefaultSettings();

            if (!isServer)
            {
                await outputStream.ReadAndDiscardPreface();
            }
            await outputStream.ReadAndDiscardSettings();
            await outputStream.AssertSettingsAck();
            await inputStream.WriteSettingsAck();
            return conn;
        }
    }
}