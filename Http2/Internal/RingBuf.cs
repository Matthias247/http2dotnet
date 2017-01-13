using System;
using System.Collections.Generic;
using System.Buffers;
using System.Text;

namespace Http2.Internal
{
    /// <summary>
    /// Implements a RingBuffer of bytes with the given capacity
    /// </summary>
    public class RingBuf : IDisposable
    {
        private static ArrayPool<byte> _pool = ArrayPool<byte>.Shared;

        byte[] buffer;
        private int bufferLength = 0;
        private int writeOffset = 0;
        private int readOffset = 0;
        private bool writerNotAdvanced = true;

        /// <summary>
        /// Creates a new RingBuffer of the given capacity
        /// </summary>
        public RingBuf(int capacity)
        {
            if (capacity < 1) throw new ArgumentException(nameof(capacity));
            buffer = _pool.Rent(capacity);
            bufferLength = capacity;
        }

        /// <summary>
        /// Disposes the ringbuffer and frees the used byte array
        /// </summary>
        public void Dispose()
        {
            _pool.Return(buffer);
            buffer = null;
        }

        /// <summary>
        /// The amount of data that is available to read in the ringbuffer
        /// </summary>
        public int Available
        {
            get
            {
                if (this.writerNotAdvanced)
                {
                    return this.writeOffset - this.readOffset;
                }
                else
                {
                    var avail = this.bufferLength - this.readOffset;
                    avail += this.writeOffset;
                    return avail;
                }
            }
        }

        /// <summary>
        /// Returns the total capacity of the ringbuffer
        /// </summary>
        public int Capacity => this.bufferLength;

        /// <summary>
        /// The amount of free space in the ringbuffer
        /// </summary>
        public int Free => this.bufferLength - this.Available;

        /// <summary>
        /// Writes the amount of data into the ringbuffer.
        /// Must only be called if enough space is available.
        /// </summary>
        public void Write(ArraySegment<byte> data)
        {
            if (data.Count == 0) return;
            if (this.Free < data.Count)
            {
                throw new Exception("Not enough free space in the buffer");
            }

            var written = 0;

            // Write until the end of the buffer
            // No need to take care that we don't overwrite the readOffset, since we
            // check the available space before
            var freeToEnd = this.bufferLength - this.writeOffset;
            var toCopy = Math.Min(data.Count, freeToEnd);
            Array.Copy(data.Array, data.Offset, buffer, writeOffset, toCopy);

            written += toCopy;
            this.writeOffset += toCopy;
            if (this.writeOffset == this.bufferLength)
            {
                // Wrap around
                this.writeOffset = 0;
                this.writerNotAdvanced = false;
            }

            if (written == data.Count) return; // We're done

            // Write remaining data into next iteration
            toCopy = data.Count - written;
            Array.Copy(data.Array, data.Offset + written, buffer, writeOffset, toCopy);

            written += toCopy;
            this.writeOffset += toCopy;
            if (this.writeOffset == this.bufferLength)
            {
                // Can't happen if size check works
                throw new Exception("Unexpected wrap around");
            }
        }

        /// <summary>
        /// Copies the requested amount of data from the RingBuffer into the target
        /// buffer.
        /// It is not valid to read more bytes then there are available.
        /// </summary>
        public void Read(ArraySegment<byte> data)
        {
            if (data.Count > this.Available)
            {
                throw new Exception("Invalid read length");
            }

            var avail = 0; // Amount of data that is available for a first read
            if (this.writerNotAdvanced)
            {
                avail = this.writeOffset - this.readOffset;
            }
            else
            {
                avail = this.bufferLength - this.readOffset;
            }

            var toCopy = data.Count;
            if (avail < toCopy) toCopy = avail;
            Array.Copy(buffer, readOffset, data.Array, data.Offset, toCopy);

            var read = toCopy;
            this.readOffset += read;
            if (this.readOffset == this.bufferLength)
            {
                this.readOffset = 0;
                this.writerNotAdvanced = true; // Reader catched up
            }

            if (read == data.Count) return;

            toCopy = data.Count - read;
            Array.Copy(buffer, readOffset, data.Array, data.Offset + read, toCopy);

            read += toCopy;
            this.readOffset += toCopy;
            if (this.readOffset == this.bufferLength)
            {
                // Can't happen if size check works
                throw new Exception("Unexpected wrap around");
            }
        }
    }
}
