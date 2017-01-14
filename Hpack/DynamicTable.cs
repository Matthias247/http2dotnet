using System;
using System.Collections.Generic;

namespace Http2.Hpack
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
        public int UsedSize => this._usedSize;

        /// <summary>
        /// Get the current length of the dynamic table
        /// </summary>
        public int Length => this._entries.Count;

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

        /// <summary>
        /// Returns the index of the best matching element in the dynamic table.
        /// The index will be 0-based, means it is relative to the start of the
        /// dynamic table.
        /// If no index was found the return value is -1.
        /// If an index was found and the name as well as the value match
        /// isFullMatch will be set to true.
        /// </summary>
        public int GetBestMatchingIndex(HeaderField field, out bool isFullMatch)
        {
            var bestMatch = -1;
            isFullMatch = false;

            var i = 0;
            foreach (var entry in _entries)
            {
                if (entry.Name == field.Name)
                {
                    if (bestMatch == -1)
                    {
                        // We used the lowest matching field index, which makes
                        // search for the receiver the most efficient and provides
                        // the highest chance to use the Static Table.
                        bestMatch = i;
                    }

                    if (entry.Value == field.Value)
                    {
                        // It's a perfect match!
                        isFullMatch = true;
                        return i;
                    }
                }
                i++;
            }

            return bestMatch;
        }
    }
}
