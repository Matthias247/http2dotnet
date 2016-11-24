using System;
using System.Collections.Generic;
using System.Text;

namespace Hpack
{
    /// <summary>
    /// HPACK encoder
    /// </summary>
    public class Encoder
    {
        /// <summary>
        /// Options for creating an HPACK encoder
        /// </summary>
        public struct Options
        {
            /// <summary>
            /// The start size for the dynamic Table
            /// </summary>
            public int? DynamicTableSize;

            /// <summary>
            /// Whether to apply huffman encoding.
            /// This is true by default
            /// </summary>
            public bool? UseHuffman;
        }

        /// <summary>
        /// The result type of an Encode operation
        /// </summary>
        public struct Result
        {
            /// <summary>Encoded header block</summary>
            public byte[] Bytes;

            /// <summary>The number of header fields that were encoded</summary>
            public int FieldCount;
        }

        HeaderTable _headerTable;
        bool _useHuffman;

        /// <summary>
        /// The current maximum size of the dynamic table.!--
        /// Setting the size might cause evictions.
        /// If the size is changed a header table size update must be sent to the
        /// remote peer. That must be encoded seperatly with the EncodeSizeUpdate()
        /// function.
        /// </summary>
        public int DynamicTableSize
        {
           get { return this._headerTable.MaxDynamicTableSize; }
           set { this._headerTable.MaxDynamicTableSize = value; }
        }

        /// <summary> Gets the actual used size for the dynamic table</summary>
        public int DynamicTableUsedSize
        {
           get { return this._headerTable.UsedDynamicTableSize; }
        }

        /// <summary> Gets the number of elements in the dynamic table</summary>
        public int DynamicTableLength
        {
           get { return this._headerTable.DynamicTableLength; }
        }

        /// <summary>
        /// Creates a new HPACK encoder with default options
        /// </summary>
        public Encoder() : this(null)
        {
        }

        /// <summary>
        /// Creates a new HPACK Encoder
        /// </summary>
        /// <param name="options">Encoder options</param>
        public Encoder(Options? options)
        {
            var dynamicTableSize = Defaults.DynamicTableSize;
            this._useHuffman = true;

            if (options.HasValue)
            {
                var opts = options.Value;
                if (opts.DynamicTableSize.HasValue)
                {
                    dynamicTableSize = opts.DynamicTableSize.Value;
                }
                if (opts.UseHuffman.HasValue)
                {
                    this._useHuffman = opts.UseHuffman.Value;
                }
            }

            // TODO: If the size is not the default size we basically also need
            // to send a size update frame immediatly.
            // However this currently the obligation of the user of this class

            this._headerTable = new HeaderTable(dynamicTableSize);
        }

        /// <summary>
        /// Encodes the current dynamic table size into a size update message
        /// and returns that
        /// </summary>
        private byte[] EncodeSizeUpdate()
        {
            return IntEncoder.Encode(this.DynamicTableSize, 0x20, 5);
        }

        /// <summary>
        /// Encodes the list of the given header fields into a Buffer.
        /// It thereby may only use maxBytes at most.
        /// Therefore the operations returns the encoded header fields as well
        /// as an information how many fields were encoded. If not all fields
        /// were encoded these should be encoded into a seperate header block.
        /// </summary>
        /// <returns>The number of processed bytes</returns>
        public Result Encode(IEnumerable<HeaderField> headers, int maxBytes)
        {
            byte[] result = new byte[0];
            var nrEncodedHeaders = 0;

            foreach (var header in headers)
            {
                // Lookup in HeaderTable if we have a matching element
                bool fullMatch;
                var idx = _headerTable.GetBestMatchingIndex(header, out fullMatch);
                var nameMatch = idx != -1;

                byte []fieldBytes;

                if (fullMatch)
                {
                    // Encode index with 7bit prefix
                    fieldBytes = IntEncoder.Encode(idx, 0x80, 7);
                }
                else
                {
                    // Check if we want to add the new entry to the table or not
                    // Determine name and value length for that
                    var nameLen = Encoding.ASCII.GetByteCount(header.Name);
                    var valLen = Encoding.ASCII.GetByteCount(header.Value);

                    var addToIndex = false;
                    var neverIndex = false;
                    if (header.Sensitive)
                    {
                        // Mark as never index index the field
                        neverIndex = true;
                    }
                    else
                    {
                        // Add fields to the index if they can fit in the dynamic table
                        // at all. Otherwise they would only kill it's state.
                        // This logic could be improved, e.g. by determining if fields
                        // should be added based on the current size of the header table.
                        if (this.DynamicTableSize >= 32 + nameLen + valLen)
                        {
                            addToIndex = true;
                        }
                    }

                    byte[] nameBytes;

                    if (addToIndex)
                    {
                        if (nameMatch)
                        {
                            // Encode index with 6bit prefix
                            nameBytes = IntEncoder.Encode(idx, 0x40, 6);
                        }
                        else
                        {
                            // Write 0x40 and name
                            var str = StringEncoder.Encode(header.Name, this._useHuffman);
                            nameBytes = new byte[1+str.Length];
                            nameBytes[0] = 0x40;
                            Array.Copy(str, 0, nameBytes, 1, str.Length);
                        }
                        // Add the encoded field to the index
                        this._headerTable.Insert(header.Name, nameLen, header.Value, valLen);
                    }
                    else if (!neverIndex)
                    {
                        if (nameMatch)
                        {
                            // Encode index with 4bit prefix
                            nameBytes = IntEncoder.Encode(idx, 0x00, 4);
                        }
                        else
                        {
                            // Write 0x00 and name
                            var str = StringEncoder.Encode(header.Name, this._useHuffman);
                            nameBytes = new byte[1+str.Length];
                            nameBytes[0] = 0x00;
                            Array.Copy(str, 0, nameBytes, 1, str.Length);
                        }
                    }
                    else
                    {
                        if (nameMatch)
                        {
                            // Encode index with 4bit prefix
                            nameBytes = IntEncoder.Encode(idx, 0x10, 4);
                        }
                        else
                        {
                            // Write 0x10 and name
                            var str = StringEncoder.Encode(header.Name, this._useHuffman);
                            nameBytes = new byte[1+str.Length];
                            nameBytes[0] = 0x10;
                            Array.Copy(str, 0, nameBytes, 1, str.Length);
                        }
                    }

                    // Write the value string
                    var valueBytes = StringEncoder.Encode(header.Value, this._useHuffman);
                    // Merge everything into fieldBytes
                    fieldBytes = new byte[nameBytes.Length + valueBytes.Length];
                    Array.Copy(nameBytes, 0, fieldBytes, 0, nameBytes.Length);
                    Array.Copy(valueBytes, 0, fieldBytes, nameBytes.Length, valueBytes.Length);
                }

                // Copy the bytes from the field if it will fit into
                // the given maximum size
                if (result.Length + fieldBytes.Length <= maxBytes)
                {
                    var newResult = new byte[result.Length + fieldBytes.Length];
                    Array.Copy(result, 0, newResult, 0, result.Length);
                    Array.Copy(fieldBytes, 0, newResult, result.Length, fieldBytes.Length);
                    result = newResult;
                    nrEncodedHeaders++;
                    // Encode more headers
                }
                else
                {
                    // Not enough space for this header
                    break;
                }
            }

            return new Result
            {
                Bytes = result,
                FieldCount = nrEncodedHeaders,
            };
        }
    }
}
