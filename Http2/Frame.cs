using System;
using System.Collections.Generic;
using System.Text;

namespace Http2
{
    /// <summary>
    /// Possible frame types with associated opcodes for HTTP/2
    /// </summary>
    public enum FrameType : byte
    {
        Data = 0x0,
        Headers = 0x1,
        Priority = 0x2,
        ResetStream = 0x3,
        Settings = 0x4,
        PushPromise = 0x5,
        Ping = 0x6,
        GoAway = 0x7,
        WindowUpdate = 0x8,
        Continuation = 0x9,
    }

    /// <summary>
    /// A header of an HTTP/2 frame
    /// </summary>
    public struct FrameHeader
    {
        /// <summary>The type of the frame</summary>
        public FrameType Type;

        /// <summary>31bit stream identifier</summary>
        public uint StreamId;

        /// <summary>24bit length of the frame</summary>
        public int Length;

        /// <summary>
        /// An 8-bit field reserved for boolean flags specific to the frame type
        /// </summary>
        public byte Flags;
    }

    /// <summary>
    /// Priority data that is shared by multiple frames
    /// </summary>
    public struct PriorityData
    {
        /// <summary>
        /// A 31-bit stream identifier for the stream that this stream depends on.
        /// This field is only present if the PRIORITY flag is set
        /// </summary>
        public int StreamDependency;

        /// <summary>
        /// A single-bit flag indicating that the stream dependency is exclusive.
        /// This field is only present if the PRIORITY flag is set
        /// </summary>
        public bool StreamDependencyIsExclusive;

        /// <summary>
        /// An unsigned 8-bit integer representing a priority weight for the stream.
        /// Add one to the value to obtain a weight between 1 and 256.
        /// This field is only present if the PRIORITY flag is set.
        /// </summary>
        public byte Weight;
    }

    /// <summary>
    /// Valid flags for DATA frames
    /// </summary>
    public enum DataFrameFlags : byte
    {
        /// <summary>
        /// When set, bit 0 indicates that this frame is the last
        /// that the endpoint will send for the identified stream.
        /// Setting this flag causes the stream to enter one of the
        /// "half-closed" states or the "closed" state
        /// </summary>
        EndOfStream= 0x01,

        /// <summary>
        /// When set, bit 3 indicates that the Pad Length field and
        /// any padding that it describes are present.
        /// </summary>
        Padded = 0x08,
    }

    /// <summary>
    /// Valid flags for HEADERS frames
    /// </summary>
    public enum HeadersFrameFlags : byte
    {
        /// <summary>
        /// When set, bit 0 indicates that the header block ist the last
        /// that the endpoint will send for the identified stream.
        /// A HEADERS frame carries the END_STREAM flag that signals the end of a stream.
        /// However, a HEADERS frame with the END_STREAM flag set can be followed
        /// by CONTINUATION frames on the same stream.
        /// Logically, the CONTINUATION frames are part of the HEADERS frame.
        /// </summary>
        EndOfStream = 0x01,

        /// <summary>
        /// When set, bit 2 indicates that this frame contains an entire header block
        /// and is not followed by any CONTINUATION frames.
        /// </summary>
        EndOfHeaders = 0x4,

        /// <summary>
        /// When set, bit 3 indicates that the Pad Length field and
        /// any padding that it describes are present.
        /// </summary>
        Padded = 0x08,

        /// <summary>
        /// When set, bit 5 indicates that the Exclusive Flag (E),
        /// Stream Dependency, and Weight fields are present
        /// </summary>
        Priority = 0x20,
    }

    /// <summary>
    /// Valid flags for HEADERS frames
    /// </summary>
    public enum SettingsFrameFlags : byte
    {
        /// <summary>
        /// When set, bit 0 indicates that this frame acknowledges receipt and
        /// application of the peer's SETTINGS frame. When this bit is set, the
        /// payload of the SETTINGS frame MUST be empty. Receipt of a SETTINGS
        /// frame with the ACK flag set and a length field value other than 0 MUST
        /// be treated as a connection error (Section 5.4.1) of type FRAME_SIZE_ERROR.
        /// For more information, see Section 6.5.3 ("Settings Synchronization")
        /// </summary>
        Ack = 0x01
    }

    /// <summary>
    /// Valid flags for PUSH_PROMISE frames
    /// </summary>
    public enum PushPromiseFrameFlags : byte
    {
        /// <summary>
        /// When set, bit 2 indicates that this frame contains an entire header block
        /// and is not followed by any CONTINUATION frames.
        /// </summary>
        EndOfHeaders = 0x4,

        /// <summary>
        /// When set, bit 3 indicates that the Pad Length field and
        /// any padding that it describes are present.
        /// </summary>
        Padded = 0x08,

        /// <summary>
        /// When set, bit 5 indicates that the Exclusive Flag (E),
        /// Stream Dependency, and Weight fields are present
        /// </summary>
        Priority = 0x20,
    }

    /// <summary>
    /// Valid flags for PING frames
    /// </summary>
    public enum PingFrameFlags : byte
    {
        /// <summary>
        /// When set, bit 0 indicates that this PING frame is a PING response.
        /// An endpoint MUST set this flag in PING responses.
        /// An endpoint MUST NOT respond to PING frames containing this flag.
        /// </summary>
        Ack = 0x01,
    }

    /// <summary>
    /// Valid flags for CONTINUATION frames
    /// </summary>
    public enum ContinuationFrameFlags : byte
    {
        /// <summary>
        /// When set, bit 2 indicates that this frame ends a header block
        /// </summary>
        EndOfHeaders = 0x4,
    }

}
