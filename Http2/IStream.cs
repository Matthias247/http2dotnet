using System;
using System.Threading.Tasks;

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
        /// <summary> The ID of the stream</summary>
        int Id { get; }
        /// <summary> Returns the current state of the stream</summary>
        StreamState State { get; }
        /// <summary> Resets the stream</summary>
        ValueTask<object> Reset();
    }
}