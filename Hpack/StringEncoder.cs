using System;
using System.Text;

namespace Hpack
{
    public enum HuffmanStrategy
    {
        /// <summary>Never use Huffman encoding</summary>
        Never,
        /// <summary>Always use Huffman encoding</summary>
        Always,
        /// <summary>Use Huffman if the string is shorter in huffman format</summary>
        IfSmaller,
    }
    /// <summary>
    /// Encodes string values according to the HPACK specification.
    /// </summary>
    public static class StringEncoder
    {
        /// <summary>
        /// Encodes the given string
        /// </summary>
        /// <param name="value">The value to encode</param>
        /// <param name="huffman">Controls the huffman encoding</param>
        public static byte[] Encode(string value, HuffmanStrategy huffman)
        {
            // Convert the string into a buffer
            // This could maybe be fastened up by dropping the intermediate Buffer
            var bytes = Encoding.ASCII.GetBytes(value); // TODO: Optimize GC
            var byteView = new ArraySegment<byte>(bytes);

            var requiredHuffmanBytes = 0;
            var useHuffman = huffman == HuffmanStrategy.Always;
            if (huffman == HuffmanStrategy.Always || huffman == HuffmanStrategy.IfSmaller)
            {
                requiredHuffmanBytes = Huffman.EncodedLength(byteView);
                if (huffman == HuffmanStrategy.IfSmaller && requiredHuffmanBytes < bytes.Length)
                {
                    useHuffman = true;
                }
            }

            if (useHuffman)
            {
                var outView = new ArraySegment<byte>(new byte[requiredHuffmanBytes]);
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
