
namespace Http2
{
    /// <summary>
    /// Default values for HTTP/2
    /// </summary>
    static class Defaults
    {
        /// <summary>
        /// Default value for the maximum encoder HPACK headertable size.
        /// HPACK won't use more than this size for the encoder table even if
        /// the remote announces a bigger size through Settings.
        /// The default size of the dynamic table: 4096 bytes
        /// </summary>
        public const int MaxEncoderHeaderTableSize = 4096;

        /// <summary>
        /// The timeout until which we expect to get an ACK for our settings
        /// </summary>
        public const int SettingsTimeout = 5000;
    }
}
