using System;
using System.Collections.Generic;

using Xunit;

using Hpack;

namespace HpackTests
{
    public class DynamicTableTests
    {
        [Fact]
        public void ShouldStartWith0Items()
        {
            var t = new DynamicTable(4096);
            Assert.Equal(0, t.Length);
            Assert.Equal(0, t.UsedSize);
        }

        [Fact]
        public void ShouldStartWithTheSetMaximumSize()
        {
            var t = new DynamicTable(4096);
            Assert.Equal(4096, t.MaxTableSize);
            t.MaxTableSize = 0;
            Assert.Equal(0, t.MaxTableSize);
        }

        [Fact]
        public void ShouldIncreaseUsedSizeAndLengthIfItemIsInserted()
        {
            var t = new DynamicTable(4096);

            t.Insert("a", 1, "b", 1);
            var expectedSize = 1+1+32;
            Assert.Equal(expectedSize, t.UsedSize);
            Assert.Equal(1, t.Length);

            t.Insert("xyz", 3, "abcd", 4);
            expectedSize += 3+4+32;
            Assert.Equal(expectedSize, t.UsedSize);
            Assert.Equal(2, t.Length);

            // Fill the table up
            t.Insert("", 0, "a", 4096-expectedSize-32);
            expectedSize = 4096;
            Assert.Equal(expectedSize, t.UsedSize);
            Assert.Equal(3, t.Length);
        }

        [Fact]
        public void ShouldReturnTrueIfAnItemCouldBeInserted()
        {
            var t = new DynamicTable(4096);

            var res = t.Insert("a", 1, "b", 1);
            Assert.True(res);

            res = t.Insert("a", 1, "b", 1);
            Assert.True(res);

            res = t.Insert("a", 1, "b", 4096 - 2*(1+1+32) - 32 - 1);
            Assert.True(res);
        }

        [Fact]
        public void ShouldReturnFalseIfAnItemCanNotBeInserted()
        {
            var t = new DynamicTable(4096);

            var res = t.Insert("a", 5000, "b", 0);
            Assert.False(res);
            Assert.Equal(0, t.Length);
            Assert.Equal(t.UsedSize, 0);

            res = t.Insert("a", 0, "b", 5000);
            Assert.False(res);
            Assert.Equal(0, t.Length);
            Assert.Equal(t.UsedSize, 0);

            res = t.Insert("a", 3000, "b", 3000);
            Assert.False(res);
            Assert.Equal(t.UsedSize, 0);
            Assert.Equal(0, t.Length);
        }

        [Fact]
        public void ShouldDrainTheTableIfAnElementCanNotBeInserted()
        {
            var t = new DynamicTable(4096);

            var res = t.Insert("a", 2000, "b", 2000);
            Assert.True(res);
            Assert.Equal(1, t.Length);
            Assert.Equal(4032, t.UsedSize);

            res = t.Insert("a", 0, "b", 32);
            Assert.True(res);
            Assert.Equal(4096, t.UsedSize);
            Assert.Equal(2, t.Length);

            res = t.Insert("a", 3000, "b", 3000);
            Assert.False(res);
            Assert.Equal(t.UsedSize, 0);
            Assert.Equal(0, t.Length);
        }

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
        public void ShouldReturnTheCorrectElementWhenIndexed()
        {
            var t = new DynamicTable(4096);
            t.Insert("a", 1, "b", 1);
            t.Insert("c", 1, "d", 1);
            t.Insert("e", 1, "f", 1);
            t.Insert("ab", 2, "cd", 2);
            Assert.Equal(new TableEntry { Name = "ab", NameLen = 2, Value = "cd", ValueLen = 2}, t.GetAt(0), ec);
            Assert.Equal(new TableEntry { Name = "e", NameLen = 1, Value = "f", ValueLen = 1}, t.GetAt(1), ec);
            Assert.Equal(new TableEntry { Name = "c", NameLen = 1, Value = "d", ValueLen = 1}, t.GetAt(2), ec);
            Assert.Equal(new TableEntry { Name = "a", NameLen = 1, Value = "b", ValueLen = 1}, t.GetAt(3), ec);

            t.Insert("xyz", 3, "fgh", 99);
            Assert.Equal(new TableEntry { Name = "xyz", NameLen = 3, Value = "fgh", ValueLen = 99}, t.GetAt(0), ec);
        }

        [Fact]
        public void ShouldThrowAnErrorWhenInvalidlyIndexed()
        {
            var t = new DynamicTable(4096);
            Assert.Throws<IndexOutOfRangeException>(() => t.GetAt(-1));
            Assert.Throws<IndexOutOfRangeException>(() => t.GetAt(0));
            Assert.Throws<IndexOutOfRangeException>(() => t.GetAt(1));

            t.Insert("a", 0, "b", 0);
            Assert.Throws<IndexOutOfRangeException>(() => t.GetAt(-1));
            t.GetAt(0); // should not throw
            Assert.Throws<IndexOutOfRangeException>(() => t.GetAt(1));
        }

        static bool InsertItemOfSize(DynamicTable table, string keyName, int size)
        {
            size -= 32;
            var aSize = size/2;
            var bSize = size - aSize;
            return table.Insert(keyName, aSize, "", bSize);
        }

        [Fact]
        public void ShouldEvictItemsWhenMaxSizeIsLowered()
        {
            var t = new DynamicTable(4096);
            InsertItemOfSize(t, "a", 1999);
            InsertItemOfSize(t, "b", 2001);
            InsertItemOfSize(t, "c", 64);
            Assert.Equal(3, t.Length);
            Assert.Equal(4064, t.UsedSize);

            t.MaxTableSize = 3000;
            Assert.Equal(3000, t.MaxTableSize);
            Assert.Equal(2, t.Length);
            Assert.Equal(2065, t.UsedSize);
            Assert.Equal("c", t.GetAt(0).Name);
            Assert.Equal("b", t.GetAt(1).Name);

            t.MaxTableSize = 1000;
            Assert.Equal(1000, t.MaxTableSize);
            Assert.Equal(1, t.Length);
            Assert.Equal(64, t.UsedSize);
            Assert.Equal("c", t.GetAt(0).Name);

            t.MaxTableSize = 64;
            Assert.Equal(64, t.MaxTableSize);
            Assert.Equal(1, t.Length);
            Assert.Equal(64, t.UsedSize);

            t.MaxTableSize = 63;
            Assert.Equal(63, t.MaxTableSize);
            Assert.Equal(0, t.Length);
            Assert.Equal(0, t.UsedSize);

            t.MaxTableSize = 4096;
            Assert.Equal(4096, t.MaxTableSize);
            InsertItemOfSize(t, "aa", 1000);
            InsertItemOfSize(t, "bb", 1000);
            Assert.Equal(2, t.Length);
            Assert.Equal(2000, t.UsedSize);

            t.MaxTableSize = 999;
            Assert.Equal(999, t.MaxTableSize);
            Assert.Equal(0, t.Length);
            Assert.Equal(0, t.UsedSize);
        }

        [Fact]
        public void ShouldEvictTheOldestItemsIfNewItemsGetInserted()
        {
            var t = new DynamicTable(4000);
            InsertItemOfSize(t, "a", 2000);
            InsertItemOfSize(t, "b", 2000);
            InsertItemOfSize(t, "c", 2000);
            Assert.Equal(2, t.Length);
            Assert.Equal(4000, t.UsedSize);
            Assert.Equal("c", t.GetAt(0).Name);
            Assert.Equal("b", t.GetAt(1).Name);
            
            InsertItemOfSize(t, "d", 3000);
            Assert.Equal(1, t.Length);
            Assert.Equal(3000, t.UsedSize);
            Assert.Equal("d", t.GetAt(0).Name);
            
            InsertItemOfSize(t, "e", 100);
            InsertItemOfSize(t, "f", 100);
            InsertItemOfSize(t, "g", 100);
            InsertItemOfSize(t, "h", 701);
            Assert.Equal(4, t.Length);
            Assert.Equal(1001, t.UsedSize);
            Assert.Equal("h", t.GetAt(0).Name);
            Assert.Equal("e", t.GetAt(3).Name);
        }
    }
}
