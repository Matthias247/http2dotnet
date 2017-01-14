using System;

namespace Http2.Hpack
{
    /// <summary>
    /// The combination of the static and a dynamic header table
    /// </summary>
    public class HeaderTable
    {
        DynamicTable dynamic;

        public HeaderTable(int dynamicTableSize)
        {
            this.dynamic = new DynamicTable(dynamicTableSize);
        }

        public int MaxDynamicTableSize
        {
            get { return this.dynamic.MaxTableSize; }
            set { this.dynamic.MaxTableSize = value; }
        }

        /// <summary>Gets the occupied size in bytes for the dynamic table</summary>
        public int UsedDynamicTableSize => this.dynamic.UsedSize;

        /// <summary>Gets the current length of the dynamic table</summary>
        public int DynamicTableLength => this.dynamic.Length;

        /// <summary>
        /// Inserts a new element into the header table
        /// </summary>
        public bool Insert(string name, int nameBytes, string value, int valueBytes)
        {
            return dynamic.Insert(name, nameBytes, value, valueBytes);
        }

        public TableEntry GetAt(int index)
        {
            // 0 is not a valid index
            if (index < 1) throw new IndexOutOfRangeException();

            // Relate index to start of static table
            // and look if element is in there
            index--;
            if (index < StaticTable.Entries.Length)
            {
                // Index is in the static table
                return StaticTable.Entries[index];
            }

            // Relate index to start of dynamic table
            // and look if element is in there
            index -= StaticTable.Entries.Length;
            if (index < this.dynamic.Length)
            {
                return this.dynamic.GetAt(index);
            }

            // Element is not in static or dynamic table
            throw new IndexOutOfRangeException();
        }

        /// <summary>
        /// Returns the index of the best matching element in the header table.
        /// If no index was found the return value is -1.
        /// If an index was found and the name as well as the value match
        /// isFullMatch will be set to true.
        /// </summary>
        public int GetBestMatchingIndex(HeaderField field, out bool isFullMatch)
        {
            var bestMatch = -1;
            isFullMatch = false;

            var i = 1;
            foreach (var entry in StaticTable.Entries)
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

            // If we don't have a full match search on in the dynamic table
            bool dynamicHasFullMatch;
            var di = this.dynamic.GetBestMatchingIndex(field, out dynamicHasFullMatch);
            if (dynamicHasFullMatch)
            {
                isFullMatch = true;
                return di + 1 + StaticTable.Length;
            }
            // If the dynamic table has a match at all use it's index and normalize it
            if (di != -1 && bestMatch == -1) bestMatch = di + 1 + StaticTable.Length;

            return bestMatch;
        }
    }
}