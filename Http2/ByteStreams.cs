using System;
using System.Threading.Tasks;

namespace Http2
{
    /// <summary>
    /// The result of a ReadAsync operation
    /// </summary>
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
        Task WriteAsync(ArraySegment<byte> buffer);
    }

    public interface ICloseableByteStream
    {
        /// <summary>
        /// Closes the stream gracefully.
        /// This should signal EndOfStream to the receiving side once all prior
        /// data has been read.
        /// </summary>
        Task CloseAsync();
    }

    public interface IWriteAndCloseableByteStream
        : IWriteableByteStream, ICloseableByteStream
    {
    }

    /// <summary>
    /// A marker class that is used to signal the completion of an Async operation.
    /// This purely exists since ValueTask&lt;Void&gt; is not valid in C#.
    /// </summary>
    public class DoneHandle
    {
        private DoneHandle() {}

        /// <summary>
        /// A static instance of the Handle
        /// </summary>
        public static readonly DoneHandle Instance = new DoneHandle();
    }
}