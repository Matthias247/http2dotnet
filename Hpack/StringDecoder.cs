using System;
using System.Buffers;
using System.Text;

namespace Http2.Hpack
{
    /// <summary>
    /// Decodes string values according to the HPACK specification.
    /// </summary>
    public class StringDecoder : IDisposable
    {
        private enum State: byte
        {
            StartDecode,
            DecodeLength,
            DecodeData,
        }

        /// <summary>The result of the decode operation</summary>
        public string Result;
        /// <summary>The length of the decoded string</summary>
        public int StringLength;
        /// <summary>
        /// Whether decoding was completed.
        /// This is set after a call to Decode().
        /// If a complete integer could be decoded from the input buffer
        /// the value is true. If a complete integer could not be decoded
        /// then more bytes are needed and decodeCont must be called until
        /// done is true before reading the result.
        /// </summary>
        public bool Done = true;

        /// <summary>Whether the input data is huffman encoded</summary>
        private bool _huffman;
        /// <summary>The state of the decoder</summary>
        private State _state = State.StartDecode;
        /// <summary>The number of octets in the string</summary>
        private int _octetLength;
        /// <summary>Buffer for the received bytes of the string</summary>
        private byte[] _stringBuffer;
        /// <summary>The number of bytes that already have been read</summary>
        private int _bufferOffset;
        /// <summary>The maximum allowed byte length for strings</summary>
        private int _maxLength;
        /// <summary>Decoder for the string length</summary>
        private IntDecoder _lengthDecoder = new IntDecoder();
        /// <summary>
        /// The pool from which temporary buffers for string decoding should
        /// be rented
        /// </summary>
        private ArrayPool<byte> _bufferPool;


        public StringDecoder(
            int maxLength, ArrayPool<byte> bufferPool)
        {
            if (maxLength < 1) throw new ArgumentException(nameof(maxLength));
            if (bufferPool == null) throw new ArgumentException(nameof(bufferPool));
            _maxLength = maxLength;
            _bufferPool = bufferPool;
        }

        public void Dispose()
        {
            if (_stringBuffer != null)
            {
                _bufferPool.Return(_stringBuffer);
                _stringBuffer = null;
            }

            _bufferPool = null;
        }

        public int Decode(ArraySegment<byte> buf)
        {
            var offset = buf.Offset;
            var length = buf.Count;

            // Check if there's a leftover string from the last decode process.
            // Should normally not happen.
            if (_stringBuffer != null)
            {
                _bufferPool.Return(_stringBuffer);
                _stringBuffer = null;
            }

            var bt = buf.Array[offset];
            this._huffman = (bt & 0x80) == 0x80;
            this.Done = false;
            this._state = State.DecodeLength;
            var consumed = this._lengthDecoder.Decode(7, buf);
            length -= consumed;
            offset += consumed;

            if (this._lengthDecoder.Done)
            {
                var len = this._lengthDecoder.Result;
                if (len > this._maxLength)
                    throw new Exception("Maximum string length exceeded");
                this._octetLength = len;
                this._stringBuffer = _bufferPool.Rent(this._octetLength);
                this._bufferOffset = 0;
                this._state = State.DecodeData;
                consumed += this.DecodeCont(new ArraySegment<byte>(buf.Array, offset, length));
                return consumed;
            }
            else
            {
                // Need more input data to decode octetLength
                return consumed;
            }
        }

        private int DecodeContLength(ArraySegment<byte> buf)
        {
            var offset = buf.Offset;
            var length = buf.Count;

            var consumed = this._lengthDecoder.DecodeCont(buf);
            length -= consumed;
            offset += consumed;

            if (this._lengthDecoder.Done)
            {
                var len = this._lengthDecoder.Result;
                if (len > this._maxLength)
                    throw new Exception("Maximum string length exceeded");
                this._octetLength = len;
                this._stringBuffer = _bufferPool.Rent(this._octetLength);
                this._bufferOffset = 0;
                this._state = State.DecodeData;
            }
            // else need more data to decode octetLength

            return consumed;
        }

        private int DecodeContByteData(ArraySegment<byte> buf)
        {
            var offset = buf.Offset;
            var count = buf.Count;

            // Check how many bytes are available and how much we need
            var available = count;
            var need = this._octetLength - this._bufferOffset;

            var toCopy = available >= need ? need : available;
            if (toCopy > 0)
            {
                // Return is wrong because it doesn't handle 0byte strings
                // Copy that amount of data into our target buffer
                Array.Copy(buf.Array, offset, this._stringBuffer, this._bufferOffset, toCopy);
                this._bufferOffset += toCopy;
                // Adjust the offset of the input and output buffer
                offset += toCopy;
                count -= toCopy;
            }

            if (this._bufferOffset == this._octetLength)
            {
                // Copied everything
                var view = new ArraySegment<byte>(
                    this._stringBuffer, 0, this._octetLength
                );
                if (this._huffman)
                {
                    // We need to perform huffman decoding
                    this.Result = Huffman.Decode(view, _bufferPool);
                }
                else
                {
                    // TODO: Check if encoding is really correct
                    this.Result =
                        Encoding.ASCII.GetString(view.Array, view.Offset, view.Count);
                }
                // TODO: Optionally check here for valid HTTP/2 header names
                this.Done = true;
                // The string length for the table is used without huffman encoding
                // TODO: This might by a different result than Encoding.ASCII.GetByteCount
                // Might be required to streamline that
                this.StringLength = this.Result.Length;
                this._state = State.StartDecode;

                _bufferPool.Return(this._stringBuffer);
                this._stringBuffer = null;
            }
            // Else we need more input data

            return offset - buf.Offset;
        }

        public int DecodeCont(ArraySegment<byte> buf)
        {
            var offset = buf.Offset;
            var count = buf.Count;

            if (this._state == State.DecodeLength && count > 0)
            {
                // Continue to decode the data length
                var consumed = this.DecodeContLength(buf);
                offset += consumed;
                count -= consumed;
                // Decoding the length might have moved us into the DECODE_DATA state
            }

            if (this._state == State.DecodeData)
            {
                // Continue to decode the data
                var consumed = this.DecodeContByteData(
                    new ArraySegment<byte>(buf.Array, offset, count));
                offset += consumed;
                count -= consumed;
            }

            return offset - buf.Offset;
        }
    }
}

// TODO: This will leak memory from the pool if a string could not be fully decoded
// However as that's not the normal situation it won't be too bad, as it will
// still get garbage collected.