using System;
using System.Collections.Generic;
using System.Text;
using System.Reflection;
using Xunit;
using Hpack;

namespace HpackTests
{
    public class DecoderTests
    {
        static DynamicTable GetDynamicTableOfDecoder(Hpack.Decoder decoder)
        {
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var fi = decoder.GetType().GetTypeInfo().GetField("_headerTable", flags);
            var headerTable = (HeaderTable)fi.GetValue(decoder);
            fi = headerTable.GetType().GetField("dynamic", flags);
            DynamicTable dtable = (DynamicTable)fi.GetValue(headerTable);
            return dtable;
        }

        [Fact]
        public void ShouldHaveADefaultDynamicTableSizeAndLimitOf4096()
        {
            Hpack.Decoder decoder = new Hpack.Decoder();

            Assert.Equal(4096, decoder.DynamicTableSize);
            Assert.Equal(4096, decoder.DynamicTableSizeLimit);
        }

        [Fact]
        public void ShouldAllowToAdjustTheDynamicTableSizeAndLimtThroughConstructor()
        {
            Hpack.Decoder decoder = new Hpack.Decoder(new Hpack.Decoder.Options{
                DynamicTableSize = 0,
                DynamicTableSizeLimit = 10
            });

            Assert.Equal(0, decoder.DynamicTableSize);
            Assert.Equal(10, decoder.DynamicTableSizeLimit);
        }

        [Fact]
        public void ShouldAllowReadingAFullyIndexedValueFromTheStaticTable()
        {
            Hpack.Decoder decoder = new Hpack.Decoder();

            var buf = new Buffer();
            buf.WriteByte(0x81);
            var consumed = decoder.Decode(buf.View);
            Assert.True(decoder.Done);
            Assert.Equal(":authority", decoder.HeaderField.Name);
            Assert.Equal("", decoder.HeaderField.Value);
            Assert.False(decoder.HeaderField.Sensitive);
            Assert.Equal(42, decoder.HeaderSize);
            Assert.Equal(1, consumed);

            buf.WriteByte(0x82);
            consumed = decoder.Decode(new ArraySegment<byte>(buf.Bytes, 1, 1));
            Assert.True(decoder.Done);
            Assert.Equal(":method", decoder.HeaderField.Name);
            Assert.Equal("GET", decoder.HeaderField.Value);
            Assert.False(decoder.HeaderField.Sensitive);
            Assert.Equal(42, decoder.HeaderSize);
            Assert.Equal(1, consumed);

            buf.WriteByte(0xBD);
            consumed = decoder.Decode(new ArraySegment<byte>(buf.Bytes, 2, 1));
            Assert.True(decoder.Done);
            Assert.Equal("www-authenticate", decoder.HeaderField.Name);
            Assert.Equal("", decoder.HeaderField.Value);
            Assert.False(decoder.HeaderField.Sensitive);
            Assert.Equal(48, decoder.HeaderSize);
            Assert.Equal(1, consumed);
        }

        [Fact]
        public void ShouldAllowReadingAFullyIndexedValueFromTheStaticAndDynamicTable()
        {
            var decoder = new Hpack.Decoder(new Hpack.Decoder.Options{
                DynamicTableSize = 100000,
                DynamicTableSizeLimit = 100000,
            });

            var dtable = GetDynamicTableOfDecoder(decoder);

            for (var i = 100; i >= 0; i--) {
                // Add elements into the dynamic table
                // Insert in backward fashion because the last insertion
                // will get the lowest index
                dtable.Insert("key"+i, 1, "val"+i, 1);
            }

            var buf = new Buffer();
            buf.WriteByte(0xFF); // Prefix 127
            var consumed = decoder.Decode(buf.View);
            Assert.False(decoder.Done);
            Assert.Equal(1, consumed);
            consumed = decoder.Decode(new ArraySegment<byte>(buf.Bytes, 1, 0));
            Assert.False(decoder.Done);
            Assert.Equal(0, consumed);
            // Add next byte
            buf.WriteByte(0x0A); // 127 + 10
            consumed = decoder.Decode(new ArraySegment<byte>(buf.Bytes, 1, 1));
            Assert.True(decoder.Done);
            Assert.Equal(1, consumed);

            var targetIndex = 137 - StaticTable.Length - 1;
            Assert.Equal("key"+targetIndex, decoder.HeaderField.Name);
            Assert.Equal("val"+targetIndex, decoder.HeaderField.Value);
            Assert.False(decoder.HeaderField.Sensitive);
            Assert.Equal(34, decoder.HeaderSize);
        }

        [Fact]
        public void ShouldThrowAnErrorWhenReadingFullyIndexedValueOnInvalidIndex()
        {
            var decoder = new Hpack.Decoder();
            var buf = new Buffer();
            buf.WriteByte(0x80); // Index 0 // TODO: Might be used for something different
            var ex = Assert.Throws<Exception>(() => decoder.Decode(buf.View));
            Assert.Equal("", ex.Message);

            decoder = new Hpack.Decoder();
            buf = new Buffer();
            buf.WriteByte(0xC0); // 1100 0000 => Index 64 is outside of static table
            ex = Assert.Throws<Exception>(() => decoder.Decode(buf.View));
            Assert.Equal("", ex.Message);

            // Put enough elements into the dynamic table to reach 64 in total
            decoder = new Hpack.Decoder();
            var neededAdds = 64 - StaticTable.Length - 1; // -1 because 1 is not a valid index
            var dtable = GetDynamicTableOfDecoder(decoder);
            for (var i = neededAdds; i >= 0; i--)
            {
                // Add elements into the dynamic table
                // Insert in backward fashion because the last insertion
                // will get the lowest index
                dtable.Insert("key"+i, 1, "val"+i, 1);
            }
            // This should now no longer throw
            var consumed = decoder.Decode(buf.View);

            // Increase index by 1 should lead to throw again
            buf = new Buffer();
            buf.WriteByte(0xC1); // 1100 0001 => Index 66 is outside of static and dynamic table
            ex = Assert.Throws<Exception>(() => decoder.Decode(buf.View));
            Assert.Equal("", ex.Message);
        }

        [Fact]
        public void ShouldAllowReceivingIncrementalIndexedFieldsWithIndexedName()
        {
            var decoder = new Hpack.Decoder();

            var buf = new Buffer();
            buf.WriteByte(0x42); // Incremental indexed, header index 2
            buf.WriteByte(0x03); // 3 bytes
            buf.WriteString("abc");
            var consumed = decoder.Decode(buf.View);
            Assert.True(decoder.Done);
            Assert.Equal(":method", decoder.HeaderField.Name);
            Assert.Equal("abc", decoder.HeaderField.Value);
            Assert.False(decoder.HeaderField.Sensitive);
            Assert.Equal(42, decoder.HeaderSize);
            Assert.Equal(5, consumed);
            Assert.Equal(1, decoder.DynamicTableLength);
            Assert.Equal(32+7+3, decoder.DynamicTableUsedSize);
        }

        [Fact]
        public void ShouldAllowReceivingIncrementalIndexedFieldsWithNotIndexedName()
        {
            var decoder = new Hpack.Decoder();

            var buf = new Buffer();
            buf.WriteByte(0x40); // Incremental indexed, no name index
            buf.WriteByte(0x02);
            buf.WriteString("de");
            buf.WriteByte(0x03);
            buf.WriteString("fgh");
            var consumed = decoder.Decode(buf.View);
            Assert.True(decoder.Done);
            Assert.Equal("de", decoder.HeaderField.Name);
            Assert.Equal("fgh", decoder.HeaderField.Value);
            Assert.False(decoder.HeaderField.Sensitive);
            Assert.Equal(372, decoder.HeaderSize);
            Assert.Equal(8, consumed);
            Assert.Equal(1, decoder.DynamicTableLength);
            Assert.Equal(32+2+3, decoder.DynamicTableUsedSize);

            var emptyView = new ArraySegment<byte>(null, 20, 20);

            // Add a second entry to it, this time in chunks
            buf = new Buffer();
            buf.WriteByte(0x40);
            consumed = decoder.Decode(buf.View);
            Assert.False(decoder.Done);
            Assert.Equal(1, consumed);
            consumed = decoder.Decode(emptyView);
            Assert.False(decoder.Done);
            Assert.Equal(0, consumed);
            buf = new Buffer();
            buf.WriteByte(0x02);
            consumed = decoder.Decode(buf.View);
            Assert.False(decoder.Done);
            Assert.Equal(1, consumed);
            consumed = decoder.Decode(emptyView);
            Assert.False(decoder.Done);
            Assert.Equal(0, consumed);
            buf = new Buffer();
            buf.WriteByte('z');
            consumed = decoder.Decode(buf.View);
            Assert.False(decoder.Done);
            Assert.Equal(1, consumed);
            consumed = decoder.Decode(emptyView);
            Assert.False(decoder.Done);
            Assert.Equal(0, consumed);
            buf = new Buffer();
            buf.WriteByte('1');
            consumed = decoder.Decode(buf.View);
            Assert.False(decoder.Done);
            Assert.Equal(1, consumed);
            consumed = decoder.Decode(emptyView);
            Assert.False(decoder.Done);
            Assert.Equal(0, consumed);
            buf = new Buffer();
            buf.WriteByte(0x03);
            consumed = decoder.Decode(buf.View);
            Assert.False(decoder.Done);
            Assert.Equal(1, consumed);
            consumed = decoder.Decode(emptyView);
            Assert.False(decoder.Done);
            Assert.Equal(0, consumed);
            buf.WriteByte('2');
            consumed = decoder.Decode(buf.View);
            Assert.False(decoder.Done);
            Assert.Equal(1, consumed);
            buf = new Buffer();
            buf.WriteByte('3');
            consumed = decoder.Decode(buf.View);
            Assert.False(decoder.Done);
            Assert.Equal(1, consumed);
            buf = new Buffer();
            buf.WriteByte('4');
            consumed = decoder.Decode(buf.View);
            Assert.True(decoder.Done);
            Assert.Equal("z1", decoder.HeaderField.Name);
            Assert.Equal("234", decoder.HeaderField.Value);
            Assert.False(decoder.HeaderField.Sensitive);
            Assert.Equal(37, decoder.HeaderSize);
            Assert.Equal(1, consumed);
            Assert.Equal(decoder.DynamicTableLength, 2);
            Assert.Equal(decoder.DynamicTableUsedSize, 32+2+3+32+2+3);
        }

        [Fact]
        public void ShouldAllowReceivingNotIndexedFieldsWithIndexedName()
        {
            var decoder = new Hpack.Decoder();

            var buf = new Buffer();
            buf.WriteByte(0x02); // Not indexed, header index 2
            buf.WriteByte(0x03); // 3 bytes
            buf.WriteString("abc");
            var consumed = decoder.Decode(buf.View);
            Assert.True(decoder.Done);
            Assert.Equal(":method", decoder.HeaderField.Name);
            Assert.Equal("abc", decoder.HeaderField.Value);
            Assert.False(decoder.HeaderField.Sensitive);
            Assert.Equal(42, decoder.HeaderSize);
            Assert.Equal(5, consumed);
            Assert.Equal(decoder.DynamicTableLength, 0);
            Assert.Equal(decoder.DynamicTableUsedSize, 0);
        }

        [Fact]
        public void ShouldAllowReceivingNotIndexedFieldsWithNotIndexedName()
        {
            var decoder = new Hpack.Decoder();

            var buf = new Buffer();
            buf.WriteByte(0x00); // Not indexed, no name index
            buf.WriteByte(0x02);
            buf.WriteString("de");
            buf.WriteByte(0x03);
            buf.WriteString("fgh");
            var consumed = decoder.Decode(buf.View);
            Assert.True(decoder.Done);
            Assert.Equal("de", decoder.HeaderField.Name);
            Assert.Equal("fgh", decoder.HeaderField.Value);
            Assert.False(decoder.HeaderField.Sensitive);
            Assert.Equal(37, decoder.HeaderSize);
            Assert.Equal(8, consumed);
            Assert.Equal(decoder.DynamicTableLength, 0);
            Assert.Equal(decoder.DynamicTableUsedSize, 0);
        }

        [Fact]
        public void ShouldAllowReceivingNeverIndexedFieldsWithIndexedName()
        {
            var decoder = new Hpack.Decoder();

            var buf = new Buffer();
            buf.WriteByte(0x12); // Never indexed, header index 2
            buf.WriteByte(0x03); // 3 bytes
            buf.WriteString("abc");
            var consumed = decoder.Decode(buf.View);
            Assert.True(decoder.Done);
            Assert.Equal(":method", decoder.HeaderField.Name);
            Assert.Equal("abc", decoder.HeaderField.Value);
            Assert.True(decoder.HeaderField.Sensitive);
            Assert.Equal(42, decoder.HeaderSize);
            Assert.Equal(5, consumed);
            Assert.Equal(decoder.DynamicTableLength, 0);
            Assert.Equal(decoder.DynamicTableUsedSize, 0);
        }

        [Fact]
        public void ShouldAllowReceivingNeverIndexedFieldsWithNotIndexedName()
        {
            var decoder = new Hpack.Decoder();

            var buf = new Buffer();
            buf.WriteByte(0x10); // Never indexed, no name index
            buf.WriteByte(0x02);
            buf.WriteString("de");
            buf.WriteByte(0x03);
            buf.WriteString("fgh");
            var consumed = decoder.Decode(buf.View);
            Assert.True(decoder.Done);
            Assert.Equal("de", decoder.HeaderField.Name);
            Assert.Equal("fgh", decoder.HeaderField.Value);
            Assert.True(decoder.HeaderField.Sensitive);
            Assert.Equal(37, decoder.HeaderSize);
            Assert.Equal(8, consumed);
            Assert.Equal(decoder.DynamicTableLength, 0);
            Assert.Equal(decoder.DynamicTableUsedSize, 0);
        }

        [Fact]
        public void ShouldHandleTableSizeUpdateFrames()
        {
            var decoder = new Hpack.Decoder();

            // Table update in single step
            var buf = new Buffer();
            buf.WriteByte(0x30);
            var consumed = decoder.Decode(buf.View);
            Assert.False(decoder.Done);
            Assert.Equal(16, decoder.DynamicTableSize);
            Assert.Equal(1, consumed);

            // Table update in multiple steps
            buf = new Buffer();
            buf.WriteByte(0x3F); // I = 31
            consumed = decoder.Decode(buf.View);
            Assert.False(decoder.Done);
            Assert.Equal(16, decoder.DynamicTableSize);
            Assert.Equal(1, consumed);
            // 2nd data part
            buf = new Buffer();
            buf.WriteByte(0x80); // I = 31
            consumed = decoder.Decode(buf.View);
            Assert.False(decoder.Done);
            Assert.Equal(16, decoder.DynamicTableSize);
            Assert.Equal(1, consumed);
            buf = new Buffer();
            buf.WriteByte(0x10); // I = 31 + 0x10 * 2^7 = 2079
            consumed = decoder.Decode(buf.View);
            Assert.False(decoder.Done);
            Assert.Equal(2079, decoder.DynamicTableSize);
            Assert.Equal(1, consumed);
        }

        [Fact]
        public void ShouldThrowAnErrorIfTableSizeUpdateExceedsLimit()
        {
            var decoder = new Hpack.Decoder();

            decoder.DynamicTableSizeLimit = 100;
            var buf = new Buffer();
            buf.WriteByte(0x3F); // I = 31
            buf.WriteByte(0x80); // I = 31
            buf.WriteByte(0x10); // I = 31 + 0x10 * 2^7 = 2079
            var ex = Assert.Throws<Exception>(() => decoder.Decode(buf.View));
            Assert.Equal("table size limit exceeded", ex.Message);
        }

        [Fact]
        public void ShouldNotCrashWhenDecodeIsStartedWith0Bytes()
        {
            var decoder = new Hpack.Decoder();

            var buf = new Buffer();
            var consumed = decoder.Decode(buf.View);
            Assert.False(decoder.Done);
            Assert.Equal(0, consumed);
        }

        [Fact]
        public void ShouldNotCrashWhenNotNameIndexedDecodeIsContinuedWith0Bytes()
        {
            var decoder = new Hpack.Decoder();

            var emptyView = new ArraySegment<byte>(null, 20, 20);

            var buf = new Buffer();
            buf.WriteByte(0x10);
            var consumed = decoder.Decode(buf.View);
            Assert.False(decoder.Done);
            Assert.Equal(1, consumed);
            // 0 byte read
            consumed = decoder.Decode(emptyView);
            Assert.False(decoder.Done);
            Assert.Equal(0, consumed);
            buf = new Buffer();
            buf.WriteByte(0x01); // name length
            consumed = decoder.Decode(buf.View);
            Assert.False(decoder.Done);
            Assert.Equal(1, consumed);
            // 0 byte read
            consumed = decoder.Decode(emptyView);
            Assert.False(decoder.Done);
            Assert.Equal(0, consumed);
            buf = new Buffer();
            buf.WriteByte('a'); // name value
            consumed = decoder.Decode(buf.View);
            Assert.False(decoder.Done);
            Assert.Equal(1, consumed);
            // 0 byte read
            consumed = decoder.Decode(emptyView);
            Assert.False(decoder.Done);
            Assert.Equal(0, consumed);
            buf = new Buffer();
            buf.WriteByte(0x01); // value length
            consumed = decoder.Decode(buf.View);
            Assert.False(decoder.Done);
            Assert.Equal(1, consumed);
            // 0 byte read
            consumed = decoder.Decode(emptyView);
            Assert.False(decoder.Done);
            Assert.Equal(0, consumed);
            buf = new Buffer();
            buf.WriteByte('b'); // value value
            consumed = decoder.Decode(buf.View);
            Assert.True(decoder.Done);
            Assert.Equal(1, consumed);
            Assert.Equal("a", decoder.HeaderField.Name);
            Assert.Equal("b", decoder.HeaderField.Value);
            Assert.True(decoder.HeaderField.Sensitive);
            Assert.Equal(34, decoder.HeaderSize);
        }

        [Fact]
        public void ShouldNotCrashWhenNameIndexedDecodeIsContinuedWith0Bytes()
        {
            // Need more entries in the dynamic table
            var decoder = new Hpack.Decoder(new Hpack.Decoder.Options{
                DynamicTableSize = 100000,
                DynamicTableSizeLimit = 100000,
            });

            var emptyView = new ArraySegment<byte>(null, 20, 20);

            var dtable = GetDynamicTableOfDecoder(decoder);
            for (var i = 99; i >= 0; i--) {
                // Add elements into the dynamic table
                // Insert in backward fashion because the last insertion
                // will get the lowest index
                dtable.Insert("key"+i, 1, "val"+i, 1);
            }
            Assert.Equal(100, decoder.DynamicTableLength);
            Assert.Equal(34*100, decoder.DynamicTableUsedSize);

            // Need a more than 1byte index value for this test
            var buf = new Buffer();
            buf.WriteByte(0x1F); // prefix filled with 15
            var consumed = decoder.Decode(buf.View);
            Assert.False(decoder.Done);
            Assert.Equal(1, consumed);
            // 0 byte read
            consumed = decoder.Decode(emptyView);
            Assert.False(decoder.Done);
            Assert.Equal(0, consumed);

            buf = new Buffer();
            buf.WriteByte(0x80); // cont index
            consumed = decoder.Decode(buf.View);
            Assert.False(decoder.Done);
            Assert.Equal(1, consumed);
            // 0 byte read
            consumed = decoder.Decode(emptyView);
            Assert.False(decoder.Done);
            Assert.Equal(0, consumed);

            buf = new Buffer();
            buf.WriteByte(0x01); // final index, value = 15 + 0 + 1 * 2^7 = 143
            consumed = decoder.Decode(buf.View);
            Assert.False(decoder.Done);
            Assert.Equal(1, consumed);
            // 0 byte read
            consumed = decoder.Decode(emptyView);
            Assert.False(decoder.Done);
            Assert.Equal(0, consumed);

            buf = new Buffer();
            buf.WriteByte(0x02); // string length
            consumed = decoder.Decode(buf.View);
            Assert.False(decoder.Done);
            Assert.Equal(1, consumed);
            // 0 byte read
            consumed = decoder.Decode(emptyView);
            Assert.False(decoder.Done);
            Assert.Equal(0, consumed);

            buf = new Buffer();
            buf.WriteByte('a'); // name value 1
            consumed = decoder.Decode(buf.View);
            Assert.False(decoder.Done);
            Assert.Equal(1, consumed);
            // 0 byte read
            consumed = decoder.Decode(emptyView);
            Assert.False(decoder.Done);
            Assert.Equal(0, consumed);

            buf = new Buffer();
            buf.WriteByte('9'); // name value 2
            consumed = decoder.Decode(buf.View);
            Assert.True(decoder.Done);
            Assert.Equal(1, consumed);
            var tableIdx = 143 - StaticTable.Length - 1;
            Assert.Equal("key" + tableIdx, decoder.HeaderField.Name);
            Assert.Equal("a9", decoder.HeaderField.Value);
            Assert.True(decoder.HeaderField.Sensitive);
            Assert.Equal(35, decoder.HeaderSize);
        }

        [Fact]
        public void ShouldNotCrashWhenFullyIndexedDecodeIsContinuiedWith0Bytes()
        {
            // Need more entries in the dynamic table
            var decoder = new Hpack.Decoder(new Hpack.Decoder.Options {
                DynamicTableSize = 100000,
                DynamicTableSizeLimit = 100000,
            });

            var emptyView = new ArraySegment<byte>(null, 20, 20);
            var dtable = GetDynamicTableOfDecoder(decoder);
            for (var i = 199; i >= 0; i--)
            {
                // Add elements into the dynamic table
                // Insert in backward fashion because the last insertion
                // will get the lowest index
                dtable.Insert("key"+i, 1, "val"+i, 1);
            }
            Assert.Equal(200, decoder.DynamicTableLength);
            Assert.Equal(34*200, decoder.DynamicTableUsedSize);

            // Need a more than 1byte index value for this test
            var buf = new Buffer();
            buf.WriteByte(0xFF); // prefix filled with 127
            var consumed = decoder.Decode(buf.View);
            Assert.False(decoder.Done);
            Assert.Equal(1, consumed);
            // 0 byte read
            consumed = decoder.Decode(emptyView);
            Assert.False(decoder.Done);
            Assert.Equal(0, consumed);

            buf = new Buffer();
            buf.WriteByte(0x80); // cont index
            consumed = decoder.Decode(buf.View);
            Assert.False(decoder.Done);
            Assert.Equal(1, consumed);
            // 0 byte read
            consumed = decoder.Decode(emptyView);
            Assert.False(decoder.Done);
            Assert.Equal(0, consumed);

            buf = new Buffer();
            buf.WriteByte(0x01); // final index, value = 127 + 0 + 1 * 2^7 = 255
            consumed = decoder.Decode(buf.View);
            Assert.True(decoder.Done);
            Assert.Equal(1, consumed);
            var tableIdx = 255 - StaticTable.Length - 1;
            Assert.Equal("key" + tableIdx, decoder.HeaderField.Name);
            Assert.Equal("val" + tableIdx, decoder.HeaderField.Value);
            Assert.False(decoder.HeaderField.Sensitive);
            Assert.Equal(34, decoder.HeaderSize);
        }

        [Fact]
        public void ShouldHandleExampleC2_1OfTheSpecificationCorrectly()
        {
            var decoder = new Hpack.Decoder();
            var buf = new Buffer(
            '400a637573746f6d2d6b65790d637573' +
            '746f6d2d686561646572', 'hex');

            var consumed = decoder.Decode(buf.View);
            Assert.True(decoder.Done);
            Assert.Equal("custom-key", decoder.HeaderField.Name);
            Assert.Equal("custom-header", decoder.HeaderField.Value);
            Assert.Equal(55, decoder.HeaderSize);
            Assert.Equal(1, decoder.DynamicTableLength);
            Assert.Equal(55, decoder.DynamicTableUsedSize);
        }

        [Fact]
        public void ShouldHandleExampleC2_2OfTheSpecificationCorrectly()
        {
            var decoder = new Hpack.Decoder();
            var buf = new Buffer(
            '040c2f73616d706c652f70617468', 'hex');

            var consumed = decoder.Decode(buf.View);
            Assert.True(decoder.Done);
            Assert.Equal(":path", decoder.HeaderField.Name);
            Assert.Equal("/sample/path", decoder.HeaderField.Value);
            Assert.Equal(49, decoder.HeaderSize);
            Assert.Equal(0, decoder.DynamicTableLength);
            Assert.Equal(0, decoder.DynamicTableUsedSize);
        }

        [Fact]
        public void ShouldHandleExampleC2_3OfTheSpecificationCorrectly()
        {
            var decoder = new Hpack.Decoder();
            var buf = new Buffer(
            '100870617373776f726406736563726574', 'hex');

            var consumed = decoder.Decode(buf.View);
            Assert.True(decoder.Done);
            Assert.Equal("password", decoder.HeaderField.Name);
            Assert.Equal("secret", decoder.HeaderField.Value);
            Assert.Equal(46, decoder.HeaderSize);
            Assert.Equal(0, decoder.DynamicTableLength);
            Assert.Equal(0, decoder.DynamicTableUsedSize);
        }

        [Fact]
        public void ShouldHandleExampleC2_4OfTheSpecificationCorrectly()
        {
            var decoder = new Hpack.Decoder();
            var buf = new Buffer(
            '82', 'hex');

            var consumed = decoder.Decode(buf.View);
            Assert.True(decoder.Done);
            Assert.Equal(":method", decoder.HeaderField.Name);
            Assert.Equal("GET", decoder.HeaderField.Value);
            Assert.Equal(42, decoder.HeaderSize);
            Assert.Equal(0, decoder.DynamicTableLength);
            Assert.Equal(0, decoder.DynamicTableUsedSize);
        }

    }
}
