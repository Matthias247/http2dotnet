using System;
using System.Collections.Generic;

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
            /// The stream which is used for receiving data
            /// </summary>
            public IStreamReader InputStream;

            /// <summary>
            /// The stream which is used for writing data
            /// </summary>
            public IStreamWriterCloser OutputStream;

            /// <summary>
            /// Whether the connection represents the client or server part of
            /// a HTTP/2 connection. True for servers.
            /// </summary>
            public bool IsServer;

            /// <summary>
            /// The function that should be called whenever a new stream is
            /// opened by the remote peer.
            /// </summary>
            public Action<Object> StreamListener;

            /// <summary>
            /// Strategy for applying huffman encoding on outgoing headers
            /// </summary>
            public HuffmanStrategy? HuffmanStrategy;

            /// <summary>
            /// Allows to override settings for the connection
            /// </summary>
            public Dictionary<SettingId, Setting> Settings;
        }

        internal ConnectionWriter Writer;
        internal HeaderReader HeaderReader;
        internal Settings LocalSettings;
        internal Settings RemoteSettings;

        /// <summary>
        /// Whether the connection represents the client or server part of
        /// a HTTP/2 connection. True for servers.
        /// </summary>
        public readonly bool IsServer;

        /// <summary>
        /// Unregisters a stream from the map of streams that are managed
        /// by this connection.
        /// </summary>
        /// <param name="stream">The stream to unregister</param>
        internal void UnregisterStream(StreamImpl stream)
        {
            // TODO: Implement me
            throw new NotImplementedException();
        }

        // TODO: Somewhere we need to send window update frames for the connection
    }
}