using System;
using System.Buffers;

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
            var decoded = Huffman.Decode(buffer.View, ArrayPool<byte>.Shared);
            Assert.Equal(decoded.Length, 1);
            Assert.Equal(decoded[0], 0);

            buffer = new Buffer();
            buffer.WriteByte(0xf8); // '&'
            decoded = Huffman.Decode(buffer.View, ArrayPool<byte>.Shared);
            Assert.Equal(decoded.Length, 1);
            Assert.Equal(decoded[0], '&');

            buffer = new Buffer();
            buffer.WriteByte(0x59); // 0101 1001
            buffer.WriteByte(0x7f); // 0111 1111
            buffer.WriteByte(0xff); // 1111 1111
            buffer.WriteByte(0xe1); // 1110 0001
            decoded = Huffman.Decode(buffer.View, ArrayPool<byte>.Shared);
            Assert.Equal(decoded.Length, 3);
            Assert.Equal(decoded[0], '-');
            Assert.Equal(decoded[1], '.');
            Assert.Equal(decoded[2], '\\');

            buffer = new Buffer();
            buffer.WriteByte(0x86); // AB = 100001 1011101 = 1000 0110 1110 1
            buffer.WriteByte(0xEF);
            decoded = Huffman.Decode(buffer.View, ArrayPool<byte>.Shared);
            Assert.Equal(decoded.Length, 2);
            Assert.Equal(decoded[0], 'A');
            Assert.Equal(decoded[1], 'B');
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ShouldThrowErrorIfEOSSymbolIsEncountered(bool fillLastByte)
        {
            var buffer = new Buffer();
            buffer.WriteByte(0xf8); // '&'
            buffer.WriteByte(0xff);
            buffer.WriteByte(0xff);
            buffer.WriteByte(0xff);
            byte lastByte = fillLastByte ? (byte)0xff : (byte)0xfc; // Both contain EOS
            buffer.WriteByte(lastByte);
            var ex = Assert.Throws<Exception>(() => {
                Huffman.Decode(buffer.View, ArrayPool<byte>.Shared);
            });
            Assert.Equal(ex.Message, "Encountered EOS in huffman code");
        }

        [Fact]
        public void ShouldThrowErrorIfPaddingIsZero()
        {
            var buffer = new Buffer();
            buffer.WriteByte(0x86); // AB = 100001 1011101 = 1000 0110 1110 1
            buffer.WriteByte(0xE8);
            var ex = Assert.Throws<Exception>(() => {
                Huffman.Decode(buffer.View, ArrayPool<byte>.Shared);
            });
            Assert.Equal("Invalid padding", ex.Message);
        }

        [Fact]
        public void ShouldThrowErrorIfPaddingIsLongerThanNecessary()
        {
            var buffer = new Buffer();
            buffer.WriteByte(0x86); // AB = 100001 1011101 = 1000 0110 1110 1
            buffer.WriteByte(0xEF);
            buffer.WriteByte(0xFF); // Extra padding
            var ex = Assert.Throws<Exception>(() => {
                Huffman.Decode(buffer.View, ArrayPool<byte>.Shared);
            });
            Assert.Equal("Padding exceeds 7 bits", ex.Message);

            buffer = new Buffer();
            buffer.WriteByte(0xFA); // ',' = 0xFA
            buffer.WriteByte(0xFF); // Padding
            ex = Assert.Throws<Exception>(() => {
                Huffman.Decode(buffer.View, ArrayPool<byte>.Shared);
            });
            Assert.Equal("Padding exceeds 7 bits", ex.Message);
        }
    }
}
