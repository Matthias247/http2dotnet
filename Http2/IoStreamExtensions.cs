using System;
using System.Threading;
using System.Threading.Tasks;

namespace Http2
{
    public static class IoStreamExtensions
    {
        /// <summary>
        /// Contains the result of a System.IO.Stream#CreateStreams operation
        /// </summary>
        public struct CreateStreamsResult
        {
            /// <summary>The resulting reader</summary>
            public IStreamReader Reader;
            /// <summary>The resulting writer</summary>
            public IStreamWriterCloser Writer;
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
                Reader = wrappedStream,
                Writer = wrappedStream,
            };
        }

        internal class IoStreamWrapper : IStreamReader, IStreamWriterCloser
        {
            private System.IO.Stream stream;

            public IoStreamWrapper(System.IO.Stream stream)
            {
                this.stream = stream;
            }

            public async ValueTask<StreamReadResult> ReadAsync(ArraySegment<byte> buffer)
            {
                if (buffer.Count == 0)
                {
                    throw new Exception("Reading 0 bytes is not supported");
                }

                var res = await stream.ReadAsync(buffer.Array, buffer.Offset, buffer.Count);
                return new StreamReadResult
                {
                    BytesRead = res,
                    EndOfStream = res == 0,
                };
            }

            public ValueTask<object> WriteAsync(ArraySegment<byte> buffer)
            {
                return new ValueTask<object>(
                    stream.WriteAsync(buffer.Array, buffer.Offset, buffer.Count));
           }

            public ValueTask<object> CloseAsync()
            {
                stream.Dispose();
                return new ValueTask<object>(null);
            }
        }
    }
}