
namespace Http2.Hpack
{
    /// <summary>
    /// An HTTP/2 header field
    /// </summary>
    public struct HeaderField
    {
        /// <summary>
        /// The name of the header field
        /// </summary>
        public string Name;

        /// <summary>
        /// The value of the header field
        /// </summary>
        public string Value;

        /// <summary>
        /// Whether the contained information is sensitive
        /// and should not be cached.
        /// This defaults to false if not set.
        /// </summary>
        public bool Sensitive;
    }
}
