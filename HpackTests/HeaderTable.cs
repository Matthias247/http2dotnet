using System;
using System.Collections.Generic;

using Xunit;

using Http2.Hpack;

namespace HpackTests
{
    public class HeaderTableTests
    {
        class TableEntryComparer : IEqualityComparer<TableEntry>
        {
            public bool Equals(TableEntry x, TableEntry y)
            {
                return x.Name == y.Name && x.Value == y.Value
                    && x.NameLen == y.NameLen && x.ValueLen == y.ValueLen;
            }

            public int GetHashCode(TableEntry obj)
            {
                return obj.GetHashCode();
            }
        }

        TableEntryComparer ec = new TableEntryComparer();

        [Fact]
        public void ShouldBeAbleToSetDynamicTableSize()
        {
            var t = new HeaderTable(2001);
            Assert.Equal(2001, t.MaxDynamicTableSize);
            t.MaxDynamicTableSize = 300;
            Assert.Equal(300, t.MaxDynamicTableSize);
        }

        [Fact]
        public void ShouldReturnItemsFromStaticTable()
        {
            var t = new HeaderTable(400);
            var item = t.GetAt(1);
            Assert.Equal(
                new TableEntry { Name = ":authority", NameLen = 10, Value = "", ValueLen = 0},
                item, ec);
            item = t.GetAt(2);
            Assert.Equal(
                new TableEntry { Name = ":method", NameLen = 7, Value = "GET", ValueLen = 3},
                item, ec);
            item = t.GetAt(61);
            Assert.Equal(
                new TableEntry { Name = "www-authenticate", NameLen = 16, Value = "", ValueLen = 0},
                item, ec);
        }

        [Fact]
        public void ShouldReturnItemsFromDynamicTable()
        {
            var t = new HeaderTable(400);
            t.Insert("a", 1, "b", 2);
            t.Insert("c", 3, "d", 4);
            var item = t.GetAt(62);
            Assert.Equal(
                new TableEntry { Name = "c", NameLen = 3, Value = "d", ValueLen = 4},
                item, ec);
            item = t.GetAt(63);
            Assert.Equal(
                new TableEntry { Name = "a", NameLen = 1, Value = "b", ValueLen = 2},
                item, ec);
        }

        [Fact]
        public void ShouldThrowWhenIncorrectlyIndexed()
        {
            var t = new HeaderTable(400);
            Assert.Throws<IndexOutOfRangeException>(() => t.GetAt(-1));
            Assert.Throws<IndexOutOfRangeException>(() => t.GetAt(0));
            t.GetAt(1); // Valid
            t.GetAt(61); // Last valid
            Assert.Throws<IndexOutOfRangeException>(() => t.GetAt(62));

            // Put something into the dynamic table and test again
            t.Insert("a", 1, "b", 1);
            t.Insert("a", 1, "b", 1);
            t.GetAt(62);
            t.GetAt(63);
            Assert.Throws<IndexOutOfRangeException>(() => t.GetAt(64));
        }
    }
}
