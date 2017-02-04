using System;
using System.Threading.Tasks;
using Http2;

namespace Http2Tests
{
    public class BufferReadStream : Http2.IReadableByteStream
    {
        public byte[] Buffer;
        public int Written = 0;
        public int ReadOffset = 0;
        public int NrReads = 0;
        private int maxRead;

        public BufferReadStream(int bufferSize, int maxRead)
        {
            Buffer = new byte[bufferSize];
            this.maxRead = maxRead;
        }

        async ValueTask<StreamReadResult> IReadableByteStream.ReadAsync(
            ArraySegment<byte> buffer)
        {
            return await Task.Run(() =>
            {
                var available = Written - ReadOffset;
                NrReads++;
                if (available == 0)
                {
                    return new StreamReadResult
                    {
                        BytesRead = 0,
                        EndOfStream = true,
                    };
                }

                var toCopy = Math.Min(buffer.Count, available);
                toCopy = Math.Min(toCopy, maxRead);
                Array.Copy(Buffer, ReadOffset, buffer.Array, buffer.Offset, toCopy);
                ReadOffset += toCopy;

                // We can read up the length
                return new StreamReadResult
                {
                    BytesRead = toCopy,
                    EndOfStream = false,
                };
            });
        }
    }

    public class BufferWriteStream : Http2.IWriteableByteStream
    {
        public byte[] Buffer;
        public int Written = 0;

        public BufferWriteStream(int bufferSize)
        {
            Buffer = new byte[bufferSize];
        }

        async Task IWriteableByteStream.WriteAsync(
            ArraySegment<byte> buffer)
        {
            await Task.Run(() =>
            {
                Array.Copy(buffer.Array, 0, Buffer, Written, buffer.Count);
                Written += buffer.Count;
            });
        }
    }
}
