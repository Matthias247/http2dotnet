using System;
using System.Buffers;
using Xunit;
using Http2.Hpack;

namespace HpackTests
{
    public class StringDecoderTests
    {
        private ArrayPool<byte> bufPool = ArrayPool<byte>.Shared;

        [Fact]
        public void ShouldDecodeAnASCIIStringFromACompleteBuffer()
        {
            StringDecoder Decoder = new StringDecoder(1024, bufPool);

            // 0 Characters
            var buf = new Buffer();
            buf.WriteByte(0x00);
            var consumed = Decoder.Decode(buf.View);
            Assert.True(Decoder.Done);
            Assert.Equal("", Decoder.Result);
            Assert.Equal(0, Decoder.StringLength);
            Assert.Equal(1, consumed);

            buf = new Buffer();
            buf.WriteByte(0x04); // 4 Characters, non huffman
            buf.WriteByte('a');
            buf.WriteByte('s');
            buf.WriteByte('d');
            buf.WriteByte('f');

            consumed = Decoder.Decode(buf.View);
            Assert.True(Decoder.Done);
            Assert.Equal("asdf", Decoder.Result);
            Assert.Equal(4, Decoder.StringLength);
            Assert.Equal(5, consumed);

            // Multi-byte prefix
            buf = new Buffer();
            buf.WriteByte(0x7F); // Prefix filled, non huffman, I = 127
            buf.WriteByte(0xFF); // I = 0x7F + 0x7F * 2^0 = 0xFE
            buf.WriteByte(0x03); // I = 0xFE + 0x03 * 2^7 = 0xFE + 0x180 = 0x27E = 638
            var expectedLength = 638;
            var expectedString = "";
            for (var i = 0; i < expectedLength; i++)
            {
                buf.WriteByte(' ');
                expectedString += ' ';
            }
            consumed = Decoder.Decode(buf.View);
            Assert.True(Decoder.Done);
            Assert.Equal(expectedString, Decoder.Result);
            Assert.Equal(expectedLength, Decoder.StringLength);
            Assert.Equal(3 + expectedLength, consumed);
        }

        [Fact]
        public void ShouldDecodeAnASCIIStringIfPayloadIsInMultipleBuffers()
        {
            StringDecoder Decoder = new StringDecoder(1024, bufPool);

            // Only put the prefix in the first byte
            var buf = new Buffer();
            buf.WriteByte(0x04); // 4 Characters, non huffman
            var consumed = Decoder.Decode(buf.View);
            Assert.False(Decoder.Done);
            Assert.Equal(1, consumed);

            // Next chunk with part of data
            buf = new Buffer();
            buf.WriteByte('a');
            buf.WriteByte('s');
            consumed = Decoder.DecodeCont(buf.View);
            Assert.False(Decoder.Done);
            Assert.Equal(2, consumed);

            // Give the thing a depleted buffer
            consumed = Decoder.DecodeCont(new ArraySegment<byte>(buf.Bytes, 2, 0));
            Assert.False(Decoder.Done);
            Assert.Equal(0, consumed);

            // Final chunk
            buf = new Buffer();
            buf.WriteByte('d');
            buf.WriteByte('f');
            consumed = Decoder.DecodeCont(buf.View);
            Assert.True(Decoder.Done);
            Assert.Equal("asdf", Decoder.Result);
            Assert.Equal(4, Decoder.StringLength);
            Assert.Equal(2, consumed);
        }

        [Fact]
        public void ShouldDecodeAHuffmanEncodedStringIfLengthAndPayloadAreInMultipleBuffers()
        {
            StringDecoder Decoder = new StringDecoder(1024, bufPool);

            // Only put the prefix in the first byte
            var buf = new Buffer();
            buf.WriteByte(0xFF); // Prefix filled, non huffman, I = 127
            var consumed = Decoder.Decode(buf.View);
            Assert.False(Decoder.Done);
            Assert.Equal(1, consumed);

            // Remaining part of the length plus first content byte
            buf = new Buffer();
            buf.WriteByte(0x02); // I = 0x7F + 0x02 * 2^0 = 129 byte payload
            buf.WriteByte(0xf9); // first byte of the payload
            var expectedResult = "*";
            consumed = Decoder.DecodeCont(buf.View);
            Assert.False(Decoder.Done);
            Assert.Equal(2, consumed);

            // Half of other content bytes
            buf = new Buffer();
            for (var i = 0; i < 64; i = i+2)
            {
                expectedResult += ")-";
                buf.WriteByte(0xfe);
                buf.WriteByte(0xd6);
            }
            consumed = Decoder.DecodeCont(buf.View);
            Assert.False(Decoder.Done);
            Assert.Equal(64, consumed);

            // Last part of content bytes
            buf = new Buffer();
            for (var i = 0; i < 64; i = i+2)
            {
                expectedResult += "0+";
                buf.WriteByte(0x07);
                buf.WriteByte(0xfb);
            }
            consumed = Decoder.DecodeCont(buf.View);
            Assert.True(Decoder.Done);
            Assert.Equal(expectedResult, Decoder.Result);
            Assert.Equal(129, Decoder.StringLength);
            Assert.Equal(64, consumed);
        }

        [Fact]
        public void ShouldCheckTheMaximumStringLength()
        {
            StringDecoder Decoder = new StringDecoder(2, bufPool);

            // 2 Characters are ok
            var buf = new Buffer();
            buf.WriteByte(0x02);
            buf.WriteByte('a');
            buf.WriteByte('b');
            var consumed = Decoder.Decode(buf.View);
            Assert.True(Decoder.Done);
            Assert.Equal("ab", Decoder.Result);
            Assert.Equal(2, Decoder.StringLength);
            Assert.Equal(3, consumed);

            // 3 should fail
            buf = new Buffer();
            buf.WriteByte(0x03);
            buf.WriteByte('a');
            buf.WriteByte('b');
            buf.WriteByte('c');
            var ex = Assert.Throws<Exception>(() => Decoder.Decode(buf.View));
            Assert.Equal("Maximum string length exceeded", ex.Message);

            // Things were the length is stored in a continuation byte should also fail
            buf = new Buffer();
            buf.WriteByte(0x7F); // More than 127 bytes
            consumed = Decoder.Decode(buf.View);
            Assert.False(Decoder.Done);
            Assert.Equal(1, consumed);
            buf.WriteByte(1);
            var view = new ArraySegment<byte>(buf.Bytes, 1, 1);
            ex = Assert.Throws<Exception>(() => Decoder.DecodeCont(view));
            Assert.Equal("Maximum string length exceeded", ex.Message);

        }
    }
}
