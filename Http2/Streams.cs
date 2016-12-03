using System;
using System.Threading.Tasks;

namespace Http2
{
    public struct StreamReadResult
    {
        /// <summary>
        /// The amount of bytes that were read
        /// </summary>
        public int BytesRead;

        /// <summary>
        /// Whether the end of the stream was reached
        /// In this case no bytes should be read
        /// </summary>
        public bool EndOfStream;
    }

    public interface IStreamReader
    {
        /// <summary>
        /// Reads data from a stream into the given buffer segment.
        /// The amound of bytes that will be read is up to the given buffer length
        /// The return value signals how many bytes were actually read.
        /// </summary>
        ValueTask<StreamReadResult> ReadAsync(ArraySegment<byte> buffer);
    }

    public interface IStreamWriter
    {
        /// <summary>
        /// Writes the buffer to the stream.
        /// </summary>
        ValueTask<object> WriteAsync(ArraySegment<byte> buffer);
    }

    public interface IStreamCloser
    {
        /// <summary>
        /// Closes the stream gracefully.
        /// This signals and EndOfStream to the receiving side once all prior
        /// data has been read.
        /// </summary>
        ValueTask<object> CloseAsync();
    }

    interface IStreamWriterCloser : IStreamWriter, IStreamCloser
    {
    }
}