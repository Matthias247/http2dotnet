using System;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Http2
{
    /// <summary>
    /// Extension methods for System.Net.Sockets
    /// </summary>
    public static class SocketExtensions
    {
        /// <summary>
        /// Contains the result of a System.Net.Socket#CreateStreams operation
        /// </summary>
        public struct CreateStreamsResult
        {
            /// <summary>The resulting readable stream</summary>
            public IReadableByteStream ReadableStream;
            /// <summary>The resulting writable stream</summary>
            public IWriteAndCloseableByteStream WriteableStream;
        }

        /// <summary>
        /// Creates the required stream abstractions on top of a .NET
        /// Socket.
        /// The created stream wrappers will take ownership of the stream.
        /// It is not allowed to use the socket directly after this.
        /// </summary>
        public static CreateStreamsResult CreateStreams(this Socket socket)
        {
            if (socket == null) throw new ArgumentNullException(nameof(socket));
            var wrappedStream = new SocketWrapper(socket);
            return new CreateStreamsResult
            {
                ReadableStream = wrappedStream,
                WriteableStream = wrappedStream,
            };
        }

        internal class SocketWrapper : IReadableByteStream, IWriteAndCloseableByteStream
        {
            private Socket socket;
            /// <summary>
            /// Whether a speculative nonblocking read should by tried the next
            /// time instead of directly using an ReadAsync method.
            /// </summary>
            private bool tryNonBlockingRead = false;

            public SocketWrapper(Socket socket)
            {
                this.socket = socket;
                // Switch socket into nonblocking mode
                socket.Blocking = false;
            }

            public ValueTask<StreamReadResult> ReadAsync(ArraySegment<byte> buffer)
            {
                if (buffer.Count == 0)
                {
                    throw new Exception("Reading 0 bytes is not supported");
                }

                var offset = buffer.Offset;
                var count = buffer.Count;

                if (tryNonBlockingRead)
                {
                    // Try a nonblocking read if the last read yielded all required bytes
                    // This means there are most likely bytes left in the socket buffer
                    SocketError ec;
                    var rcvd = socket.Receive(buffer.Array, offset, count, SocketFlags.None, out ec);
                    if (ec != SocketError.Success &&
                        ec != SocketError.WouldBlock &&
                        ec != SocketError.TryAgain)
                    {
                        return new ValueTask<StreamReadResult>(
                            Task.FromException<StreamReadResult>(
                                new SocketException((int)ec)));
                    }

                    if (rcvd != count)
                    {
                        // Socket buffer seems empty
                        // Use an async read next time
                        tryNonBlockingRead = false;
                    }

                    if (ec == SocketError.Success)
                    {
                        return new ValueTask<StreamReadResult>(
                            new StreamReadResult
                            {
                                BytesRead = rcvd,
                                EndOfStream = rcvd == 0,
                            });
                    }

                    // In the other case we got EAGAIN, which means we try
                    // an async read now
                    // Assert that we have nothing read yet - otherwise the
                    // logic here would be broken
                    if (rcvd != 0)
                    {
                        throw new Exception(
                            "Unexpected reception of data in TryAgain case");
                    }
                }

                var readTask = socket.ReceiveAsync(buffer, SocketFlags.None);
                Task<StreamReadResult> transformedTask = readTask.ContinueWith(tt =>
                {
                    if (tt.Exception != null)
                    {
                        throw tt.Exception;
                    }

                    var res = tt.Result;
                    if (res == count)
                    {
                        // All required data was read
                        // Try a speculative nonblocking read next time
                        tryNonBlockingRead = true;
                    }

                    return new StreamReadResult
                    {
                        BytesRead = res,
                        EndOfStream = res == 0,
                    };
                });

                return new ValueTask<StreamReadResult>(transformedTask);
            }

            public Task WriteAsync(ArraySegment<byte> buffer)
            {
                if (buffer.Count == 0)
                {
                    return Task.CompletedTask;
                }

                // Try a nonblocking write first
                SocketError ec;
                var sent = socket.Send(
                    buffer.Array, buffer.Offset, buffer.Count, SocketFlags.None, out ec);

                if (ec != SocketError.Success && 
                    ec != SocketError.WouldBlock &&
                    ec != SocketError.TryAgain)
                {
                    throw new SocketException((int)ec);
                }

                if (sent == buffer.Count)
                {
                    return Task.CompletedTask;
                }

                // More data needs to be sent
                var remaining = new ArraySegment<byte>(
                    buffer.Array, buffer.Offset + sent, buffer.Count - sent);

                return socket.SendAsync(remaining, SocketFlags.None);
            }

            public Task CloseAsync()
            {
                socket.Dispose();
                return Task.CompletedTask;
            }
        }
    }
}