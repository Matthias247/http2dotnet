
namespace Http2.Hpack
{
    /// <summary>
    /// An entry in the static or dynamic table
    /// </summary>
    public struct TableEntry
    {
        public string Name;
        public int NameLen;
        public string Value;
        public int ValueLen;
    }
}