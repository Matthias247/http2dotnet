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
            IWriteAndCloseableByteStream inputStream,
            IWriteAndCloseableByteStream outputStream)
        {
            var conn = new Connection(new Connection.Options
            {
                InputStream = inputStream as IReadableByteStream,
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
            var oStream = outputStream as IReadableByteStream;

            if (!isServer)
            {
                await oStream.ReadAndDiscardPreface();
            }
            await oStream.ReadAndDiscardSettings();
            await oStream.AssertSettingsAck();
            await inputStream.WriteSettingsAck();
            return conn;
        }
    }
}