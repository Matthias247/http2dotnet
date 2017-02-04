using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Http2.Hpack;

namespace Http2
{
    /// <summary>
    /// Enumerates possible states of a stream
    /// </summary>
    public enum StreamState
    {
        Idle,
        ReservedLocal,
        ReservedRemote,
        Open,
        HalfClosedLocal,
        HalfClosedRemote,
        Closed,
        Reset, // TODO: Should this be an extra state?
    }

    /// <summary>
    /// A HTTP/2 stream
    /// </summary>
    public interface IStream
        : IReadableByteStream, IWriteAndCloseableByteStream, IDisposable
    {
        /// <summary>The ID of the stream</summary>
        uint Id { get; }
        /// <summary>Returns the current state of the stream</summary>
        StreamState State { get; }

        /// <summary>
        /// Cancels the stream.
        /// This will cause sending a RESET frame to the remote peer
        /// if the stream was not yet fully processed.
        /// </summary>
        void Cancel();

        /// <summary>
        /// Reads the list of incoming header fields from the stream.
        /// These will include all pseudo header fields.
        /// However the validity of the header fields will have been verified.
        /// </summary>
        Task<IEnumerable<HeaderField>> ReadHeadersAsync();

        /// <summary>
        /// Reads the list of incoming trailing header fields from the stream.
        /// The validity of the header fields will have been verified.
        /// </summary>
        Task<IEnumerable<HeaderField>> ReadTrailersAsync();

        /// <summary>
        /// Writes a header block for the stream.
        /// </summary>
        /// <param name="headers">
        /// The list of header fields to write.
        /// The headers must contain the required pseudo headers for the
        /// type of the stream. The pseudo headers must be at the start
        /// of the list.
        /// </param>
        /// <param name="endOfStream">
        /// Whether the stream should be closed with the headers.
        /// If this is set to true no data frames may be written.
        /// </param>
        Task WriteHeadersAsync(IEnumerable<HeaderField> headers, bool endOfStream);

        /// <summary>
        /// Writes a block of trailing headers for the stream.
        /// The writing side of the stream will automatically be closed
        /// through this operation.
        /// </summary>
        /// <param name="headers">
        /// The list of trailing headers to write.
        /// No pseudo headers must be contained in this list.
        /// </param>
        Task WriteTrailersAsync(IEnumerable<HeaderField> headers);

        /// <summary>
        /// Writes data to the stream and optionally allows to signal the end
        /// of the stream.
        /// </summary>
        /// <param name="buffer">The block of data to write</param>
        /// <param name="endOfStream">
        /// Whether this is the last data block and the stream should be closed
        /// after this operation
        /// </param>
        Task WriteAsync(ArraySegment<byte> buffer, bool endOfStream = false);
    }

    /// <summary>
    /// Signals that a stream was reset
    /// </summary>
    public class StreamResetException : Exception
    {
    }
}