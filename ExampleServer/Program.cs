using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Http2;
using Hpack;

class Program
{
    static void Main(string[] args)
    {
        var listener = new TcpListener(IPAddress.Any, 8888);
        listener.Start();
        Task.Run(() => AcceptTask(listener)).Wait();
    }

    static bool AcceptIncomingStream(IStream stream)
    {
        Task.Run(() => HandleIncomingStream(stream));
        return true;
    }

    static async Task HandleIncomingStream(IStream stream)
    {
        try
        {
            // Read the headers
            var headers = await stream.ReadHeaders();
            Console.WriteLine(
                "Method: {0}, Path: {1}",
                headers.GetMethod(), headers.GetPath());
            // Read the request body
            var body = await stream.DrainAsync();
            Console.WriteLine(Encoding.UTF8.GetString(body));
        }
        catch (Exception e)
        {
            Console.WriteLine("Error on stream: {0}", e);
        }
    }

    static async Task AcceptTask(TcpListener listener)
    {
        while (true)
        {
            var tcpClient = await listener.AcceptTcpClientAsync();
            Console.WriteLine("accepted a client");
            var stream = tcpClient.GetStream();
            var wrappedStreams = stream.CreateStreams();

            // Build a HTTP connection on top of the socket
            var http2Con = new Connection(new Connection.Options
            {
                InputStream = wrappedStreams.Reader,
                OutputStream = wrappedStreams.Writer,
                IsServer = true,
                Settings = Settings.Default,
                StreamListener = AcceptIncomingStream,
            });
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

    public async static Task<byte[]> DrainAsync(this IStreamReader stream)
    {
        var buf = new byte[2048];
        var s = new System.IO.MemoryStream();
        var bytesRead = 0;

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
}