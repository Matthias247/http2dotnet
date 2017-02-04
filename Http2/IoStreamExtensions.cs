using System;
using System.Threading.Tasks;

namespace Http2
{
    /// <summary>
    /// Extension methods for System.IO.Stream
    /// </summary>
    public static class IoStreamExtensions
    {
        /// <summary>
        /// Contains the result of a System.IO.Stream#CreateStreams operation
        /// </summary>
        public struct CreateStreamsResult
        {
            /// <summary>The resulting readable stream</summary>
            public IReadableByteStream ReadableStream;
            /// <summary>The resulting writeable stream</summary>
            public IWriteAndCloseableByteStream WriteableStream;
        }

        /// <summary>
        /// Creates the required stream abstractions on top of a .NET
        /// System.IO.Stream.
        /// The created stream wrappers will take ownership of the stream.
        /// It is not allowed to use the stream directly after this.
        /// </summary>
        public static CreateStreamsResult CreateStreams(this System.IO.Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            var wrappedStream = new IoStreamWrapper(stream);
            return new CreateStreamsResult
            {
                ReadableStream = wrappedStream,
                WriteableStream = wrappedStream,
            };
        }

        internal class IoStreamWrapper : IReadableByteStream, IWriteAndCloseableByteStream
        {
            private readonly System.IO.Stream stream;

            public IoStreamWrapper(System.IO.Stream stream)
            {
                this.stream = stream;
            }

            public ValueTask<StreamReadResult> ReadAsync(ArraySegment<byte> buffer)
            {
                if (buffer.Count == 0)
                {
                    throw new Exception("Reading 0 bytes is not supported");
                }

                var readTask = stream.ReadAsync(buffer.Array, buffer.Offset, buffer.Count);
                Task<StreamReadResult> transformedTask = readTask.ContinueWith(tt =>
                {
                    if (tt.Exception != null)
                    {
                        throw tt.Exception;
                    }

                    var res = tt.Result;
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
                return stream.WriteAsync(buffer.Array, buffer.Offset, buffer.Count);
            }

            public Task CloseAsync()
            {
                stream.Dispose();
                return Task.CompletedTask;
            }
        }
    }
}