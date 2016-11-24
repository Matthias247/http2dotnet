using System;
using System.Text;

namespace Hpack
{
    /// <summary>
    /// Encodes string values according to the HPACK specification.
    /// </summary>
    public static class StringEncoder
    {
        /// <summary>
        /// Encodes the given string
        /// </summary>
        /// <param name="value">The value to encode</param>
        public static byte[] Encode(string value, bool useHuffman)
        {
            // Convert the string into a buffer
            // This could maybe be fastened up by dropping the intermediate Buffer
            var bytes = Encoding.ASCII.GetBytes(value);// Encoding.ASCII.GetBytes(value); // TODO: Optimize GC
            var byteView = new ArraySegment<byte>(bytes);

            if (useHuffman)
            {
                var required = Huffman.EncodedLength(byteView);
                var outView = new ArraySegment<byte>(new byte[required]);
                byteView = Huffman.Encode(byteView, outView);
            }

            // Write the length of the bytes
            var prefixContent = useHuffman ? (byte)0x80 : (byte)0;
            var lenBytes = IntEncoder.Encode(byteView.Count, prefixContent, 7);
            var total = new byte[lenBytes.Length + byteView.Count];
            // Copy length
            Array.Copy(lenBytes, total, lenBytes.Length);
            // Copy string
            Array.Copy(byteView.Array, 0, total, lenBytes.Length, byteView.Count);

            return total;
        }
    }
}
