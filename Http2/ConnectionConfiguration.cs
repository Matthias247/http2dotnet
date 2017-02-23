using System;
using System.Buffers;
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
        /// </summary>
        public readonly Func<IStream, bool> StreamListener;

        /// <summary>
        /// Strategy for applying huffman encoding on outgoing headers.
        /// </summary>
        public readonly HuffmanStrategy? HuffmanStrategy;

        /// <summary>
        /// The time to wait for a Client Preface on startup at server side.
        /// </summary>
        public readonly int ClientPrefaceTimeout;

        /// <summary>
        /// The HTTP/2 settings which will be utilized for the connection.
        /// </summary>
        public readonly Settings Settings;

        /// <summary>
        /// The buffer pool which will be utilized for allocating send and
        /// receive buffers.
        /// </summary>
        public readonly ArrayPool<byte> BufferPool;

        internal ConnectionConfiguration(
            bool isServer,
            Func<IStream, bool> streamListener,
            HuffmanStrategy? huffmanStrategy,
            Settings settings,
            ArrayPool<byte> bufferPool,
            int clientPrefaceTimeout)
        {
            this.IsServer = isServer;
            this.StreamListener = streamListener;
            this.HuffmanStrategy = huffmanStrategy;
            this.Settings = settings;
            this.BufferPool = bufferPool;
            this.ClientPrefaceTimeout = clientPrefaceTimeout;
        }
    }

    /// <summary>
    /// A builder for connection configurations.
    /// </summary>
    public class ConnectionConfigurationBuilder
    {
        private const int DefaultClientPrefaceTimeout = 1000;

        bool isServer = true;
        Func<IStream, bool> streamListener = null;
        HuffmanStrategy? huffmanStrategy = null;
        Settings settings = Settings.Default;
        ArrayPool<byte> bufferPool = null;
        int clientPrefaceTimeout = DefaultClientPrefaceTimeout;

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

            var pool = bufferPool;
            if (pool == null) pool = ArrayPool<byte>.Shared;

            var config = new ConnectionConfiguration(
                isServer: isServer,
                streamListener: streamListener,
                huffmanStrategy: huffmanStrategy,
                settings: settings,
                bufferPool: pool,
                clientPrefaceTimeout: clientPrefaceTimeout);

            return config;
        }

        /// <summary>
        /// Configures the function that should be called whenever a new stream
        /// is opened by the remote peer. This function must be configured for
        /// server configurations.
        /// The function should return true if it wants to handle the new
        /// stream and false otherwise.
        /// Applications should handle the stream in another Task.
        /// The Task from which this function is called may not be blocked.
        /// </summary>
        public ConnectionConfigurationBuilder UseStreamListener(
            Func<IStream, bool> streamListener)
        {
            this.streamListener = streamListener;
            return this;
        }

        /// <summary>
        /// Configures the strategy for applying huffman encoding on outgoing
        /// headers.
        /// </summary>
        public ConnectionConfigurationBuilder UseHuffmanStrategy(HuffmanStrategy strategy)
        {
            this.huffmanStrategy = strategy;
            return this;
        }

        /// <summary>
        /// Allows to override the HTTP/2 settings for the connection.
        /// If not explicitely specified the default HTTP/2 settings,
        /// which are stored within Settings.Default, will be utilized.
        /// </summary>
        public ConnectionConfigurationBuilder UseSettings(Settings settings)
        {
            if (!settings.Valid)
            {
                throw new Exception("Invalid settings");
            }
            this.settings = settings;
            return this;
        }

        /// <summary>
        /// Configures a buffer pool which will be utilized for allocating send
        /// and receive buffers.
        /// If this is not explicitely configured ArrayPool&lt;byte&gt;.Shared
        /// will be used.
        /// </summary>
        /// <param name="pool">The buffer pool to utilize</param>
        public ConnectionConfigurationBuilder UseBufferPool(ArrayPool<byte> pool)
        {
            if (pool == null) throw new ArgumentNullException(nameof(pool));
            this.bufferPool = pool;
            return this;
        }

        /// <summary>
        /// Allows to override the time time wait for a Client Preface on startup
        /// at server side.
        /// </summary>
        /// <param name="timeout">
        /// The time to wait for a client preface.
        /// The time must be configured in milliseconds and must be bigger than 0.
        /// </param>
        public ConnectionConfigurationBuilder UseClientPrefaceTimeout(int timeout)
        {
            if (timeout <= 0) throw new ArgumentException(nameof(timeout));
            this.clientPrefaceTimeout = timeout;
            return this;
        }
    }
}