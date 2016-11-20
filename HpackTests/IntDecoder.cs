using System;
using System.Collections.Generic;
using System.Text;
using Xunit;
using Hpack;

namespace HpackTests
{
    public class IntDecoderTests
    {
        [Fact]
        public void ShouldDecodeAValueThatIsCompletelyInThePrefix()
        {
            IntDecoder Decoder = new IntDecoder();

            var buf = new Buffer();
            buf.WriteByte(0x2A);
            buf.WriteByte(0x80);
            buf.WriteByte(0xFE);

            var consumed = Decoder.Decode(5, new ArraySegment<byte>(buf.Bytes, 0, 3));
            Assert.True(Decoder.Done);
            Assert.Equal(10u, Decoder.Result);
            Assert.Equal(1, consumed);

            consumed = Decoder.Decode(5, new ArraySegment<byte>(buf.Bytes, 1, 2));
            Assert.True(Decoder.Done);
            Assert.Equal(0u, Decoder.Result);
            Assert.Equal(1, consumed);

            consumed = Decoder.Decode(5, new ArraySegment<byte>(buf.Bytes, 2, 1));
            Assert.True(Decoder.Done);
            Assert.Equal(30u, Decoder.Result);
            Assert.Equal(1, consumed);

            // Test with 1bit prefix (least)
            buf = new Buffer();
            buf.WriteByte(0xFE);
            buf.WriteByte(0x00);
            buf.WriteByte(0x54);

            consumed = Decoder.Decode(1, new ArraySegment<byte>(buf.Bytes, 0, 3));
            Assert.True(Decoder.Done);
            Assert.Equal(0u, Decoder.Result);
            Assert.Equal(1, consumed);

            consumed = Decoder.Decode(1, new ArraySegment<byte>(buf.Bytes, 1, 2));
            Assert.True(Decoder.Done);
            Assert.Equal(0u, Decoder.Result);
            Assert.Equal(1, consumed);

            consumed = Decoder.Decode(1, new ArraySegment<byte>(buf.Bytes, 2, 1));
            Assert.True(Decoder.Done);
            Assert.Equal(0u, Decoder.Result);
            Assert.Equal(1, consumed);

            // Test with 8bit prefix (largest)
            buf = new Buffer();
            buf.WriteByte(0xFE);
            buf.WriteByte(0xEF);
            buf.WriteByte(0x00);
            buf.WriteByte(0x01);
            buf.WriteByte(0x2A);

            consumed = Decoder.Decode(8, new ArraySegment<byte>(buf.Bytes, 0, 5));
            Assert.True(Decoder.Done);
            Assert.Equal(0xFEu, Decoder.Result);
            Assert.Equal(1, consumed);

            consumed = Decoder.Decode(8, new ArraySegment<byte>(buf.Bytes, 1, 4));
            Assert.True(Decoder.Done);
            Assert.Equal(0xEFu, Decoder.Result);
            Assert.Equal(1, consumed);

            consumed = Decoder.Decode(8, new ArraySegment<byte>(buf.Bytes, 2, 3));
            Assert.True(Decoder.Done);
            Assert.Equal(0u, Decoder.Result);
            Assert.Equal(1, consumed);

            consumed = Decoder.Decode(8, new ArraySegment<byte>(buf.Bytes, 3, 2));
            Assert.True(Decoder.Done);
            Assert.Equal(1u, Decoder.Result);
            Assert.Equal(1, consumed);

            consumed = Decoder.Decode(8, new ArraySegment<byte>(buf.Bytes, 4, 1));
            Assert.True(Decoder.Done);
            Assert.Equal(42u, Decoder.Result);
            Assert.Equal(1, consumed);
        }

        [Fact]
        public void ShouldDecodeMultiByteValues()
        {
            IntDecoder Decoder = new IntDecoder();

            var buf = new Buffer();
            buf.WriteByte(0x1F);
            buf.WriteByte(0x9A);
            buf.WriteByte(10);
            var consumed = Decoder.Decode(5, new ArraySegment<byte>(buf.Bytes, 0, 3));
            Assert.True(Decoder.Done);
            Assert.Equal(1337u, Decoder.Result);
            Assert.Equal(3, consumed);
        }

        [Fact]
        public void ShouldDecodeInMultipleSteps()
        {
            IntDecoder Decoder = new IntDecoder();

            var buf1= new Buffer();
            buf1.WriteByte(0x1F);
            buf1.WriteByte(154);
            var buf2 = new Buffer();
            buf2.WriteByte(10);

            var consumed = Decoder.Decode(5, buf1.View);
            Assert.False(Decoder.Done);
            Assert.Equal(2, consumed);

            consumed = Decoder.DecodeCont(buf2.View);
            Assert.True(Decoder.Done);
            Assert.Equal(1337u, Decoder.Result);
            Assert.Equal(1, consumed);

            // And test with only prefix in first byte
            buf1 = new Buffer();
            buf1.WriteByte(0x1F);
            buf2 = new Buffer();
            buf2.WriteByte(154);
            buf2.WriteByte(10);

            consumed = Decoder.Decode(5, buf1.View);
            Assert.False(Decoder.Done);
            Assert.Equal(1, consumed);

            consumed = Decoder.DecodeCont(buf2.View);
            Assert.True(Decoder.Done);
            Assert.Equal(1337u, Decoder.Result);
            Assert.Equal(2, consumed);

            // Test with a single bit prefix
            buf1 = new Buffer();
            buf1.WriteByte(0xFF); // I = 1
            buf2 = new Buffer();
            buf2.WriteByte(0x90); // I = 1 + 0x10 * 2^0 = 0x11
            buf2.WriteByte(0x10); // I = 0x81 + 0x10 * 2^7 = 0x11 + 0x800 = 0x811

            consumed = Decoder.Decode(1, buf1.View);
            Assert.False(Decoder.Done);
            Assert.Equal(1, consumed);

            consumed = Decoder.DecodeCont(buf2.View);
            Assert.True(Decoder.Done);
            Assert.Equal(0x811u, Decoder.Result);
            Assert.Equal(2, consumed);

            // Test with 8bit prefix
            buf1 = new Buffer();
            buf1.WriteByte(0xFF); // I = 0xFF
            buf1.WriteByte(0x90); // I = 0xFF + 0x10 * 2^0 = 0x10F
            buf2 = new Buffer();
            buf2.WriteByte(0x10); // I = 0x10F + 0x10 * 2^7 = 0x10F + 0x800 = 0x90F

            consumed = Decoder.Decode(8, buf1.View);
            Assert.False(Decoder.Done);
            Assert.Equal(2, consumed);

            consumed = Decoder.DecodeCont(buf2.View);
            Assert.True(Decoder.Done);
            Assert.Equal(0x90Fu, Decoder.Result);
            Assert.Equal(1, consumed);
        }

        [Fact]
        public void ShouldThrowAnErrorIfDecodedValueGetsTooLarge()
        {
            IntDecoder Decoder = new IntDecoder();

            // Add 5*7 = 35bits after the prefix, which results in a larger value than 2^53-1
            var buf = new Buffer();
            buf.WriteByte(0x1F);
            buf.WriteByte(0xFF);
            buf.WriteByte(0xFF);
            buf.WriteByte(0xFF);
            buf.WriteByte(0xFF);
            buf.WriteByte(0xEF);
            var ex = Assert.Throws<Exception>(() => Decoder.Decode(5, buf.View));
            Assert.Equal(ex.Message, "invalid integer");
        }
    }
}
