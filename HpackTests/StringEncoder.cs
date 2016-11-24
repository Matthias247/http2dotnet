using System;
using System.Collections.Generic;
using System.Text;
using Xunit;
using Hpack;

namespace HpackTests
{
    public class StringEncoderTests
    {
        [Fact]
        public void ShouldEncodeStringsWithoutHuffmanEncoding()
        {
            var testStr = "Hello World";
            var bytes = StringEncoder.Encode(testStr, false);
            Assert.Equal(12, bytes.Length);

            // Compare the bytes
            Assert.Equal(11, bytes[0]);
            for (var i = 0; i < testStr.Length; i++)
            {
                var c = testStr[i];
                Assert.Equal((byte)c, bytes[1+i]);
            }

            // Test with a longer string
            testStr = "";
            for (var i = 0; i < 64; i++)
            {
                testStr += "a";
            }
            for (var i = 0; i < 64; i++)
            {
                testStr += "b";
            }
            bytes = StringEncoder.Encode(testStr, false);
            Assert.Equal(130, bytes.Length);

            // Compare the bytes
            Assert.Equal(127, bytes[0]);
            Assert.Equal(1, bytes[1]);
            for (var i = 0; i < testStr.Length; i++)
            {
                var c = testStr[i];
                Assert.Equal((byte)c, bytes[2+i]);
            }
        }

        [Fact]
        public void ShouldEncodeStringsWithHuffmanEncoding()
        {
            var testStr = "Hello";
            // 1100011 00101 101000 101000 00111
            // 11000110 01011010 00101000 00111
            // var expectedResult = 0xC65A283F;
            var bytes = StringEncoder.Encode(testStr, true);
            Assert.Equal(5, bytes.Length);

            // Compare the bytes
            Assert.Equal(0x84, bytes[0]);
            Assert.Equal(0xC6, bytes[1]);
            Assert.Equal(0x5A, bytes[2]);
            Assert.Equal(0x28, bytes[3]);
            Assert.Equal(0x3F, bytes[4]);

            // Test with a longer string
            testStr = "";
            for (var i = 0; i < 64; i++)
            {
                testStr += (char)9; // ffffea  [24]
                testStr += "Z"; // fd  [ 8]
            }

            bytes = StringEncoder.Encode(testStr, true);
            Assert.Equal(3+4*64, bytes.Length);

            // Compare the bytes
            Assert.Equal(255, bytes[0]); // 127
            Assert.Equal(0x81, bytes[1]); // 127 + 1 = 128
            Assert.Equal(1, bytes[2]); // 128 + 128 = 256
            for (var i = 3; i < testStr.Length; i += 4)
            {
                Assert.Equal(0xFF, bytes[i+0]);
                Assert.Equal(0xFF, bytes[i+1]);
                Assert.Equal(0xEA, bytes[i+2]);
                Assert.Equal(0xFD, bytes[i+3]);
            }
        }
    }
}
