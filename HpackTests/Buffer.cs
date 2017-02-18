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

        public void WriteBytes(byte[] bytes)
        {
            foreach (var b in bytes) WriteByte(b);
        }

        public void WriteString(string s)
        {
            var bytes = Encoding.ASCII.GetBytes(s);
            WriteBytes(bytes);
        }

        public void AddHexString(string s)
        {
            var byteBuf = new byte[2];
            for (int i = 0; i < s.Length; i += 2)
            {
                WriteByte(Convert.ToByte(s.Substring(i, 2), 16));
            }
        }

        public void RemoveFront(int amount)
        {
            for (var i = 0; i < amount; i++)
            {
                bytes.RemoveAt(0);
            }
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
