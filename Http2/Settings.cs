using System;
using System.Collections.Generic;
using System.Text;

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

    internal struct SettingMetaData
    {
        /// <summary>
        /// Initial values for the setting as defined in the HTTP/2 specification
        /// </summary>
        public uint InitialValue;

        /// <summary>
        /// Minimum values for the setting as defined in the HTTP/2 specification
        /// </summary>
        public uint MinValue;

        /// <summary>
        /// Maximum values for the setting as defined in the HTTP/2 specification
        /// </summary>
        public uint MaxValue;
    }

    /// <summary>
    /// Structure that stores all known settings for HTTP/2 connections
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
            MaxConcurrentStreams = 1,
            InitialWindowSize = 1,
            MaxFrameSize = 16384,
            MaxHeaderListSize = 1,
        };

        /// <summary>
        /// Contains the maximum value for all settings
        /// </summary>
        public readonly static Settings Max = new Settings
        {
            HeaderTableSize = uint.MaxValue,
            EnablePush = true,
            MaxConcurrentStreams = uint.MaxValue,
            InitialWindowSize = uint.MaxValue,
            MaxFrameSize = 16777215,
            MaxHeaderListSize = uint.MaxValue,
        };
    }

    public struct Setting
    {
        /// <summary>ID of the setting</summary>
        public SettingId ID;
        /// <summary>Value of the setting</summary>
        public uint Value;
    }

    /// <summary>
    /// Utilities for working with SETTINGS frames and their data
    /// </summary>
    public static class SettingTools
    {
        private static SettingMetaData MakeSettingMetaData(
          uint InitialValue, uint MinValue, uint MaxValue
        )
        {
            return new SettingMetaData
            {
                InitialValue = InitialValue,
                MinValue = MinValue,
                MaxValue = MaxValue,
            };
        }
        
        /// <summary>
        /// Initial, Minimal and Maximum values for the settings as defined in
        /// the HTTP/2 specification
        /// </summary>
        private static readonly Dictionary<SettingId, SettingMetaData> Boundaries =
            new Dictionary<SettingId, SettingMetaData>
        {
            [SettingId.HeaderTableSize] = MakeSettingMetaData(4096, 0, uint.MaxValue),
            [SettingId.EnablePush] = MakeSettingMetaData(1, 0, 1),
            [SettingId.MaxConcurrentStreams] = MakeSettingMetaData(uint.MaxValue, 1, uint.MaxValue),
            [SettingId.InitialWindowSize] = MakeSettingMetaData(65535, 1, int.MaxValue),
            [SettingId.MaxFrameSize] = MakeSettingMetaData(16384, 16384, 16777215),
            [SettingId.MaxHeaderListSize] = MakeSettingMetaData(uint.MaxValue, 0, uint.MaxValue),
        };

        /// <summary>Returns the initial value of the setting with the given ID</summary>
        public static uint? GetInitialValue(SettingId id)
        {
            SettingMetaData si;
            if (Boundaries.TryGetValue(id, out si))
            {
                return si.InitialValue;
            }
            return null;
        }

        /// <summary>
        /// Creates a map of default settings as defined in the HTTP/2 specification
        /// </summary>
        public static Dictionary<SettingId, Setting> CreateDefaultSettings()
        {
            var result = new Dictionary<SettingId, Setting>();
            foreach (var b in Boundaries)
            {
                var id = b.Key;
                var val = b.Value.InitialValue;
                result[id] = new Setting { ID = id, Value = val };
            }
            return result;
        }

        /// <summary>
        /// Validates whether a setting is in a known boundary.
        /// Returns an error if the setting is invalid or null if ok.
        /// </summary>
        public static Http2Error? ValidateSetting(ushort id, uint value)
        {
            SettingMetaData meta;
            if (!Boundaries.TryGetValue((SettingId)id, out meta))
            {
                // Ignore unknown settings
                return null;
            }

            var hasErr = false;
            if (value < meta.MinValue) hasErr = true;
            if (value > meta.MaxValue) hasErr = true;
            if (!hasErr) return null;

            // For InitialWindowSize we must return a FlowControlError according
            // to the spec, for other settings a ProtocolError
            var code = ErrorCode.ProtocolError;
            if ((SettingId)id == SettingId.InitialWindowSize)
            {
                code = ErrorCode.FlowControlError;
            }

            return new Http2Error
            {
                Type = ErrorType.ConnectionError,
                Code = code,
                Message = "Invalid value " + value + " for setting with ID " + id,
            };
        }

        /// <summary>
        /// Validates all settings in a SettingMap to confirm to their known boundaries.
        /// Returns an error if settings are invalid or null if ok.
        /// </summary>
        public static Http2Error? ValidateSettings(Dictionary<ushort, Setting> settings)
        {
            Http2Error? err = null;
            // Iterate through map until first error occurs
            foreach (var s in settings)
            {
                err = ValidateSetting(s.Key, s.Value.Value);
                if (err.HasValue) break;
            };
            return err;
        }
    }
}
