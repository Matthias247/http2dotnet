using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Hpack;

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
    public interface IStream : IStreamReader, IStreamWriterCloser
    {
        /// <summary>The ID of the stream</summary>
        uint Id { get; }
        /// <summary>Returns the current state of the stream</summary>
        StreamState State { get; }
        /// <summary>Resets the stream</summary>
        ValueTask<object> Reset();

        ValueTask<object> WriteHeaders(IEnumerable<HeaderField> headers, bool endOfStream);

        ValueTask<object> WriteTrailers(IEnumerable<HeaderField> headers);

        ValueTask<object> WriteAsync(ArraySegment<byte> buffer, bool endOfStream = false);
    }
}