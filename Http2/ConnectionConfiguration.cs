using System;
using Http2.Hpack;

namespace Http2
{
    /// <summary>
    /// Configuration options for a Connnection.
    /// This class is immutable and can be shared between an arbitrary
    /// number of connections.
    /// </summary>
    public class ConnectionConfiguration
    {
        /// <summary>
        /// Whether the connection represents the client or server part of
        /// a HTTP/2 connection. True for servers.
        /// </summary>
        public readonly bool IsServer;

        /// <summary>
        /// The function that should be called whenever a new stream is
        /// opened by the remote peer.
        /// The function should return true if it wants to handle the new
        /// stream and false otherwise.
        /// Applications should handle the stream in another Task.
        /// The Task from which this function is called may not be blocked.
        /// </summary>
        public readonly Func<IStream, bool> StreamListener;

        /// <summary>
        /// Strategy for applying huffman encoding on outgoing headers
        /// </summary>
        public readonly HuffmanStrategy? HuffmanStrategy;

        /// <summary>
        /// Allows to override settings for the connection
        /// </summary>
        public readonly Settings Settings;

        internal ConnectionConfiguration(
            bool isServer,
            Func<IStream, bool> streamListener,
            HuffmanStrategy? huffmanStrategy,
            Settings settings)
        {
            this.IsServer = isServer;
            this.StreamListener = streamListener;
            this.HuffmanStrategy = huffmanStrategy;
            this.Settings = settings;
        }
    }

    /// <summary>
    /// A builder for connection configurations.
    /// </summary>
    public class ConnectionConfigurationBuilder
    {
        bool isServer = true;
        Func<IStream, bool> streamListener = null;
        HuffmanStrategy? huffmanStrategy = null;
        Settings settings = Settings.Default;

        /// <summary>
        /// Creates a new Builder for connection configurations.
        /// </summary>
        /// <param name="isServer">
        /// Whether the connection is on the server side (true) or on the
        /// client side (false).
        /// </param>
        public ConnectionConfigurationBuilder(bool isServer)
        {
            this.isServer = isServer;
        }

        /// <summary>
        /// Builds a ConnectionConfiguration from the configured connection
        /// settings.
        /// </summary>
        public ConnectionConfiguration Build()
        {
            if (isServer && streamListener == null)
            {
                throw new Exception(
                    "Server connections must have configured a StreamListener");
            }

            var config = new ConnectionConfiguration(
                isServer: isServer,
                streamListener: streamListener,
                huffmanStrategy: huffmanStrategy,
                settings: settings);

            return config;
        }

        public ConnectionConfigurationBuilder UseStreamListener(
            Func<IStream, bool> streamListener)
        {
            this.streamListener = streamListener;
            return this;
        }

        public ConnectionConfigurationBuilder UseHuffmanStrategy(HuffmanStrategy strategy)
        {
            this.huffmanStrategy = strategy;
            return this;
        }

        public ConnectionConfigurationBuilder UseSettings(Settings settings)
        {
            if (!settings.Valid)
            {
                throw new Exception("Invalid settings");
            }
            this.settings = settings;
            return this;
        }

    }
}