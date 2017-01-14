using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Http2;
using Hpack;

class Program
{
    static void Main(string[] args)
    {
        //var logProvider = new ConsoleLoggerProvider((s, level) => true, true);
        var logProvider = NullLoggerProvider.Instance;
        // Create a TCP socket acceptor
        var listener = new TcpListener(IPAddress.Any, 8888);
        listener.Start();
        Task.Run(() => AcceptTask(listener, logProvider)).Wait();
    }

    static bool AcceptIncomingStream(IStream stream)
    {
        Task.Run(() => HandleIncomingStream(stream));
        return true;
    }

    static byte[] responseBody = Encoding.ASCII.GetBytes("Hello World!");

    static async void HandleIncomingStream(IStream stream)
    {
        try
        {
            // Read the headers
            var headers = await stream.ReadHeaders();
            //Console.WriteLine(
            //    "Method: {0}, Path: {1}",
            //    headers.GetMethod(), headers.GetPath());

            // Read the request body to the end
            await stream.DrainAsync();
            //Console.WriteLine(Encoding.UTF8.GetString(body));

            // Send a response which consists of headers and a payload
            var responseHeaders = new HeaderField[] {
                new HeaderField { Name = ":status", Value = "200" },
                new HeaderField { Name = "abcd", Value = "asdfkljdadfksjllköfds" },
                new HeaderField { Name = "nextone", Value = "asdfkljdadfksjllköfds" },
            };
            await stream.WriteHeaders(responseHeaders, false);
            await stream.WriteAsync(new ArraySegment<byte>(responseBody), true);

            // Request is fully handled here
        }
        catch (Exception e)
        {
            Console.WriteLine("Error during handling request: {0}", e.Message);
            stream.Cancel();
        }
    }

    static async Task AcceptTask(TcpListener listener, ILoggerProvider logProvider)
    {
        var connectionId = 0;

        var settings = Settings.Default;

        while (true)
        {
            // Accept TCP sockets
            var clientSocket = await listener.AcceptSocketAsync();
            clientSocket.NoDelay = true;
            var stream = new NetworkStream(clientSocket, true);
            var wrappedStreams = stream.CreateStreams();

            // Build a HTTP connection on top of the socket
            var http2Con = new Connection(new Connection.Options
            {
                InputStream = wrappedStreams.Reader,
                OutputStream = wrappedStreams.Writer,
                IsServer = true,
                Settings = settings,
                StreamListener = AcceptIncomingStream,
                Logger = logProvider.CreateLogger("HTTP2Conn" + connectionId),
            });

            connectionId++;
        }
    }
}

public static class RequestUtils
{
    public static string GetValue(this IEnumerable<HeaderField> fields, string name)
    {
        return fields.Where(f => f.Name == name).First().Value;
    }

    public static string GetMethod(this IEnumerable<HeaderField> fields)
    {
        return GetValue(fields, ":method");
    }

    public static string GetPath(this IEnumerable<HeaderField> fields)
    {
        return GetValue(fields, ":path");
    }

    public async static Task<byte[]> ReadAllBytesAsync(this IStreamReader stream)
    {
        var buf = new byte[2048];
        var s = new System.IO.MemoryStream();
        var bytesRead = 0;

        try
        {
            while (true)
            {
                var res = await stream.ReadAsync(new ArraySegment<byte>(buf));
                // Copy the read bytes into the memorystream
                if (res.BytesRead != 0)
                {
                    s.Write(buf, 0, res.BytesRead);
                    bytesRead += res.BytesRead;
                }

                if (res.EndOfStream)
                {
                    // Return everything that was read so far
                    // TODO: That might be wrong if this returns the whole backing
                    // buffer and not only the really read data amount
                    return s.ToArray();
                }
            }
        }
        finally
        {
            s.Dispose();
        }
    }

    public async static Task DrainAsync(this IStreamReader stream)
    {
        var buf = new byte[2048];
        var bytesRead = 0;

        while (true)
        {
            var res = await stream.ReadAsync(new ArraySegment<byte>(buf));
            if (res.BytesRead != 0)
            {
                bytesRead += res.BytesRead;
            }

            if (res.EndOfStream)
            {
                return;
            }
        }
    }
}