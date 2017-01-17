using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Xunit;

using Http2;

namespace Http2Tests
{
    public class ConnectionCompletionTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ConnectionShouldSignalDoneWhenInputIsClosed(bool isServer)
        {
            var inPipe = new BufferedPipe(1024);
            var outPipe = new BufferedPipe(1024);
            var http2Con = await ConnectionUtils.BuildEstablishedConnection(
                isServer, inPipe, outPipe);

            await inPipe.CloseAsync();
            // Expect the connection to close within timeout
            var closed = http2Con.Done;
            Assert.True(
                closed == await Task.WhenAny(closed, Task.Delay(1000)),
                "Expected connection to close");
        }
    }
}