using System;

namespace Http2
{
    /// <summary>
    /// Contains constant values for HTTP/2
    /// </summary>
    static class Constants
    {
        /// <summary>An empty array segment</summery>
        public static readonly ArraySegment<byte> EmptyByteArray =
            new ArraySegment<byte>(new byte[0]);

        /// <summary>The initial flow control window for connections</summary>
        public const int InitialConnectionWindowSize = 65535;
    }
}
