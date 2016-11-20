using System;
using System.Collections.Generic;

namespace Hpack
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
        public int UsedDynamicTableSize
        {
            get { return this.dynamic.UsedSize; }
        }

        /// <summary>Gets the current length of the dynamic table</summary>
        public int DynamicTableLength
        {
            get { return this.dynamic.Length; }
        }

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
    }
}