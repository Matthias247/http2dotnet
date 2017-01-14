using System;

using Xunit;

using Http2.Hpack;

namespace HpackTests
{
    public class HuffmanDecodeTests
    {
        [Fact]
        public void ShouldDecodeValidHuffmanCodes()
        {
            var buffer = new Buffer();
            buffer.WriteByte(0xff);
            buffer.WriteByte(0xc7);
            var decoded = Huffman.Decode(buffer.View);
            Assert.Equal(decoded.Length, 1);
            Assert.Equal(decoded[0], 0);

            buffer = new Buffer();
            buffer.WriteByte(0xf8); // '&'
            decoded = Huffman.Decode(buffer.View);
            Assert.Equal(decoded.Length, 1);
            Assert.Equal(decoded[0], '&');

            buffer = new Buffer();
            buffer.WriteByte(0x59); // 0101 1001
            buffer.WriteByte(0x7f); // 0111 1111
            buffer.WriteByte(0xff); // 1111 1111
            buffer.WriteByte(0xe1); // 1110 0001
            decoded = Huffman.Decode(buffer.View);
            Assert.Equal(decoded.Length, 3);
            Assert.Equal(decoded[0], '-');
            Assert.Equal(decoded[1], '.');
            Assert.Equal(decoded[2], '\\');
        }

        [Fact]
        public void ShouldThrowErrorIfEOSSymbolIsEncountered()
        {
            var buffer = new Buffer();
            buffer.WriteByte(0xf8); // '&'
            buffer.WriteByte(0xff);
            buffer.WriteByte(0xff);
            buffer.WriteByte(0xff);
            buffer.WriteByte(0xfc);
            var ex = Assert.Throws<Exception>(() => {
                Huffman.Decode(buffer.View);
            });
            Assert.Equal(ex.Message, "Encountered EOS in huffman code");
        }

        // A seperate test case that uses an invalid huffman code does not make sense, because
        // the first invalid huffman code is EOS, and that case is captured
    }
}
