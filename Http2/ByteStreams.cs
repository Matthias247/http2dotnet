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

    public interface IReadableByteStream
    {
        /// <summary>
        /// Reads data from a stream into the given buffer segment.
        /// The amound of bytes that will be read is up to the given buffer length
        /// The return value signals how many bytes were actually read.
        /// </summary>
        ValueTask<StreamReadResult> ReadAsync(ArraySegment<byte> buffer);
    }

    public interface IWriteableByteStream
    {
        /// <summary>
        /// Writes the buffer to the stream.
        /// </summary>
        ValueTask<object> WriteAsync(ArraySegment<byte> buffer);
    }

    public interface ICloseableByteStream
    {
        /// <summary>
        /// Closes the stream gracefully.
        /// This signals and EndOfStream to the receiving side once all prior
        /// data has been read.
        /// </summary>
        ValueTask<object> CloseAsync();
    }

    public interface IWriteAndCloseableByteStream
        : IWriteableByteStream, ICloseableByteStream
    {
    }

    /// <summary>
    /// A static instance of a completed ValueTask&lt;object&gt;
    /// </summary>
    internal static class DoneTask
    {
        public static readonly ValueTask<object> Instance =
            new ValueTask<object>(1234);
    }
}