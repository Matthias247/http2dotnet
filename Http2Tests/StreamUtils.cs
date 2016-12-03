using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using Http2;
using Xunit;
using System.Threading.Tasks;

namespace Http2Tests
{
    public class StreamUtils
    {
        public class ReadAll
        {
            [Fact]
            public async Task ShouldReadAllRequiredDataFromAConnection()
            {
                // Read 111 bytes in 3 parts
                var source = new BufferReadStream(111, 50);
                for (var i = 0; i < 111; i++) source.Buffer[i] = (byte)i;
                source.Written = 111;
                var dest = new byte[111];
                var destView = new ArraySegment<byte>(dest);
                await source.ReadAll(destView);
                for (var i = 0; i < 111; i++)
                {
                    Assert.Equal(source.Buffer[i], dest[i]);
                }
                Assert.Equal(3, source.NrReads);
            }

            [Fact]
            public async Task ShouldYieldAnErrorIfNotEnoughDataIsAvailable()
            {
                var source = new BufferReadStream(111, 50);
                source.Written = 111;
                // need 112 bytes, but only 111 are available
                var dest = new ArraySegment<byte>(new byte[112]);
                await Assert.ThrowsAsync<EndOfStreamException>(
                    async () => await source.ReadAll(dest));
                Assert.Equal(4, source.NrReads);
            }
        }
    }
}
