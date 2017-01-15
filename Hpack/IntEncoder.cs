using System;

namespace Http2.Hpack
{
    /// <summary>
    /// Encodes integer values according to the HPACK specification.
    /// </summary>
    public static class IntEncoder
    {
        /// <summary>
        /// Returns the number of bytes that is required for encoding
        /// the given value.
        /// </summary>
        /// <param name="value">The value to encode</param>
        /// <param name="beforePrefix">The value that is stored in the same byte as the prefix</param>
        /// <param name="prefixBits">The number of bits that shall be used for the prefix</param>
        /// <returns>The number of required bytes</returns>
        public static int RequiredBytes(int value, byte beforePrefix, int prefixBits)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));

            var offset = 0;

            // Calculate the maximum value that fits into the prefix
            int maxPrefixVal = ((1 << prefixBits) - 1); // equals 2^N - 1
            if (value < maxPrefixVal) {
                // Value fits into the prefix
                offset++;
            } else {
                offset++;
                value -= maxPrefixVal;
                while (value >= 128)
                {
                    offset++;
                    value = value / 128; // Shift is not valid above 32bit
                }
                offset++;
            }

            return offset;
        }

        /// <summary>
        /// Encodes the given number into the target buffer
        /// </summary>
        /// <param name="buf">The target buffer for encoding the value</param>
        /// <param name="value">The value to encode</param>
        /// <param name="beforePrefix">The value that is stored in the same byte as the prefix</param>
        /// <param name="prefixBits">The number of bits that shall be used for the prefix</param>
        /// <returns>
        /// The number of bytes that were required to encode the value.
        /// -1 if the value did not fit into the buffer.
        /// </returns>
        public static int EncodeInto(
            ArraySegment<byte> buf, int value, byte beforePrefix, int prefixBits)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));

            var offset = buf.Offset;
            int free = buf.Count;
            if (free < 1) return -1;

            // Calculate the maximum value that fits into the prefix
            int maxPrefixVal = ((1 << prefixBits) - 1); // equals 2^N - 1
            if (value < maxPrefixVal) {
                // Value fits into the prefix
                buf.Array[offset] = (byte)((beforePrefix | value) & 0xFF);
                offset++;
            } else {
                // Value does not fit into prefix
                // Save the max prefix value
                buf.Array[offset] = (byte)((beforePrefix | maxPrefixVal) & 0xFF);
                offset++;
                free--;
                if (free < 1) return -1;
                value -= maxPrefixVal;
                while (value >= 128)
                {
                    var part = (value % 128 + 128);
                    buf.Array[offset] = (byte)(part & 0xFF);
                    offset++;
                    free--;
                    if (free < 1) return -1;
                    value = value / 128; // Shift is not valid above 32bit
                }
                buf.Array[offset] = (byte)(value & 0xFF);
                offset++;
            }

            return offset - buf.Offset;
        }

        /// <summary>
        /// Encodes the given number.
        /// </summary>
        /// <param name="value">The value to encode</param>
        /// <param name="beforePrefix">The value that is stored in the same byte as the prefix</param>
        /// <param name="prefixBits">The number of bits that shall be used for the prefix</param>
        /// <returns>The encoded number</returns>
        public static byte[] Encode(int value, byte beforePrefix, int prefixBits)
        {
            var bytes = new byte[RequiredBytes(value, beforePrefix, prefixBits)];
            EncodeInto(new ArraySegment<byte>(bytes), value, beforePrefix, prefixBits);
            return bytes;
        }
    }
}
