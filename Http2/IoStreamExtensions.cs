using System;
using System.Threading;
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

            public ValueTask<object> WriteAsync(ArraySegment<byte> buffer)
            {
                return new ValueTask<object>(
                    stream.WriteAsync(buffer.Array, buffer.Offset, buffer.Count));
           }

            public ValueTask<object> CloseAsync()
            {
                stream.Dispose();
                return new ValueTask<object>(DoneTask.Instance);
            }
        }
    }
}