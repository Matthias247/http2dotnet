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
            Assert.True(decoder.HasInitialState);

            buf.WriteByte(0x82);
            consumed = decoder.Decode(new ArraySegment<byte>(buf.Bytes, 1, 1));
            Assert.True(decoder.Done);
            Assert.Equal(":method", decoder.HeaderField.Name);
            Assert.Equal("GET", decoder.HeaderField.Value);
            Assert.False(decoder.HeaderField.Sensitive);
            Assert.Equal(42, decoder.HeaderSize);
            Assert.Equal(1, consumed);
            Assert.True(decoder.HasInitialState);

            buf.WriteByte(0xBD);
            consumed = decoder.Decode(new ArraySegment<byte>(buf.Bytes, 2, 1));
            Assert.True(decoder.Done);
            Assert.Equal("www-authenticate", decoder.HeaderField.Name);
            Assert.Equal("", decoder.HeaderField.Value);
            Assert.False(decoder.HeaderField.Sensitive);
            Assert.Equal(48, decoder.HeaderSize);
            Assert.Equal(1, consumed);
            Assert.True(decoder.HasInitialState);
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
            Assert.False(decoder.HasInitialState);
            consumed = decoder.Decode(new ArraySegment<byte>(buf.Bytes, 1, 0));
            Assert.False(decoder.Done);
            Assert.Equal(0, consumed);
            Assert.False(decoder.HasInitialState);
            // Add next byte
            buf.WriteByte(0x0A); // 127 + 10
            consumed = decoder.Decode(new ArraySegment<byte>(buf.Bytes, 1, 1));
            Assert.True(decoder.Done);
            Assert.Equal(1, consumed);
            Assert.True(decoder.HasInitialState);

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
            Assert.Throws<IndexOutOfRangeException>(() => decoder.Decode(buf.View));

            decoder = new Hpack.Decoder();
            buf = new Buffer();
            buf.WriteByte(0xC0); // 1100 0000 => Index 64 is outside of static table
            Assert.Throws<IndexOutOfRangeException>(() => decoder.Decode(buf.View));

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
            Assert.Throws<IndexOutOfRangeException>(() => decoder.Decode(buf.View));
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
            Assert.True(decoder.HasInitialState);
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
            Assert.Equal(37, decoder.HeaderSize);
            Assert.Equal(8, consumed);
            Assert.Equal(1, decoder.DynamicTableLength);
            Assert.Equal(32+2+3, decoder.DynamicTableUsedSize);
            Assert.True(decoder.HasInitialState);

            var emptyView = new ArraySegment<byte>(new byte[20], 20, 0);

            // Add a second entry to it, this time in chunks
            buf = new Buffer();
            buf.WriteByte(0x40);
            consumed = decoder.Decode(buf.View);
            Assert.False(decoder.Done);
            Assert.Equal(1, consumed);
            Assert.False(decoder.HasInitialState);
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
            buf = new Buffer();
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
            Assert.True(decoder.HasInitialState);
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
            Assert.True(decoder.HasInitialState);
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
            Assert.True(decoder.HasInitialState);
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
            Assert.True(decoder.HasInitialState);
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
            Assert.True(decoder.HasInitialState);
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
            Assert.True(decoder.HasInitialState);

            // Table update in multiple steps
            buf = new Buffer();
            buf.WriteByte(0x3F); // I = 31
            consumed = decoder.Decode(buf.View);
            Assert.False(decoder.Done);
            Assert.Equal(16, decoder.DynamicTableSize);
            Assert.Equal(1, consumed);
            Assert.False(decoder.HasInitialState);
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
            Assert.True(decoder.HasInitialState);
        }

        [Fact]
        public void ShouldHandleHeadersAfterATableSizeUpdateFrame()
        {
            var decoder = new Hpack.Decoder();

            // Table update in single step
            var buf = new Buffer();
            buf.WriteByte(0x30); // Table update
            buf.WriteByte(0x81); // Header frame
            var consumed = decoder.Decode(buf.View);
            Assert.True(decoder.Done);
            Assert.Equal(":authority", decoder.HeaderField.Name);
            Assert.Equal("", decoder.HeaderField.Value);
            Assert.False(decoder.HeaderField.Sensitive);
            Assert.Equal(42, decoder.HeaderSize);
            Assert.Equal(2, consumed);
            Assert.Equal(16, decoder.DynamicTableSize);
            Assert.True(decoder.HasInitialState);
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
            Assert.True(decoder.HasInitialState);
        }

        [Fact]
        public void ShouldNotCrashWhenNotNameIndexedDecodeIsContinuedWith0Bytes()
        {
            var decoder = new Hpack.Decoder();

            var emptyView = new ArraySegment<byte>(new byte[20], 20, 0);

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

            var emptyView = new ArraySegment<byte>(new byte[20], 20, 0);

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

            var emptyView = new ArraySegment<byte>(new byte[20], 20, 0);
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
            
            var buf = new Buffer();
            buf.AddHexString(
                "400a637573746f6d2d6b65790d637573" +
                "746f6d2d686561646572");

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
            var buf = new Buffer();
            buf.AddHexString("040c2f73616d706c652f70617468");

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
            var buf = new Buffer();
            buf.AddHexString("100870617373776f726406736563726574");

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
            var buf = new Buffer();
            buf.AddHexString("82");

            var consumed = decoder.Decode(buf.View);
            Assert.True(decoder.Done);
            Assert.Equal(":method", decoder.HeaderField.Name);
            Assert.Equal("GET", decoder.HeaderField.Value);
            Assert.Equal(42, decoder.HeaderSize);
            Assert.Equal(0, decoder.DynamicTableLength);
            Assert.Equal(0, decoder.DynamicTableUsedSize);
        }

        [Fact]
        public void ShouldHandleExampleC3OfTheSpecificationCorrectly()
        {
            var decoder = new Hpack.Decoder();
            // C.3.1
            var buf = new Buffer();
            buf.AddHexString("828684410f7777772e6578616d706c652e636f6d");

            var results = DecodeAll(decoder, buf);
            Assert.Equal(4, results.Count);
            Assert.Equal(":method", results[0].Name);
            Assert.Equal("GET", results[0].Value);
            Assert.Equal(":scheme", results[1].Name);
            Assert.Equal("http", results[1].Value);
            Assert.Equal(":path", results[2].Name);
            Assert.Equal("/", results[2].Value);
            Assert.Equal(":authority", results[3].Name);
            Assert.Equal("www.example.com", results[3].Value);
            Assert.Equal(57, decoder.DynamicTableUsedSize);
            Assert.Equal(1, decoder.DynamicTableLength);

            // C.3.2
            buf = new Buffer();
            buf.AddHexString("828684be58086e6f2d6361636865");

            results = DecodeAll(decoder, buf);
            Assert.Equal(5, results.Count);
            Assert.Equal(":method", results[0].Name);
            Assert.Equal("GET", results[0].Value);
            Assert.Equal(":scheme", results[1].Name);
            Assert.Equal("http", results[1].Value);
            Assert.Equal(":path", results[2].Name);
            Assert.Equal("/", results[2].Value);
            Assert.Equal(":authority", results[3].Name);
            Assert.Equal("www.example.com", results[3].Value);
            Assert.Equal("cache-control", results[4].Name);
            Assert.Equal("no-cache", results[4].Value);
            Assert.Equal(110, decoder.DynamicTableUsedSize);
            Assert.Equal(2, decoder.DynamicTableLength);

            // C.3.3
            buf = new Buffer();
            buf.AddHexString("828785bf400a637573746f6d2d6b65790c637573746f6d2d76616c7565");

            results = DecodeAll(decoder, buf);
            Assert.Equal(5, results.Count);
            Assert.Equal(":method", results[0].Name);
            Assert.Equal("GET", results[0].Value);
            Assert.Equal(":scheme", results[1].Name);
            Assert.Equal("https", results[1].Value);
            Assert.Equal(":path", results[2].Name);
            Assert.Equal("/index.html", results[2].Value);
            Assert.Equal(":authority", results[3].Name);
            Assert.Equal("www.example.com", results[3].Value);
            Assert.Equal("custom-key", results[4].Name);
            Assert.Equal("custom-value", results[4].Value);
            Assert.Equal(164, decoder.DynamicTableUsedSize);
            Assert.Equal(3, decoder.DynamicTableLength);
        }

        [Fact]
        public void ShouldHandleExampleC4OfTheSpecificationCorrectly()
        {
            var decoder = new Hpack.Decoder();
            // C.4.1
            var buf = new Buffer();
            buf.AddHexString("828684418cf1e3c2e5f23a6ba0ab90f4ff");

            var results = DecodeAll(decoder, buf);
            Assert.Equal(4, results.Count);
            Assert.Equal(":method", results[0].Name);
            Assert.Equal("GET", results[0].Value);
            Assert.Equal(":scheme", results[1].Name);
            Assert.Equal("http", results[1].Value);
            Assert.Equal(":path", results[2].Name);
            Assert.Equal("/", results[2].Value);
            Assert.Equal(":authority", results[3].Name);
            Assert.Equal("www.example.com", results[3].Value);
            Assert.Equal(57, decoder.DynamicTableUsedSize);
            Assert.Equal(1, decoder.DynamicTableLength);

            // C.4.2
            buf = new Buffer();
            buf.AddHexString("828684be5886a8eb10649cbf");

            results = DecodeAll(decoder, buf);
            Assert.Equal(5, results.Count);
            Assert.Equal(":method", results[0].Name);
            Assert.Equal("GET", results[0].Value);
            Assert.Equal(":scheme", results[1].Name);
            Assert.Equal("http", results[1].Value);
            Assert.Equal(":path", results[2].Name);
            Assert.Equal("/", results[2].Value);
            Assert.Equal(":authority", results[3].Name);
            Assert.Equal("www.example.com", results[3].Value);
            Assert.Equal("cache-control", results[4].Name);
            Assert.Equal("no-cache", results[4].Value);
            Assert.Equal(110, decoder.DynamicTableUsedSize);
            Assert.Equal(2, decoder.DynamicTableLength);

            // C.4.3
            buf = new Buffer();
            buf.AddHexString("828785bf408825a849e95ba97d7f8925a849e95bb8e8b4bf");

            results = DecodeAll(decoder, buf);
            Assert.Equal(5, results.Count);
            Assert.Equal(":method", results[0].Name);
            Assert.Equal("GET", results[0].Value);
            Assert.Equal(":scheme", results[1].Name);
            Assert.Equal("https", results[1].Value);
            Assert.Equal(":path", results[2].Name);
            Assert.Equal("/index.html", results[2].Value);
            Assert.Equal(":authority", results[3].Name);
            Assert.Equal("www.example.com", results[3].Value);
            Assert.Equal("custom-key", results[4].Name);
            Assert.Equal("custom-value", results[4].Value);
            Assert.Equal(164, decoder.DynamicTableUsedSize);
            Assert.Equal(3, decoder.DynamicTableLength);
        }

        [Fact]
        public void ShouldHandleExampleC5OfTheSpecificationCorrectly()
        {
            var decoder = new Hpack.Decoder(new Hpack.Decoder.Options {
                DynamicTableSize = 256,
                DynamicTableSizeLimit = 256,
            });

            // C.5.1
            var buf = new Buffer();
            buf.AddHexString(
                "4803333032580770726976617465611d" +
                "4d6f6e2c203231204f63742032303133" +
                "2032303a31333a323120474d546e1768" +
                "747470733a2f2f7777772e6578616d70" +
                "6c652e636f6d");

            var results = DecodeAll(decoder, buf);
            Assert.Equal(4, results.Count);
            Assert.Equal(":status", results[0].Name);
            Assert.Equal("302", results[0].Value);
            Assert.Equal("cache-control", results[1].Name);
            Assert.Equal("private", results[1].Value);
            Assert.Equal("date", results[2].Name);
            Assert.Equal("Mon, 21 Oct 2013 20:13:21 GMT", results[2].Value);
            Assert.Equal("location", results[3].Name);
            Assert.Equal("https://www.example.com", results[3].Value);
            Assert.Equal(222, decoder.DynamicTableUsedSize);
            Assert.Equal(4, decoder.DynamicTableLength);

            // C.5.2
            buf = new Buffer();
            buf.AddHexString("4803333037c1c0bf");

            results = DecodeAll(decoder, buf);
            Assert.Equal(4, results.Count);
            Assert.Equal(":status", results[0].Name);
            Assert.Equal("307", results[0].Value);
            Assert.Equal("cache-control", results[1].Name);
            Assert.Equal("private", results[1].Value);
            Assert.Equal("date", results[2].Name);
            Assert.Equal("Mon, 21 Oct 2013 20:13:21 GMT", results[2].Value);
            Assert.Equal("location", results[3].Name);
            Assert.Equal("https://www.example.com", results[3].Value);
            Assert.Equal(222, decoder.DynamicTableUsedSize);
            Assert.Equal(4, decoder.DynamicTableLength);

            // C.5.3
            buf = new Buffer();
            buf.AddHexString(
                "88c1611d4d6f6e2c203231204f637420" +
                "323031332032303a31333a323220474d" +
                "54c05a04677a69707738666f6f3d4153" +
                "444a4b48514b425a584f5157454f5049" +
                "5541585157454f49553b206d61782d61" +
                "67653d333630303b2076657273696f6e" +
                "3d31");

            results = DecodeAll(decoder, buf);
            Assert.Equal(6, results.Count);
            Assert.Equal(":status", results[0].Name);
            Assert.Equal("200", results[0].Value);
            Assert.Equal("cache-control", results[1].Name);
            Assert.Equal("private", results[1].Value);
            Assert.Equal("date", results[2].Name);
            Assert.Equal("Mon, 21 Oct 2013 20:13:22 GMT", results[2].Value);
            Assert.Equal("location", results[3].Name);
            Assert.Equal("https://www.example.com", results[3].Value);
            Assert.Equal("content-encoding", results[4].Name);
            Assert.Equal("gzip", results[4].Value);
            Assert.Equal("set-cookie", results[5].Name);
            Assert.Equal("foo=ASDJKHQKBZXOQWEOPIUAXQWEOIU; max-age=3600; version=1", results[5].Value);
            Assert.Equal(215, decoder.DynamicTableUsedSize);
            Assert.Equal(3, decoder.DynamicTableLength);
        }

        [Fact]
        public void ShouldHandleExampleC6OfTheSpecificationCorrectly()
        {
            var decoder = new Hpack.Decoder(new Hpack.Decoder.Options {
                DynamicTableSize = 256,
                DynamicTableSizeLimit = 256,
            });
            // C.6.1
            var buf = new Buffer();
            buf.AddHexString(
                "488264025885aec3771a4b6196d07abe" +
                "941054d444a8200595040b8166e082a6" +
                "2d1bff6e919d29ad171863c78f0b97c8" +
                "e9ae82ae43d3");

            var results = DecodeAll(decoder, buf);
            Assert.Equal(4, results.Count);
            Assert.Equal(":status", results[0].Name);
            Assert.Equal("302", results[0].Value);
            Assert.Equal("cache-control", results[1].Name);
            Assert.Equal("private", results[1].Value);
            Assert.Equal("date", results[2].Name);
            Assert.Equal("Mon, 21 Oct 2013 20:13:21 GMT", results[2].Value);
            Assert.Equal("location", results[3].Name);
            Assert.Equal("https://www.example.com", results[3].Value);
            Assert.Equal(222, decoder.DynamicTableUsedSize);
            Assert.Equal(4, decoder.DynamicTableLength);

            // C.6.2
            buf = new Buffer();
            buf.AddHexString("4883640effc1c0bf");

            results = DecodeAll(decoder, buf);
            Assert.Equal(4, results.Count);
            Assert.Equal(":status", results[0].Name);
            Assert.Equal("307", results[0].Value);
            Assert.Equal("cache-control", results[1].Name);
            Assert.Equal("private", results[1].Value);
            Assert.Equal("date", results[2].Name);
            Assert.Equal("Mon, 21 Oct 2013 20:13:21 GMT", results[2].Value);
            Assert.Equal("location", results[3].Name);
            Assert.Equal("https://www.example.com", results[3].Value);
            Assert.Equal(222, decoder.DynamicTableUsedSize);
            Assert.Equal(4, decoder.DynamicTableLength);

            // C.6.3
            buf = new Buffer();
            buf.AddHexString(
                "88c16196d07abe941054d444a8200595" +
                "040b8166e084a62d1bffc05a839bd9ab" +
                "77ad94e7821dd7f2e6c7b335dfdfcd5b" +
                "3960d5af27087f3672c1ab270fb5291f" +
                "9587316065c003ed4ee5b1063d5007");

            results = DecodeAll(decoder, buf);
            Assert.Equal(6, results.Count);
            Assert.Equal(":status", results[0].Name);
            Assert.Equal("200", results[0].Value);
            Assert.Equal("cache-control", results[1].Name);
            Assert.Equal("private", results[1].Value);
            Assert.Equal("date", results[2].Name);
            Assert.Equal("Mon, 21 Oct 2013 20:13:22 GMT", results[2].Value);
            Assert.Equal("location", results[3].Name);
            Assert.Equal("https://www.example.com", results[3].Value);
            Assert.Equal("content-encoding", results[4].Name);
            Assert.Equal("gzip", results[4].Value);
            Assert.Equal("set-cookie", results[5].Name);
            Assert.Equal("foo=ASDJKHQKBZXOQWEOPIUAXQWEOIU; max-age=3600; version=1", results[5].Value);
            Assert.Equal(215, decoder.DynamicTableUsedSize);
            Assert.Equal(3, decoder.DynamicTableLength);
        }

        static List<HeaderField> DecodeAll(Hpack.Decoder decoder, Buffer buf)
        {
            var results = new List<HeaderField>();
            var total = buf.View;
            var offset = total.Offset;
            var count = total.Count;
            while (true)
            {
                var segment = new ArraySegment<byte>(total.Array, offset, count);
                var consumed = decoder.Decode(segment);
                offset += consumed;
                count -= consumed;
                if (decoder.Done)
                {
                    results.Add(decoder.HeaderField);
                }
                else break;
            }
            return results;
        }
    }
}
