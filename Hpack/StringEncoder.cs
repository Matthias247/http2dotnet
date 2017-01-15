using System;
using System.Text;

namespace Http2.Hpack
{
    /// <summary>
    /// Possible strategies for applying huffman encoding
    /// </summary>
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
        /// Returns the byte length of the given string in non-huffman encoded
        /// form.
        /// </summary>
        public static int GetByteLength(string value)
        {
            return Encoding.ASCII.GetByteCount(value);
        }

        /// <summary>
        /// Encodes the given string into the target buffer
        /// </summary>
        /// <param name="buf">The buffer into which the string should be serialized</param>
        /// <param name="value">The value to encode</param>
        /// <param name="valueByteLen">
        /// The length of the string in bytes in non-huffman-encoded form.
        /// This can be retrieved through the GetByteLength method.
        /// </param>
        /// <param name="huffman">Controls the huffman encoding</param>
        /// <returns>
        /// The number of bytes that were required to encode the value.
        /// -1 if the value did not fit into the buffer.
        /// </returns>
        public static int EncodeInto(
            ArraySegment<byte> buf,
            string value, int valueByteLen, HuffmanStrategy huffman)
        {
            var offset = buf.Offset;
            var free = buf.Count;
            // Fast check for free space. Doesn't need to be exact
            if (free < 1 + valueByteLen) return -1;

            var encodedByteLen = valueByteLen;
            var requiredHuffmanBytes = 0;
            var useHuffman = huffman == HuffmanStrategy.Always;
            byte[] huffmanInputBuf = null;

            // Check if the string should be reencoded with huffman encoding
            if (huffman == HuffmanStrategy.Always || huffman == HuffmanStrategy.IfSmaller)
            {
                huffmanInputBuf = Encoding.ASCII.GetBytes(value);
                requiredHuffmanBytes = Huffman.EncodedLength(
                    new ArraySegment<byte>(huffmanInputBuf));
                if (huffman == HuffmanStrategy.IfSmaller && requiredHuffmanBytes < encodedByteLen)
                {
                    useHuffman = true;
                }
            }

            if (useHuffman)
            {
                encodedByteLen = requiredHuffmanBytes;
            }

            // Write the required length to the target buffer
            var prefixContent = useHuffman ? (byte)0x80 : (byte)0;
            var used = IntEncoder.EncodeInto(
                new ArraySegment<byte>(buf.Array, offset, free),
                encodedByteLen, prefixContent, 7);
            if (used == -1) return -1; // Couldn't write length
            offset += used;
            free -= used;

            if (useHuffman)
            {
                if (free < requiredHuffmanBytes) return -1;
                // Use the huffman encoder to write bytes to target buffer
                used = Huffman.EncodeInto(
                    new ArraySegment<byte>(buf.Array, offset, free),
                    new ArraySegment<byte>(huffmanInputBuf));
                if (used == -1) return -1; // Couldn't write length
                offset += used;
            }
            else
            {
                if (free < valueByteLen) return -1;
                // Use ASCII encoder to write bytes to target buffer
                used = Encoding.ASCII.GetBytes(
                    value, 0, value.Length, buf.Array, offset);
                offset += used;
            }

            // Return the number amount of used bytes
            return offset - buf.Offset;
        }

        /// <summary>
        /// Encodes the given string.
        /// This method should not be directly used since it allocates.
        /// EncodeInto is preferred.
        /// </summary>
        /// <param name="value">The value to encode</param>
        /// <param name="huffman">Controls the huffman encoding</param>
        public static byte[] Encode(string value, HuffmanStrategy huffman)
        {
            // Estimate the size of the buffer
            var asciiSize = Encoding.ASCII.GetByteCount(value);
            var estimatedHeaderLength = IntEncoder.RequiredBytes(asciiSize, 0, 7);
            var estimatedBufferSize = estimatedHeaderLength + asciiSize;

            while (true)
            {
                // Create a buffer with some headroom
                var buf = new byte[estimatedBufferSize + 16];
                // Try to serialize value in there
                var size = EncodeInto(
                    new ArraySegment<byte>(buf), value, asciiSize, huffman);
                if (size != -1)
                {
                    // Serialization was performed
                    // Trim the buffer in order to return it
                    if (size == buf.Length) return buf;
                    var newBuf = new byte[size];
                    Array.Copy(buf, 0, newBuf, 0, size);
                    return newBuf;
                }
                else
                {
                    // Need more buffer space
                    estimatedBufferSize = (estimatedBufferSize + 2) * 2;
                }
            }
        }
    }
}
