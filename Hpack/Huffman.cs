using System;
using System.Text;
using System.Buffers;

namespace Http2.Hpack
{
    public class Huffman
    {
        /// <summary>
        /// Resizes a buffer by allocating a new one and copying usedBytes from
        /// the old to the new buffer.
        /// </summary>
        private static void ResizeBuffer(
            ref byte[] outBuf, int usedBytes, int newBytes,
            ArrayPool<byte> pool)
        {
            var newSize = outBuf.Length + newBytes;
            var newBuf = pool.Rent(newSize);
            Array.Copy(outBuf, 0, newBuf, 0, usedBytes);
            pool.Return(outBuf);
            outBuf = newBuf;
        }

        public static string Decode(ArraySegment<byte> input, ArrayPool<byte> pool)
        {
            // TODO: Check here if buffer is correctly returned to pool in error cases
            var byteCount = 0;
            // Estimate a buffer length - might need more depending on coding factor
            var estLength = (input.Count * 3 + 1) / 2;

            var outBuf = pool.Rent(estLength);

            // Offsets for decoding
            var inputByteOffset = 0;
            var inputBitOffset = 0;

            var currentSymbolLength = 0;
            // Padding is only valid in case all bits are 1's
            var isValidPadding = true;

            var treeNode = HuffmanTree.Root;

            for (inputByteOffset = 0; inputByteOffset < input.Count; inputByteOffset++)
            {
                var bt = input.Array[inputByteOffset];
                for (inputBitOffset = 7; inputBitOffset >= 0; inputBitOffset--) {
                    // Fetch bit at offset position
                    var bit = (bt & (1 << inputBitOffset)) >> inputBitOffset;
                    // Follow the tree branch that is specified by that bit
                    if (bit != 0)
                    {
                        treeNode = treeNode.Child1;
                    }
                    else
                    {
                        // Encountered a 0 bit
                        treeNode = treeNode.Child0;
                        isValidPadding = false;
                    }
                    currentSymbolLength++;

                    if (treeNode == null)
                    {
                        pool.Return(outBuf);
                        throw new Exception("Invalid huffman code");
                    }

                    if (treeNode.Value != -1)
                    {
                        if (treeNode.Value != 256)
                        {
                            // We are at the leaf and got a value
                            if (outBuf.Length - byteCount == 0)
                            {
                                // No more space - resize first
                                var unprocessedBytes = input.Count - inputByteOffset;
                                ResizeBuffer(
                                    ref outBuf, byteCount, 2*unprocessedBytes,
                                    pool);
                            }
                            outBuf[byteCount] = (byte)treeNode.Value;
                            byteCount++;
                            treeNode = HuffmanTree.Root;
                            currentSymbolLength = 0;
                            isValidPadding = true;
                        }
                        else
                        {
                            // EOS symbol
                            // Fully receiving this is a decoding error,
                            // because padding must not be longer than 7 bits
                            pool.Return(outBuf);
                            throw new Exception("Encountered EOS in huffman code");
                        }
                    }
                    
                }
            }

            if (currentSymbolLength > 7)
            {
                // A padding strictly longer
                // than 7 bits MUST be treated as a decoding error.
                throw new Exception("Padding exceeds 7 bits");
            }

            if (!isValidPadding)
            {
                throw new Exception("Invalid padding");
            }

            // Convert the buffer into a string
            // TODO: Check if encoding is really correct
            var str = Encoding.ASCII.GetString(outBuf, 0, byteCount);
            pool.Return(outBuf);
            return str;
        }

        /// <summary>
        /// Returns the length of a string in huffman encoded form
        /// </summary>
        public static int EncodedLength(ArraySegment<byte> data)
        {
            var byteCount = 0;
            var bitCount = 0;

            for (var i = data.Offset; i < data.Offset + data.Count; i++)
            {
                var tableEntry = HuffmanTable.Entries[data.Array[i]];
                bitCount += tableEntry.Len;
                if (bitCount >= 8)
                {
                    byteCount += bitCount / 8;
                    bitCount = bitCount % 8;
                }
            }

            if (bitCount != 0)
            {
                byteCount++;
            }

            return byteCount;
        }

        /// <summary>
        /// Performs huffman encoding on the input buffer.
        /// Output must be big enough to hold the huffman encoded string at
        /// the specified offset.
        /// The required size can be calculated with Huffman.EncodedLength
        /// </summary>
        /// <returns>
        /// The number of bytes that are used in the buffer for the encoded
        /// string. Will return -1 if there was not enough free space for encoding
        /// </returns>
        public static int EncodeInto(
            ArraySegment<byte> buf,
            ArraySegment<byte> bytes)
        {
            if (bytes.Count == 0) return 0;

            var bitOffset = 0;
            var byteOffset = buf.Offset;
            /// The value at the current offset in the output buffer
            var currentValue = 0;

            for (var i = bytes.Offset; i  < bytes.Offset + bytes.Count; i++)
            {
                // Lookup the corresponding value in the table
                var tableEntry = HuffmanTable.Entries[bytes.Array[i]];
                var binVal = tableEntry.Bin;
                var bits = tableEntry.Len;

                while (bits > 0)
                {
                    // Put a number of bits into output buffer
                    // Calculate the number of bits first
                    var bitsToPut = 8 - bitOffset;
                    if (bits < bitsToPut) bitsToPut = bits;

                    // Take the bitsToPut highest bits from binVal
                    var putBytes = binVal >> (bits - bitsToPut);
                    // Align the value on 8 bits
                    putBytes = putBytes << (8-bitsToPut);
                    // And put it in place at the position where we need it
                    // This can not be directly combined with the former operation,
                    // because otherwise the trailing numbers wouldn't be zeroed
                    currentValue = currentValue | (putBytes >> bitOffset);
                    bitOffset += bitsToPut;
                    if (bitOffset == 8)
                    {
                        // A full output byte was produced
                        // Write it to target buffer and advance to the next byte
                        buf.Array[byteOffset] = (byte)(currentValue & 0xFF);
                        byteOffset++;
                        bitOffset = 0;
                        currentValue = 0;
                    }
                    bits -= bitsToPut;
                    // Need only the low amount of bits
                    binVal &= ((1<<bits)-1);
                }
            }

            // Complete the last byte with 1s if necessary (EOS symbol)
            // and save currentValue, since this hasn't happened yet
            if (bitOffset != 0)
            {
                var eosBits = (1 << (8-bitOffset)) - 1;
                currentValue |= eosBits;
                buf.Array[byteOffset] = (byte)(currentValue & 0xFF);
                byteOffset++;
            }

            return byteOffset - buf.Offset;
        }
    }
}
