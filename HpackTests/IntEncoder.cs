using System;
using System.Collections.Generic;
using System.Text;
using Xunit;
using Xunit.Abstractions;
using Hpack;

namespace HpackTests
{
    public class IntEncoderTests
    {
        [Fact]
        public void ShouldEncodeValuesWhichFitIntoThePrefix()
        {
            uint val = 30; // Fits into 5bit prefix
            var buf = IntEncoder.Encode(val, 0x80, 5);
            Assert.Equal(buf.Length, 1);
            Assert.Equal(buf[0], 0x80 | 0x1E);

            val = 1; // Fits into 2 bit prefix
            buf = IntEncoder.Encode(val, 0xFC, 2);
            Assert.Equal(buf.Length, 1);
            Assert.Equal(buf[0], 0xFC | 0x01);

            val = 128; // Fits into 8 bit prefix
            buf = IntEncoder.Encode(val, 0x00, 8);
            Assert.Equal(buf.Length, 1);
            Assert.Equal(buf[0], 0x80);

            val = 254; // Fits into 8 bit prefix
            buf = IntEncoder.Encode(val, 0x00, 8);
            Assert.Equal(buf.Length, 1);
            Assert.Equal(buf[0], 254);
        }

        [Fact]
        public void ShouldEncodeValuesIntoPrefixPlusExtraBytes()
        {
            uint val = 30; // Fits not into 4bit prefix
            var buf = IntEncoder.Encode(val, 0xA0, 4);
            Assert.Equal(buf.Length, 2);
            Assert.Equal(buf[0], 0xA0 | 0x0F);
            Assert.Equal(buf[1], 15); // 30 - 15 = 15

            val = 1; // Fits not into 1bit prefix
            buf = IntEncoder.Encode(val, 0xFE, 1);
            Assert.Equal(buf.Length, 2);
            Assert.Equal(buf[0], 0xFE | 0x01);
            Assert.Equal(buf[1], 0);

            val = 127; // Fits not into 1bit prefix
            buf = IntEncoder.Encode(val, 0x80, 7);
            Assert.Equal(buf.Length, 2);
            Assert.Equal(buf[0], 0x80 | 0xFF);
            Assert.Equal(buf[1], 0);

            val = 128; // Fits not into 1bit prefix
            buf = IntEncoder.Encode(val, 0x00, 7);
            Assert.Equal(buf.Length, 2);
            Assert.Equal(buf[0], 0x00 | 0x7F);
            Assert.Equal(buf[1], 1);

            val = 255; // Fits not into 8 bit prefix
            buf = IntEncoder.Encode(val, 0x00, 8);
            Assert.Equal(buf.Length, 2);
            Assert.Equal(buf[0], 0xFF);
            Assert.Equal(buf[1], 0);

            val = 256; // Fits not into 8 bit prefix
            buf = IntEncoder.Encode(val, 0x00, 8);
            Assert.Equal(buf.Length, 2);
            Assert.Equal(buf[0], 0xFF);
            Assert.Equal(buf[1], 1);

            val = 1337; // 3byte example from the spec
            buf = IntEncoder.Encode(val, 0xC0, 5);
            Assert.Equal(buf.Length, 3);
            Assert.Equal(buf[0], 0xC0 | 0x1F);
            Assert.Equal(buf[1], 0x9A);
            Assert.Equal(buf[2], 0x0A);

            // 4 byte example
            val = 27 * 128 * 128 + 31 * 128 + 1;
            buf = IntEncoder.Encode(val, 0, 1);
            Assert.Equal(buf.Length, 4);
            Assert.Equal(buf[0], 1);
            Assert.Equal(buf[1], 0x80);
            Assert.Equal(buf[2], 0x9F);
            Assert.Equal(buf[3], 27);
        }

        [Fact]
        public void ShouldEncodeTheMaximumAllowedValue()
        {
            uint val = UInt32.MaxValue; // Fits not into 4bit prefix
            var buf = IntEncoder.Encode(val, 0xA0, 2);
            Assert.Equal(buf.Length, 6);
            Assert.Equal(buf[0], 0xA3); // Remaining: 4294967292
            Assert.Equal(buf[1], 252); // Remaining: 33554431
            Assert.Equal(buf[2], 255); // Remaining: 262143
            Assert.Equal(buf[3], 255); // Remaining: 2047
            Assert.Equal(buf[4], 255); // Remaining: 15
            Assert.Equal(buf[5], 15); // Remaining: 15

            // TODO: Probably test this with other prefixes
        }
    }
}
