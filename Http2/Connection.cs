using System;
using System.Collections.Generic;
using System.Text;

using Hpack;

namespace Http2
{
    /// <summary>
    /// A HTTP/2 connection
    /// </summary>
    public class Connection
    {
        /// <summary>
        /// Options for creating a HTTP/2 connection
        /// </summary>
        public struct Options
        {
            /// <summary>
            /// Whether the connection represents the client or server part of
            /// a HTTP/2 connection. True for servers.
            /// </summary>
            bool IsServer;

            /// <summary>
            /// The stream which is used to send and receive data.
            /// Must not be in closed state
            /// </summary>
            Object Stream;

            /// <summary>
            /// The function that should be called whenever a new stream is
            /// opened by the remote peer.
            /// </summary>
            Action<Object> StreamListener;

            /// <summary>
            /// Strategy for applying huffman encoding on outgoing headers
            /// </summary>
            HuffmanStrategy? HuffmanStrategy;

            /// <summary>
            /// Allows to override settings for the connection
            /// </summary>
            Dictionary<SettingId, Setting> Settings;
        }
    }
}