using System;
using System.Collections.Generic;

namespace Http2.Hpack
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
            /// By default huffman encoding will be applied when the output gets
            /// smaller through it.
            /// </summary>
            public HuffmanStrategy? HuffmanStrategy;
        }

        /// <summary>
        /// The result type of an Encode operation
        /// </summary>
        public struct Result
        {
            /// <summary>The number of used bytes of the input buffer</summary>
            public int UsedBytes;

            /// <summary>The number of header fields that were encoded</summary>
            public int FieldCount;
        }

        HeaderTable _headerTable;
        HuffmanStrategy _huffmanStrategy;

        /// <summary>
        /// The minimum table size that existed between encoding 2 header blocks.
        /// -1 means no update must be transmitted.
        /// </summary>
        int _tableSizeUpdateMinValue = -1;
        /// <summary>
        /// The table size that must be communicated to the remote in the next
        /// header block after a table update was performed.
        /// -1 means no update must be transmitted.
        /// </summary>
        int _tableSizeUpdateFinalValue = -1;

        /// <summary>
        /// The current maximum size of the dynamic table.
        /// Setting the size might cause evictions.
        /// If the size is changed a header table size update must be sent to the
        /// remote peer. The new table size will be sent at the start of the next
        /// encoded header block to the remote peer.
        /// </summary>
        public int DynamicTableSize
        {
            get { return this._headerTable.MaxDynamicTableSize; }
            set
            {
                var current = _headerTable.MaxDynamicTableSize;
                if (current == value) return;
                // Track whether a sending a header table size update is required
                // when encoding the next header block.
                // We might need to send multiple size updates in the worst case,
                // if the remote requires multiple table size updates.
                // If the size is only inreased or only decreased we can simply
                // use the latest value. However if the size is first decreased
                // and then increased we must also announce the lowest value to
                // trigger the eviction process.
                _tableSizeUpdateFinalValue = value;
                if (_tableSizeUpdateMinValue == -1 || value < _tableSizeUpdateMinValue)
                {
                    _tableSizeUpdateMinValue = value;
                }
                this._headerTable.MaxDynamicTableSize = value;
            }
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
            this._huffmanStrategy = HuffmanStrategy.IfSmaller;

            if (options.HasValue)
            {
                var opts = options.Value;
                if (opts.DynamicTableSize.HasValue)
                {
                    dynamicTableSize = opts.DynamicTableSize.Value;
                }
                if (opts.HuffmanStrategy.HasValue)
                {
                    this._huffmanStrategy = opts.HuffmanStrategy.Value;
                }
            }

            // TODO: If the size is not the default size we basically also need
            // to send a size update frame immediatly.
            // However this currently the obligation of the user of this class

            this._headerTable = new HeaderTable(dynamicTableSize);
        }

        /// <summary>
        /// Encodes the list of the given header fields into a Buffer, which
        /// represents the data part of a header block fragment.
        /// The operation will only try to write as much headers as fit in the
        /// fragment.
        /// Therefore the operations returns the size of the encoded header
        /// fields as well as an information how many fields were encoded.
        /// If not all fields were encoded these should be encoded into a
        /// seperate header block.
        /// If the input headers list was bigger than 0 and 0 encoded headers
        /// are reported as a result this means the target buffer is too small
        /// for encoding even a single header field. In this case using
        /// continuation frames won't help, as they header field also wouldn't
        /// fit there.
        /// </summary>
        /// <returns>The used bytes and number of encoded headers</returns>
        public Result EncodeInto(
            ArraySegment<byte> buf,
            IEnumerable<HeaderField> headers)
        {
            var nrEncodedHeaders = 0;
            var offset = buf.Offset;
            var count = buf.Count;

            // Encode table size updates at the beginning of the headers
            // If the minimum size equals the final size we do not need to
            // transmit both.
            var tableUpdatesOk = true;
            if (_tableSizeUpdateMinValue != -1 &&
                _tableSizeUpdateMinValue != _tableSizeUpdateFinalValue)
            {
                var used = IntEncoder.EncodeInto(
                    new ArraySegment<byte>(buf.Array, offset, count),
                    this._tableSizeUpdateMinValue, 0x20, 5);
                if (used == -1)
                {
                    tableUpdatesOk = false;
                }
                else
                {
                    offset += used;
                    count -= used;
                    _tableSizeUpdateMinValue = -1;
                }
            }
            if (_tableSizeUpdateFinalValue != -1)
            {
                var used = IntEncoder.EncodeInto(
                    new ArraySegment<byte>(buf.Array, offset, count),
                    this._tableSizeUpdateFinalValue, 0x20, 5);
                if (used == -1)
                {
                    tableUpdatesOk = false;
                }
                else
                {
                    offset += used;
                    count -= used;
                    _tableSizeUpdateMinValue = -1;
                    _tableSizeUpdateFinalValue = -1;
                }
            }

            if (!tableUpdatesOk)
            {
                return new Result
                {
                    UsedBytes = 0,
                    FieldCount = 0,
                }; 
            }

            foreach (var header in headers)
            {
                // Lookup in HeaderTable if we have a matching element
                bool fullMatch;
                var idx = _headerTable.GetBestMatchingIndex(header, out fullMatch);
                var nameMatch = idx != -1;

                // Make a copy for the offset that can be manipulated and which
                // will not yield in losing the real offset if we can only write
                // a part of a field
                var tempOffset = offset;

                if (fullMatch)
                {
                    // Encode index with 7bit prefix
                    var used = IntEncoder.EncodeInto(
                        new ArraySegment<byte>(buf.Array, tempOffset, count),
                        idx, 0x80, 7);
                    if (used == -1) break;
                    tempOffset += used;
                    count -= used;
                }
                else
                {
                    // Check if we want to add the new entry to the table or not
                    // Determine name and value length for that
                    // TODO: This could be improved by using a bytecount method
                    // that only counts up to DynamicTableSize, since we can't
                    // add more bytes anyway
                    var nameLen = StringEncoder.GetByteLength(header.Name);
                    var valLen = StringEncoder.GetByteLength(header.Value);

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

                    if (addToIndex)
                    {
                        if (nameMatch)
                        {
                            // Encode index with 6bit prefix
                            var used = IntEncoder.EncodeInto(
                                new ArraySegment<byte>(buf.Array, tempOffset, count),
                                idx, 0x40, 6);
                            if (used == -1) break;
                            tempOffset += used;
                            count -= used;
                        }
                        else
                        {
                            // Write 0x40 and name
                            if (count < 1) break;
                            buf.Array[tempOffset] = 0x40;
                            tempOffset += 1;
                            count -= 1;
                            var used = StringEncoder.EncodeInto(
                                new ArraySegment<byte>(buf.Array, tempOffset, count),
                                header.Name, nameLen, this._huffmanStrategy);
                            if (used == -1) break;
                            tempOffset += used;
                            count -= used;
                        }
                        // Add the encoded field to the index
                        this._headerTable.Insert(header.Name, nameLen, header.Value, valLen);
                    }
                    else if (!neverIndex)
                    {
                        if (nameMatch)
                        {
                            // Encode index with 4bit prefix
                            var used = IntEncoder.EncodeInto(
                                new ArraySegment<byte>(buf.Array, tempOffset, count),
                                idx, 0x00, 4);
                            if (used == -1) break;
                            tempOffset += used;
                            count -= used;
                        }
                        else
                        {
                            // Write 0x00 and name
                            if (count < 1) break;
                            buf.Array[tempOffset] = 0x00;
                            tempOffset += 1;
                            count -= 1;
                            var used = StringEncoder.EncodeInto(
                                new ArraySegment<byte>(buf.Array, tempOffset, count),
                                header.Name, nameLen, this._huffmanStrategy);
                            if (used == -1) break;
                            tempOffset += used;
                            count -= used;
                        }
                    }
                    else
                    {
                        if (nameMatch)
                        {
                            // Encode index with 4bit prefix
                            var used = IntEncoder.EncodeInto(
                                new ArraySegment<byte>(buf.Array, offset, count),
                                idx, 0x10, 4);
                            if (used == -1) break;
                            tempOffset += used;
                            count -= used;
                        }
                        else
                        {
                            // Write 0x10 and name
                            if (count < 1) break;
                            buf.Array[tempOffset] = 0x10;
                            tempOffset += 1;
                            count -= 1;
                            var used = StringEncoder.EncodeInto(
                                new ArraySegment<byte>(buf.Array, tempOffset, count),
                                header.Name, nameLen, this._huffmanStrategy);
                            if (used == -1) break;
                            tempOffset += used;
                            count -= used;
                        }
                    }

                    // Write the value string
                    var usedForValue = StringEncoder.EncodeInto(
                        new ArraySegment<byte>(buf.Array, tempOffset, count),
                        header.Value, valLen, this._huffmanStrategy);
                    if (usedForValue == -1) break;
                    // Writing the value succeeded
                    tempOffset += usedForValue;
                    count -= usedForValue;
                }

                // If we got here the whole field could be encoded into the
                // target buffer
                offset = tempOffset;
                nrEncodedHeaders++;
            }

            return new Result
            {
                UsedBytes = offset - buf.Offset,
                FieldCount = nrEncodedHeaders,
            };
        }
    }
}
