using System;

namespace Http2.Hpack
{
    /// <summary>
    /// Encodes integer values according to the HPACK specification.
    /// </summary>
    public static class IntEncoder
    {
        /// <summary>
        /// Encodes the given number
        /// </summary>
        /// <param name="value">The value to encode</param>
        /// <param name="beforePrefix">The value that is stored in the same byte as the prefix</param>
        /// <param name="prefixBits">The number of bits that shall be used for the prefix</param>
        public static byte[] Encode(int value, byte beforePrefix, int prefixBits)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            //uint val = (uint)value;

            var buf = new byte[8]; // Will never need more than this for 32bit numbers
            var offset = 0;
            // TODO: Buffer pooling

            // Calculate the maximum value that fits into the prefix
            int maxPrefixVal = ((1 << prefixBits) - 1); // equals 2^N - 1
            if (value < maxPrefixVal) {
                // Value fits into the prefix
                buf[offset] = (byte)((beforePrefix | value) & 0xFF);
                offset++;
            } else {
                // Value does not fit into prefix
                // Save the max prefix value
                buf[offset] = (byte)((beforePrefix | maxPrefixVal) & 0xFF);
                offset++;
                value -= maxPrefixVal;
                while (value >= 128)
                {
                    var part = (value % 128 + 128);
                    buf[offset] = (byte)(part & 0xFF);
                    offset++;
                    value = value / 128; // Shift is not valid above 32bit
                }
                buf[offset] = (byte)(value & 0xFF);
                offset++;
            }

            // We need only offset bytes from the total result
            if (offset != buf.Length)
            {
                var newBuf = new byte[offset];
                Array.Copy(buf, newBuf, offset);
                buf = newBuf;
            }

            return buf;
        }
    }
}
