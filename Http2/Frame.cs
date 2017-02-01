using System;
using System.Collections.Generic;
using System.Threading.Tasks;

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

        /// <summary>The length of frame headers in bytes</summary>
        public const int HeaderSize = 9;

        /// <summary>
        /// Decodes a frame header from the given byte array
        /// This must be at least 9 bytes long
        /// </summary>
        public static FrameHeader DecodeFrom(ArraySegment<byte> bytes)
        {
            var b = bytes.Array;
            var o = bytes.Offset;
            var length = (b[o+0] << 16) | (b[o+1] << 8) | (b[o+2] & 0xFF);
            var type = (FrameType)b[o+3];
            var flags = b[o+4];
            var streamId = ((b[o+5] & 0x7F) << 24) | (b[o+6] << 16) | (b[o+7] << 8) | b[o+8];
            return new FrameHeader
            {
                Type = type,
                Length = length,
                Flags = flags,
                StreamId = (uint)streamId,
            };
        }

        /// <summary>
        /// Encodes a frame header to the given byte array
        /// This must be at least 9 bytes long
        /// </summary>
        public void EncodeInto(ArraySegment<byte> bytes)
        {
            var b = bytes.Array;
            var o = bytes.Offset;
            
            var length = (b[o+0] << 16) | (b[o+1] << 8) | (b[o+2] & 0xFF);
            b[o+0] = (byte)((Length >> 16) & 0xFF);
            b[o+1] = (byte)((Length >> 8) & 0xFF);
            b[o+2] = (byte)((Length) & 0xFF);
            b[o+3] = (byte)Type;
            b[o+4] = Flags;
            b[o+5] = (byte)((StreamId >> 24) & 0xFF);
            b[o+6] = (byte)((StreamId >> 16) & 0xFF);
            b[o+7] = (byte)((StreamId >> 8) & 0xFF);
            b[o+8] = (byte)((StreamId) & 0xFF);
        }

        /// <summary>
        /// Determines whether the EndOfStream flag is set on the frame
        /// </summary>
        public bool HasEndOfStreamFlag
        {
            get
            {
                switch (Type)
                {
                    case FrameType.Data:
                        return (Flags & (byte)DataFrameFlags.EndOfStream) != 0;
                    case FrameType.Headers:
                        return (Flags & (byte)HeadersFrameFlags.EndOfStream) != 0;
                    default:
                        return false;
                }
            }
        }

        /// <summary>
        /// Reads a frame header from the given stream
        /// </summary>
        public static async ValueTask<FrameHeader> ReceiveAsync(
            IReadableByteStream stream, byte[] headerSpace)
        {
            await stream.ReadAll(new ArraySegment<byte>(headerSpace, 0, HeaderSize));
            return DecodeFrom(new ArraySegment<byte>(headerSpace, 0, HeaderSize));
        }
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
        public uint StreamDependency;

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

        public const int Size = 5;

        /// <summary>
        /// Encodes the priority data into the given byte array
        /// This must be at least 5 bytes long
        /// </summary>
        public void EncodeInto(ArraySegment<byte> bytes)
        {
            var b = bytes.Array;
            var o = bytes.Offset;

            b[o+0] = (byte)((StreamDependency >> 24) & 0x7F);
            b[o+1] = (byte)((StreamDependency >> 16) & 0xFF);
            b[o+2] = (byte)((StreamDependency >> 8) & 0xFF);
            b[o+3] = (byte)((StreamDependency) & 0xFF);
            if (StreamDependencyIsExclusive) b[o+0] |= 0x80;
            b[o+4] = Weight;
        }

        /// <summary>
        /// Encodes the priority data into the given byte array
        /// This must be at least 5 bytes long
        /// </summary>
        public static PriorityData DecodeFrom(ArraySegment<byte> bytes)
        {
            var b = bytes.Array;
            var o = bytes.Offset;

            var dep = (((uint)b[o + 0] & 0x7F) << 24 )
                | ((uint)b[o + 1] << 16)
                | (uint)(b[o + 2] << 8)
                | (uint)b[o + 3];
            var exclusive = (b[o + 0] & 0x80) != 0;
            var weight = b[o + 4];

            return new PriorityData
            {
                StreamDependency = dep,
                StreamDependencyIsExclusive = exclusive,
                Weight = weight,
            };
        }
    }

    /// <summary>
    /// Valid flags for DATA frames
    /// </summary>
    [Flags]
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
    [Flags]
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
    [Flags]
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
    [Flags]
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
    }

    /// <summary>
    /// Valid flags for PING frames
    /// </summary>
    [Flags]
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
    [Flags]
    public enum ContinuationFrameFlags : byte
    {
        /// <summary>
        /// When set, bit 2 indicates that this frame ends a header block
        /// </summary>
        EndOfHeaders = 0x4,
    }

    /// <summary>
    /// Stores the mapping from frame type to valid flags
    /// </summary>
    public static class FrameFlagsMapping
    {
        static readonly Dictionary<FrameType, Type> flagMap = new Dictionary<FrameType, Type>()
        {
            [FrameType.Data] = typeof(DataFrameFlags),
            [FrameType.Headers] = typeof(HeadersFrameFlags),
            [FrameType.Priority] = null,
            [FrameType.ResetStream] = null,
            [FrameType.Settings] = typeof(SettingsFrameFlags),
            [FrameType.PushPromise] = typeof(PushPromiseFrameFlags),
            [FrameType.Ping] = typeof(PingFrameFlags),
            [FrameType.GoAway] = null,
            [FrameType.WindowUpdate] = null,
            [FrameType.Continuation] = typeof(ContinuationFrameFlags),
        };

        /// <summary>
        /// Returns the enumeration that contains the flags for the given
        /// frame type
        /// </summary>
        /// <param name="ft">The frame type</param>
        public static Type GetFlagType(FrameType ft)
        {
            Type t;
            var gotType = flagMap.TryGetValue(ft, out t);
            if (gotType) return t;
            else return null;
        }
    }

    /// <summary>
    /// Data that is carried inside a window update frame
    /// </summary>
    public struct WindowUpdateData
    {
        public int WindowSizeIncrement;

        public const int Size = 4;

        /// <summary>
        /// Encodes the window update data into the given byte array
        /// This must be at least 4 bytes long
        /// </summary>
        public void EncodeInto(ArraySegment<byte> bytes)
        {
            var b = bytes.Array;
            var o = bytes.Offset;

            b[o+0] = (byte)((WindowSizeIncrement >> 24) & 0xFF);
            b[o+1] = (byte)((WindowSizeIncrement >> 16) & 0xFF);
            b[o+2] = (byte)((WindowSizeIncrement >> 8) & 0xFF);
            b[o+3] = (byte)((WindowSizeIncrement) & 0xFF);
        }

        /// <summary>
        /// Encodes the window update data into the given byte array
        /// This must be at least 4 bytes long
        /// </summary>
        public static WindowUpdateData DecodeFrom(ArraySegment<byte> bytes)
        {
            var b = bytes.Array;
            var o = bytes.Offset;

            var increment =
                ((b[o+0] & 0x7F) << 24) | (b[o+1] << 16) | (b[o+2] << 8) | b[o+3];

            return new WindowUpdateData
            {
                WindowSizeIncrement = increment,
            };
        }
    }

    /// <summary>
    /// Data that is carried inside a reset frame
    /// </summary>
    public struct ResetFrameData
    {
        public ErrorCode ErrorCode;

        public const int Size = 4;

        /// <summary>
        /// Encodes the reset stream data into the given byte array
        /// This must be at least 4 bytes long
        /// </summary>
        public void EncodeInto(ArraySegment<byte> bytes)
        {
            var b = bytes.Array;
            var o = bytes.Offset;
            var ec = (uint)ErrorCode;

            b[o+0] = (byte)((ec >> 24) & 0xFF);
            b[o+1] = (byte)((ec >> 16) & 0xFF);
            b[o+2] = (byte)((ec >> 8) & 0xFF);
            b[o+3] = (byte)((ec) & 0xFF);
        }

        /// <summary>
        /// Encodes the reset stream data into the given byte array
        /// This must be at least 4 bytes long
        /// </summary>
        public static ResetFrameData DecodeFrom(ArraySegment<byte> bytes)
        {
            var b = bytes.Array;
            var o = bytes.Offset;

            var errc =
                ((uint)b[o + 0] << 24)
                | ((uint)b[o + 1] << 16)
                | ((uint)b[o + 2] << 8)
                | (uint)b[o + 3];

            return new ResetFrameData
            {
                ErrorCode = (ErrorCode)errc,
            };
        }
    }

    /// <summary>
    /// Describes the reason why a GoAway was sent
    /// </summary>
    public struct GoAwayReason
    {
        public uint LastStreamId;
        public ErrorCode ErrorCode;
        public ArraySegment<byte> DebugData;
    }

    /// <summary>
    /// Data that is carried inside a GoAway frame
    /// </summary>
    public struct GoAwayFrameData
    {
        public GoAwayReason Reason;

        public int RequiredSize => 8 + Reason.DebugData.Count;

        /// <summary>
        /// Encodes the goaway data into the given byte array
        /// This must be at least RequiredSize bytes long
        /// </summary>
        public void EncodeInto(ArraySegment<byte> bytes)
        {
            var b = bytes.Array;
            var o = bytes.Offset;
            var errc = (uint)Reason.ErrorCode;

            b[o+0] = (byte)((Reason.LastStreamId >> 24) & 0xFF);
            b[o+1] = (byte)((Reason.LastStreamId >> 16) & 0xFF);
            b[o+2] = (byte)((Reason.LastStreamId >> 8) & 0xFF);
            b[o+3] = (byte)((Reason.LastStreamId) & 0xFF);
            b[o+4] = (byte)((errc >> 24) & 0xFF);
            b[o+5] = (byte)((errc >> 16) & 0xFF);
            b[o+6] = (byte)((errc >> 8) & 0xFF);
            b[o+7] = (byte)((errc) & 0xFF);
            Array.Copy(
                Reason.DebugData.Array, Reason.DebugData.Offset,
                b, o + 8,
                Reason.DebugData.Count);
        }

        /// <summary>
        /// Encodes the goaway data into the given byte array
        /// This must be at least 8 bytes long
        /// </summary>
        public static GoAwayFrameData DecodeFrom(ArraySegment<byte> bytes)
        {
            var b = bytes.Array;
            var o = bytes.Offset;

            var lastStreamId =
                (((uint)b[o + 0] & 0x7F) << 24)
                | ((uint)b[o + 1] << 16)
                | ((uint)b[o + 2] << 8)
                | (uint)b[o + 3];
            var errc =
                ((uint)b[o + 4] << 24)
                | ((uint)b[o + 5] << 16)
                | ((uint)b[o + 6] << 8)
                | (uint)b[o + 7];
            var debugData = new ArraySegment<byte>(b, o + 8, bytes.Count - 8);

            return new GoAwayFrameData
            {
                Reason = new GoAwayReason
                {
                    LastStreamId = lastStreamId,
                    ErrorCode = (ErrorCode)errc,
                    DebugData = debugData,
                },
            };
        }
    }
}
