using System;

namespace Http2
{
    /// <summary>
    /// Settings that are defined in the HTTP/2 specification
    /// </summary>
    public enum SettingId : ushort
    {
        HeaderTableSize = 0x1,
        EnablePush = 0x2,
        MaxConcurrentStreams = 0x3,
        InitialWindowSize = 0x4,
        MaxFrameSize = 0x5,
        MaxHeaderListSize = 0x6,
    }

    /// <summary>
    /// Structure that stores all known settings for HTTP/2 connections.
    /// </summary>
    public struct Settings
    {
        /// <summary>
        /// Allows the sender to inform the remote endpoint of the maximum size
        /// of the header compression table used to decode header blocks, in octets.
        /// </summary>
        public uint HeaderTableSize;

        /// <summary>
        /// This setting can be used to disable server push
        /// </summary>
        public bool EnablePush;

        /// <summary>
        /// Indicates the maximum number of concurrent streams that the sender
        /// will allow.
        /// </summary>
        public uint MaxConcurrentStreams;

        /// <summary>
        /// Indicates the sender's initial window size (in octets) for
        /// stream-level flow control
        /// </summary>
        public uint InitialWindowSize;

        /// <summary>
        /// Indicates the size of the largest frame payload that the sender is
        /// willing to receive, in octets
        /// </summary>
        public uint MaxFrameSize;

        /// <summary>
        /// This advisory setting informs a peer of the maximum size of header
        /// list that the sender is prepared to accept, in octets
        /// </summary>
        public uint MaxHeaderListSize;

        /// <summary>
        /// Contains the default settings
        /// </summary>
        public readonly static Settings Default = new Settings
        {
            HeaderTableSize = 4096,
            EnablePush = true,
            MaxConcurrentStreams = uint.MaxValue,
            InitialWindowSize = 65535,
            MaxFrameSize = 16384,
            MaxHeaderListSize = uint.MaxValue,
        };

        /// <summary>
        /// Contains the minimum value for all settings
        /// </summary>
        public readonly static Settings Min = new Settings
        {
            HeaderTableSize = 0,
            EnablePush = false,
            MaxConcurrentStreams = 0,
            InitialWindowSize = 0,
            MaxFrameSize = 16384,
            MaxHeaderListSize = 0,
        };

        /// <summary>
        /// Contains the maximum value for all settings
        /// </summary>
        public readonly static Settings Max = new Settings
        {
            HeaderTableSize = uint.MaxValue,
            EnablePush = true,
            MaxConcurrentStreams = uint.MaxValue,
            InitialWindowSize = int.MaxValue,
            MaxFrameSize = 16777215,
            MaxHeaderListSize = uint.MaxValue,
        };

        private void EncodeSingleSetting(
            ushort id, uint value, byte[] buffer, int offset)
        {
            // Write 16 bit ID
            buffer[offset+0] = (byte)((id >> 8) & 0xFF);
            buffer[offset+1] = (byte)((id) & 0xFF);

            // Write 32bit value
            buffer[offset+2] = (byte)((value >> 24) & 0xFF);
            buffer[offset+3] = (byte)((value >> 16) & 0xFF);
            buffer[offset+4] = (byte)((value >> 8) & 0xFF);
            buffer[offset+5] = (byte)((value) & 0xFF);
        }

        /// <summary>
        /// Returns the required size for encoding all settings into the body
        /// of a SETTINGS frame.
        /// </summary>
        public int RequiredSize => 6*6;

        /// <summary>
        /// Encodes the settings into a byte array.
        /// This must be long enough to hold all settings (36 bytes).
        /// </summary>
        public void EncodeInto(ArraySegment<byte> bytes)
        {
            var b = bytes.Array;
            var o = bytes.Offset;
            
            EncodeSingleSetting(
                (ushort)SettingId.EnablePush, EnablePush ? 1u : 0u, b, o);
            o += 6;
            EncodeSingleSetting(
                (ushort)SettingId.HeaderTableSize, HeaderTableSize, b, o);
            o += 6;
            EncodeSingleSetting(
                (ushort)SettingId.InitialWindowSize, InitialWindowSize, b, o);
            o += 6;
            EncodeSingleSetting(
                (ushort)SettingId.MaxConcurrentStreams, MaxConcurrentStreams, b, o);
            o += 6;
            EncodeSingleSetting(
                (ushort)SettingId.MaxFrameSize, MaxFrameSize, b, o);
            o += 6;
            EncodeSingleSetting(
                (ushort)SettingId.MaxHeaderListSize, MaxHeaderListSize, b, o);
        }

        /// <summary>
        /// Updates a Settings struct from received setting values.
        /// This method assumes that the data has a valid length (multiple of
        /// 6 bytes).
        /// </summary>
        /// <param name="data">The data which contains the updates</param>
        public Http2Error? UpdateFromData(ArraySegment<byte> data)
        {
            if (data.Count % 6 != 0)
            {
                return new Http2Error
                {
                    StreamId = 0,
                    Code = ErrorCode.ProtocolError,
                    Message = "Invalid SETTINGS frame length",
                };
            }

            var b = data.Array;
            for (var o = data.Offset; o < data.Offset + data.Count; o = o+6)
            {
                // Extract ID and value
                var id =(ushort)(
                    (b[o + 0] << 8)
                    | (ushort)b[o + 1]);
                var value =
                    ((uint)b[o + 2] << 24)
                    | ((uint)b[o + 3] << 16)
                    | ((uint)b[o + 4] << 8)
                    | (uint)b[o + 5];

                // Update the value
                var err = this.UpdateFieldById(id, value);
                if (err != null)
                {
                    return err;
                }
            }

            return null;
        }

        /// <summary>
        /// Indicates whether settings are within valid bounds
        /// </summary>
        public bool Valid
        {
            get
            {
                if (HeaderTableSize < Min.HeaderTableSize ||
                    HeaderTableSize > Max.HeaderTableSize)
                {
                    return false;
                }
                if (InitialWindowSize < Min.InitialWindowSize ||
                    InitialWindowSize > Max.InitialWindowSize)
                {
                    return false;
                }
                if (MaxConcurrentStreams < Min.MaxConcurrentStreams ||
                    MaxConcurrentStreams > Max.MaxConcurrentStreams)
                {
                    return false;
                }
                if (MaxFrameSize < Min.MaxFrameSize ||
                    MaxFrameSize > Max.MaxFrameSize)
                {
                    return false;
                }
                if (MaxHeaderListSize < Min.MaxHeaderListSize ||
                    MaxHeaderListSize > Max.MaxHeaderListSize)
                {
                    return false;
                }
                return true;
            }
        }

        /// <summary>
        /// Modifies the value of setting with the given ID to the new value.
        /// </summary>
        /// <param name="id">The ID of the setting to modify</param>
        /// <param name="value">The new value of the setting</param>
        /// <returns>An error value if the new setting value is not valid</returns>
        public Http2Error? UpdateFieldById(ushort id, uint value)
        {
            switch ((SettingId)id)
            {
                case SettingId.EnablePush:
                    if (value == 0 || value == 1)
                    {
                        EnablePush = value == 1 ? true : false;
                        return null;
                    }
                    break;
                case SettingId.HeaderTableSize:
                    if (value >= Settings.Min.HeaderTableSize &&
                        value <= Settings.Max.HeaderTableSize)
                    {
                        HeaderTableSize = value;
                        return null;
                    }
                    break;
                case SettingId.InitialWindowSize:
                    if (value >= Settings.Min.InitialWindowSize &&
                        value <= Settings.Max.InitialWindowSize)
                    {
                        InitialWindowSize = value;
                        return null;
                    }
                    break;
                case SettingId.MaxConcurrentStreams:
                    if (value >= Settings.Min.MaxConcurrentStreams &&
                        value <= Settings.Max.MaxConcurrentStreams)
                    {
                        MaxConcurrentStreams = value;
                        return null;
                    }
                    break;
                case SettingId.MaxFrameSize:
                    if (value >= Settings.Min.MaxFrameSize &&
                        value <= Settings.Max.MaxFrameSize)
                    {
                        MaxFrameSize = value;
                        return null;
                    }
                    break;
                case SettingId.MaxHeaderListSize:
                    if (value >= Settings.Min.MaxHeaderListSize &&
                        value <= Settings.Max.MaxHeaderListSize)
                    {
                        MaxHeaderListSize = value;
                        return null;
                    }
                    break;
                default:
                    // Ignore unknown settings
                    return null;
            }

            // We only get here if the setting is out of bounds.
            // In other cases the function will return early.

            // For InitialWindowSize we must return a FlowControlError according
            // to the spec, for other settings a ProtocolError
            var code = ErrorCode.ProtocolError;
            if ((SettingId)id == SettingId.InitialWindowSize)
            {
                code = ErrorCode.FlowControlError;
            }

            return new Http2Error
            {
                StreamId = 0,
                Code = code,
                Message = "Invalid value " + value + " for setting with ID " + id,
            };
        }
    }
}
