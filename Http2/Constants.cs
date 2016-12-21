using System;
using System.Text;

namespace Http2
{
    /// <summary>
    /// Contains constant values for HTTP/2
    /// </summary>
    static class Constants
    {
        /// <summary>
        /// The HTTP/2 connection preface
        /// </summary>
        public const string ConnectionPreface =
            "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n";

        /// <summary>The HTTP/2 connection preface bytes</summary>
        public static readonly byte[] ConnectionPrefaceBytes =
            Encoding.ASCII.GetBytes(ConnectionPreface);

        /// <summary>An empty array segment</summery>
        public static readonly ArraySegment<byte> EmptyByteArray =
            new ArraySegment<byte>(new byte[0]);

        /// <summary>The initial flow control window for connections</summary>
        public const int InitialConnectionWindowSize = 65535;
    }
}
