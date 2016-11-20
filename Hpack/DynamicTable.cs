using System;
using System.Collections.Generic;

namespace Hpack
{
    /// <summary>
    /// A dynamic header table
    /// </summary>
    public class DynamicTable
    {
        private List<TableEntry> _entries = new List<TableEntry>();

        /// <summary> The maximum size of the dynamic table</summary>
        private int _maxTableSize;

        /// <summary> The current used amount of bytes for the table</summary>
        private int _usedSize = 0;

        /// <summary> Get the current maximum size of the dynamic table</summary>
        public int MaxTableSize
        {
            get { return this._maxTableSize; }
            /// <summary>
            /// Sets a new maximum size of the dynamic table.
            /// The content will be evicted to fit into the new size.
            /// </summary>
            set
            {
                if (value >= this._maxTableSize)
                {
                    this._maxTableSize = value;
                    return;
                }

                // Table is shrinked, which means entries must be evicted
                this._maxTableSize = value;
                this.EvictTo(value);
            }
        }

        /// <summary>The size that is currently occupied by the table</summary>
        public int UsedSize
        {
            get { return this._usedSize; }
        }

        /// <summary>
        /// Get the current length of the dynamic table
        /// </summary>
        public int Length
        {
            get { return this._entries.Count; }
        }

        public TableEntry GetAt(int index)
        {
            if (index < 0 || index >= this._entries.Count)
                throw new IndexOutOfRangeException();
            var elem = this._entries[index];
            return elem;
        }

        public DynamicTable(int maxTableSize)
        {
            this._maxTableSize = maxTableSize;
        }

        private void EvictTo(int newSize)
        {
            if (newSize < 0) newSize = 0;
            // Delete as many entries as needed to conform to the new size
            // Start by counting how many entries need to be deleted
            var delCount = 0;
            var used = this._usedSize;
            var index = this._entries.Count - 1; // Start at end of the table
            while (used > newSize && index >= 0)
            {
                var item = this._entries[index];
                used -= (32 + item.NameLen + item.ValueLen);
                index--;
                delCount++;
            }

            if (delCount == 0) return;
            else if (delCount == this._entries.Count)
            {
                this._entries.Clear();
                this._usedSize = 0;
            }
            else
            {
                this._entries.RemoveRange(this._entries.Count - delCount, delCount);
                this._usedSize = used;
            }
        }

        /// <summary>
        /// Inserts a new element into the dynamic header table
        /// </summary>
        public bool Insert(string name, int nameBytes, string value, int valueBytes)
        {
            // Calculate the size that this dynamic table entry occupies according to the spec
            var entrySize = 32 + nameBytes + valueBytes;

            // Evict the dynamic table to have enough space for new entry - or to 0
            var maxUsedSize = this._maxTableSize - entrySize;
            if (maxUsedSize < 0) maxUsedSize = 0;
            this.EvictTo(maxUsedSize);

            // Return if entry doesn't fit into table
            if (entrySize > this._maxTableSize) return false;

            // Add the new entry at the beginning of the table
            var entry = new TableEntry
            {
                Name = name,
                NameLen = nameBytes,
                Value = value,
                ValueLen = valueBytes,
            };
            this._entries.Insert(0, entry);
            this._usedSize += entrySize;
            return true;
        }
    }
}
