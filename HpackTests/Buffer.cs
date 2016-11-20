using System;
using System.Collections.Generic;
using System.Text;

namespace HpackTests
{
    public class Buffer
    {
        List<byte> bytes;

        public Buffer()
        {
            bytes = new List<byte>();
        }

        public void WriteByte(byte b)
        {
            bytes.Add(b);
        }

        public void WriteByte(char b)
        {
            bytes.Add((byte)b);
        }

        public void WriteString(string s)
        {
            var bytes = Encoding.ASCII.GetBytes(s);
            foreach (var b in bytes) WriteByte(b);
        }

        public int Length
        {
            get { return bytes.Count; }
        }

        public byte[] Bytes
        {
            get { return bytes.ToArray(); }
        }

        public ArraySegment<byte> View
        {
            get
            {
                var b = bytes.ToArray();
                return new ArraySegment<byte>(b, 0, b.Length);
            }
        }
    }
}
