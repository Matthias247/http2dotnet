using System;
using System.IO;
using System.Threading.Tasks;
using System.Text;

using Xunit;

using Http2;

namespace Http2Tests
{
    public class ConnectionPrefaceTests
    {
        Connection BuildConnection(
            bool isServer,
            IStreamReader inputStream,
            IStreamWriterCloser outputStream)
        {
            return new Connection(new Connection.Options
            {
                InputStream = inputStream,
                OutputStream = outputStream,
                IsServer = isServer,
                Settings = Settings.Default,
                StreamListener = (s) => false,
            });
        }

        public async Task ReadAndDiscardSettings(IStreamReader stream)
        {
            var hdrBuf = new byte[FrameHeader.HeaderSize];
            var header = await FrameHeader.ReceiveAsync(stream, hdrBuf);
            Assert.Equal(FrameType.Settings, header.Type);
            Assert.InRange(header.Length, 0, 256);
            await stream.ReadAll(new ArraySegment<byte>(new byte[header.Length]));
        }

        [Fact]
        public async Task ClientShouldSendPrefaceAtStartup()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = BuildConnection(false, inPipe, outPipe);

            var b = new byte[ClientPreface.Length];
            await outPipe.ReadAll(new ArraySegment<byte>(b));
            Assert.Equal(ClientPreface.Bytes, b);
        }

        [Fact]
        public async Task ServerShouldCloseTheConnectionIfPrefaceIsNotReceived()
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = BuildConnection(true, inPipe, outPipe);

            var b = new byte[ClientPreface.Length];
            // Initialize with non-preface data
            for (var i = 0; i < b.Length; i++) b[i] = 10;
            await inPipe.WriteAsync(new ArraySegment<byte>(b));

            // Wait for the response - a settings frame is expected first
            // But as there's a race condition the connection could be closed
            // before or after the settings frame was fully received
            try
            {
                await ReadAndDiscardSettings(outPipe);
                var hdrBuf = new byte[FrameHeader.HeaderSize + 50];
                var header = await FrameHeader.ReceiveAsync(outPipe, hdrBuf);
                Assert.Equal(FrameType.GoAway, header.Type);
            }
            catch (Exception e)
            {
                Assert.IsType<System.IO.EndOfStreamException>(e);
            }
        }
    }
}