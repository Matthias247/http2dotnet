using System;
using System.Text;
using System.Buffers;

namespace Http2.Hpack
{
    public class Huffman
    {
        private static ArrayPool<byte> _pool = ArrayPool<byte>.Shared;

        public static string Decode(ArraySegment<byte> input)
        {
            var byteCount = 0;
            // Estimate a buffer length - might need more depending on coding factor
            var estLength = (input.Count * 3 + 1) / 2;

            var outBuf = _pool.Rent(estLength);

            // Offsets for decoding
            var inputByteOffset = 0;
            var inputBitOffset = 0;

            // Function for resizing the output buffer when necessary
            Action resizeOutBuf = () => {
                // All bytes are occupied, need to resize to accomodate more
                var leftBytes = input.Count - inputByteOffset;
                var newSize = outBuf.Length + leftBytes * 2;
                var newBuf = _pool.Rent(newSize);
                Array.Copy(outBuf, 0, newBuf, 0, byteCount);
                _pool.Return(outBuf);
                outBuf = newBuf;
            };

            var treeNode = HuffmanTree.Root;

            for (inputByteOffset = 0; inputByteOffset < input.Count; inputByteOffset++)
            {
                var bt = input.Array[inputByteOffset];
                for (inputBitOffset = 7; inputBitOffset >= 0; inputBitOffset--) {
                    // Fetch bit at offset position
                    var bit = (bt & (1 << inputBitOffset)) >> inputBitOffset;
                    // Follow the tree branch that is specified by that bit
                    if (bit != 0) treeNode = treeNode.Child1;
                    else treeNode = treeNode.Child0;
                    if (treeNode == null)
                    {
                        _pool.Return(outBuf);
                        throw new Exception("Invalid huffman code");
                    }

                    if (treeNode.Value != -1)
                    {
                        if (treeNode.Value != 256)
                        {
                            // We are at the leaf and got a value
                            if (outBuf.Length - byteCount == 0)
                            {
                                resizeOutBuf();
                            }
                            outBuf[byteCount] = (byte)treeNode.Value;
                            byteCount++;
                            treeNode = HuffmanTree.Root;
                        }
                        else
                        {
                            // EOS symbol
                            // Fully receiving this is a decoding error,
                            // because padding must not be longer than 7 bits
                            _pool.Return(outBuf);
                            throw new Exception("Encountered EOS in huffman code");
                        }
                    }
                    // TODO: A padding strictly longer
                    // than 7 bits MUST be treated as a decoding error.
                }
            }

            // Convert the buffer into a string
            // TODO: Check if encoding is really correct
            var str = Encoding.ASCII.GetString(outBuf, 0, byteCount);
            _pool.Return(outBuf);
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
        public static ArraySegment<byte> Encode(ArraySegment<byte> input, ArraySegment<byte> output)
        {
            if (input.Count == 0) return input;

            var bitOffset = 0;
            var byteOffset = output.Offset;

            for (var i = input.Offset; i  < input.Offset + input.Count; i++)
            {
                // Lookup the corresponding value in the table
                var tableEntry = HuffmanTable.Entries[input.Array[i]];
                var binVal = tableEntry.Bin;
                var bits = tableEntry.Len;

                while (bits > 0)
                {
                    // Put a number of bits into output buffer
                    // Calculate the number of bits first
                    var bitsToPut = 8 - bitOffset;
                    if (bits < bitsToPut) bitsToPut = bits;

                    var val = 0;
                    if (bitOffset == 0) val = 0;
                    else val = output.Array[byteOffset]; // read written value

                    // Take the bitsToPut highest bits from binVal
                    var putBytes = binVal >> (bits - bitsToPut);
                    // Align the value on 8 bits
                    putBytes = putBytes << (8-bitsToPut);
                    // And put it in place at the position where we need it
                    // This can not be directly combined with the former operation,
                    // because otherwise the trailing numbers wouldn't be zeroed
                    val = val | (putBytes >> bitOffset);
                    output.Array[byteOffset] = (byte)(val & 0xFF);
                    bitOffset += bitsToPut;
                    if (bitOffset == 8)
                    {
                        bitOffset = 0;
                        byteOffset++;
                    }
                    bits -= bitsToPut;
                    // Need only the low amount of bits
                    binVal &= ((1<<bits)-1);
                }
            }

            // Complete the last byte with 1s if necessary (EOS symbol)
            if (bitOffset != 0)
            {
                var val = (1 << (8-bitOffset)) - 1;
                val |= output.Array[byteOffset];
                output.Array[byteOffset] = (byte)(val & 0xFF);
                byteOffset++;
            }

            return new ArraySegment<byte>(
                output.Array, output.Offset, byteOffset - output.Offset
            );
        }
    }
}
