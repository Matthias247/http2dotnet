using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Http2.Internal;
using Xunit;

namespace Http2Tests
{
    public class RingBufTests
    {
        [Fact]
        public void ShouldReportCorrectCapacity()
        {
            var r = new RingBuf(111);
            Assert.Equal(111, r.Capacity);
            r.Dispose();
        }

        [Fact]
        public void ShouldAllowReadsAndWrites()
        {
            var r = new RingBuf(111);
            Assert.Equal(111, r.Capacity);
            Assert.Equal(0, r.Available);
            Assert.Equal(111, r.Free);

            // Write some bytes
            var data = new byte[60];
            var dview = new ArraySegment<byte>(data);
            for (var i = 0; i < 60; i++) data[i] = (byte)i;
            r.Write(new ArraySegment<byte>(data));
            Assert.Equal(60, r.Available);
            Assert.Equal(51, r.Free);

            // Fill the buffer
            data = new byte[51];
            for (var i = 0; i < 51; i++) data[i] = (byte)(60+i);
            r.Write(new ArraySegment<byte>(data));
            Assert.Equal(111, r.Available);
            Assert.Equal(0, r.Free);

            // Check that we can't write more than the capacity
            var ex = Assert.Throws<Exception>(() => r.Write(new ArraySegment<byte>(data, 0, 1)));
            Assert.Equal("Not enough free space in the buffer", ex.Message);

            // Read first part of the buffer
            var rbuf = new byte[80];
            r.Read(new ArraySegment<byte>(rbuf));
            for (var i = 0; i < 51; i++)
            {
                Assert.Equal((byte)i, rbuf[i]);
            }
            Assert.Equal(31, r.Available);
            Assert.Equal(80, r.Free);

            // Now write bytes again. This will overflow
            data = new byte[70];
            for (var i = 0; i < 70; i++) data[i] = (byte)(70-i);
            r.Write(new ArraySegment<byte>(data));
            Assert.Equal(101, r.Available);
            Assert.Equal(10, r.Free);

            // And read everything
            rbuf = new byte[101];
            r.Read(new ArraySegment<byte>(rbuf));
            // Check data from the first write
            for (var i = 0; i < 31; i++)
            {
                Assert.Equal((byte)(80+i), rbuf[i]);
            }
            // Check data from the second write
            for (var i = 0; i < 70; i++)
            {
                Assert.Equal((byte)(70-i), rbuf[31+i]);
            }
            Assert.Equal(0, r.Available);
            Assert.Equal(111, r.Free);

            r.Dispose();
        }
    }
}
