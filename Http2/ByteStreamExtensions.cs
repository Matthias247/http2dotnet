using System;
using System.Threading.Tasks;

namespace Http2
{
    /// <summary>
    /// Utility and extension functions for working with byte streams
    /// </summary>
    public static class ByteStreamExtensions
    {
        /// <summary>
        /// Tries to read exactly the given amount of data from a stream.
        /// The method will only return if all data was read, the stream
        /// closed or the an error happened.
        /// If the input is a 0 byte buffer the method will always succeed,
        /// even if the underlying stream was already closed.
        /// </summary>
        /// <param name="stream">The stream to read data from</param>
        /// <param name="buffer">The destination buffer</param>
        /// <returns>Awaitable task object</returns>
        public async static ValueTask<DoneHandle> ReadAll(
            this IReadableByteStream stream, ArraySegment<byte> buffer)
        {
            var array = buffer.Array;
            var offset = buffer.Offset;
            var count = buffer.Count;

            // Remark: This will not perform actual 0 byte reads to the underlying 
            // stream, which means it won't detect closed streams on 0 byte reads.
            
            while (count != 0)
            {
                var segment = new ArraySegment<byte>(array, offset, count);
                var res = await stream.ReadAsync(segment);
                if (res.EndOfStream)
                {
                    throw new System.IO.EndOfStreamException();
                }
                offset += res.BytesRead;
                count -= res.BytesRead;
            }

            return DoneHandle.Instance;
        }
    }
}
