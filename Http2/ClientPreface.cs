using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Http2
{
    /// <summary>
    /// Tools for working with the HTTP/2 client connection preface
    /// </summary>
    public static class ClientPreface
    {
        /// <summary>Contains the connection preface in string format</summary>
        public const string String = "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n";

        /// <summary>Contains the connection preface as bytes</summary>
        public static readonly byte[] Bytes = Encoding.ASCII.GetBytes(ClientPreface.String);

        /// <summary>The length of the preface</summary>
        public static int Length
        {
            get { return Bytes.Length; }
        }

        /// <summary>
        /// Writes the preface to the given stream
        /// </summary>
        public static Task WriteAsync(IWriteableByteStream stream)
        {
            return stream.WriteAsync(new ArraySegment<byte>(Bytes));
        }

        /// <summary>
        /// Reads the preface from the given stream and compares it to
        /// the expected value.
        /// Will throw an error if the preface could not be read or if the stream
        /// has finished unexpectedly.
        /// </summary>
        public static async ValueTask<DoneHandle> ReadAsync(IReadableByteStream stream)
        {
            var buffer = new byte[Length];
            await stream.ReadAll(new ArraySegment<byte>(buffer));

            // Compare with the expected preface
            for (var i = 0; i < buffer.Length; i++)
            {
                if (buffer[i] != Bytes[i])
                {
                    throw new Exception("Invalid prefix received");
                }
            }

            return DoneHandle.Instance;
        }

        /// <summary>
        /// Reads the preface from the given stream and compares it to
        /// the expected value.
        /// Will throw an error if the preface could not be read or if the stream
        /// has finished unexpectedly.
        /// </summary>
        public static async ValueTask<DoneHandle> ReadAsync(
            IReadableByteStream stream, int timeoutMillis)
        {
            if (timeoutMillis < 0) throw new ArgumentException(nameof(timeoutMillis));
            else if (timeoutMillis == 0)
            {
                // No timeout
                return await ReadAsync(stream);
            }

            var cts = new CancellationTokenSource();
            var readTask = ReadAsync(stream).AsTask();
            var timerTask = Task.Delay(timeoutMillis, cts.Token);

            var finishedTask = await Task.WhenAny(readTask, timerTask);
            var hasTimeout = ReferenceEquals(timerTask, finishedTask);
            // Cancel the timer which might be still running
            cts.Cancel();
            cts.Dispose();

            if (hasTimeout) throw new TimeoutException();
            // No timeout occured
            return readTask.Result;
        }
    }
}