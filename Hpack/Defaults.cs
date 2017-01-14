
namespace Http2.Hpack
{
    /// <summary>
    /// Stores default values for the library
    /// </summary>
    static class Defaults
    {
        /// <summary>The default size of the dynamic table: 4096 bytes</summary>
        public const int DynamicTableSize = 4096;

        /// <summary>The default limit for the dynamic table size: 4096 bytes</summary>
        public const int DynamicTableSizeLimit = 4096;

        /// <summary>Maximum length for received strings</summary>
        public const int MaxStringLength = 1 << 20;
    }
}
