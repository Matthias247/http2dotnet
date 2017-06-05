using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Http2;
using Http2.Hpack;

class Program
{
    static void Main(string[] args)
    {
        var logProvider = new ConsoleLoggerProvider((s, level) => true, true);
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

    static byte[] UpgradeSuccessResponse = Encoding.ASCII.GetBytes(
        "HTTP/1.1 101 Switching Protocols\r\n" +
        "Connection: Upgrade\r\n" +
        "Upgrade: h2c\r\n\r\n");

    static byte[] UpgradeErrorResponse = Encoding.ASCII.GetBytes(
        "HTTP/1.1 400 Bad request\r\n\r\n");

    static byte[] responseBody = Encoding.ASCII.GetBytes(
        "<html><head>Hello World</head><body>Content</body></html>");

    static byte[] http2Start = Encoding.ASCII.GetBytes(
        "PRI * HTTP/2.0\r\n\r\n");

    static bool MaybeHttpStart(ArraySegment<byte> bytes)
    {
        if (bytes == null || bytes.Count != http2Start.Length) return false;
        for (var i = 0; i < http2Start.Length; i++)
        {
            if (bytes.Array[bytes.Offset+i] != http2Start[i]) return false;
        }
        return true;
    }

    static async void HandleIncomingStream(IStream stream)
    {
        try
        {
            // Read the headers
            var headers = await stream.ReadHeadersAsync();
            var method = headers.First(h => h.Name == ":method").Value;
            var path = headers.First(h => h.Name == ":path").Value;
            // Print the request method and path
            Console.WriteLine("Method: {0}, Path: {1}", method, path);

            // Read the request body and write it to console
            var buf = new byte[2048];
            while (true)
            {
                var readResult = await stream.ReadAsync(new ArraySegment<byte>(buf));
                if (readResult.EndOfStream) break;
                // Print the received bytes
                Console.WriteLine(Encoding.ASCII.GetString(buf, 0, readResult.BytesRead));
            }

            // Send a response which consists of headers and a payload
            var responseHeaders = new HeaderField[] {
                new HeaderField { Name = ":status", Value = "200" },
                new HeaderField { Name = "content-type", Value = "text/html" },
            };
            await stream.WriteHeadersAsync(responseHeaders, false);
            await stream.WriteAsync(new ArraySegment<byte>(
                responseBody), true);

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
        settings.MaxConcurrentStreams = 50;

        var config =
            new ConnectionConfigurationBuilder(true)
            .UseStreamListener(AcceptIncomingStream)
            .UseSettings(settings)
            .UseHuffmanStrategy(HuffmanStrategy.IfSmaller)
            .Build();

        var useUpgrade = true;

        while (true)
        {
            // Accept TCP sockets
            var clientSocket = await listener.AcceptSocketAsync();
            clientSocket.NoDelay = true;
            // Create HTTP/2 stream abstraction on top of the socket
            var wrappedStreams = clientSocket.CreateStreams();
            // Alternatively on top of a System.IO.Stream
            //var netStream = new NetworkStream(clientSocket, true);
            //var wrappedStreams = netStream.CreateStreams();
            await HandleConnection(
                logProvider,
                config,
                useUpgrade,
                wrappedStreams.ReadableStream,
                wrappedStreams.WriteableStream,
                connectionId);

            connectionId++;
        }
    }

    static async Task HandleConnection(
        ILoggerProvider logProvider,
        ConnectionConfiguration config,
        bool useUpgrade,
        IReadableByteStream inputStream,
        IWriteAndCloseableByteStream outputStream,
        int connectionId)
    {
        var upgradeReadStream = new UpgradeReadStream(inputStream);
        ServerUpgradeRequest upgrade = null;
        try
        {
            // Wait for either HTTP/1 upgrade header or HTTP/2 magic header
            await upgradeReadStream.WaitForHttpHeader();
            var headerBytes = upgradeReadStream.HeaderBytes;
            if (MaybeHttpStart(headerBytes))
            {
                // This seems to be a HTTP/2 request
                // No upgrade necessary
                // Make the header rereadable by the stream reader consumer,
                // so that the library can also read the preface
                upgradeReadStream.UnreadHttpHeader();
            }
            else
            {
                // This seems to be a HTTP/1 request
                // Parse the header and check whether it's an upgrade
                var request = Http1Request.ParseFrom(
                    Encoding.ASCII.GetString(
                        headerBytes.Array, headerBytes.Offset, headerBytes.Count-4));
                // Assure that the HTTP/2 library does not get passed the HTTP/1 request
                upgradeReadStream.ConsumeHttpHeader();

                if (request.Protocol != "HTTP/1.1")
                    throw new Exception("Invalid upgrade request");

                // If the request has some payload we can't process it in this demo
                string contentLength;
                if (request.Headers.TryGetValue("content-length", out contentLength))
                {
                    await outputStream.WriteAsync(
                        new ArraySegment<byte>(UpgradeErrorResponse));
                    await outputStream.CloseAsync();
                    return;
                }

                string connectionHeader;
                string hostHeader;
                string upgradeHeader;
                string http2SettingsHeader;
                if (!request.Headers.TryGetValue("connection", out connectionHeader) ||
                    !request.Headers.TryGetValue("host", out hostHeader) ||
                    !request.Headers.TryGetValue("upgrade", out upgradeHeader) ||
                    !request.Headers.TryGetValue("http2-settings", out http2SettingsHeader) ||
                    upgradeHeader != "h2c" ||
                    http2SettingsHeader.Length == 0)
                {
                    throw new Exception("Invalid upgrade request");
                }

                var connParts = 
                    connectionHeader
                    .Split(new char[]{','})
                    .Select(p => p.Trim())
                    .ToArray();
                if (connParts.Length != 2 ||
                    !connParts.Contains("Upgrade") ||
                    !connParts.Contains("HTTP2-Settings"))
                    throw new Exception("Invalid upgrade request");

                var headers = new List<HeaderField>();
                headers.Add(new HeaderField{Name=":method", Value=request.Method});
                headers.Add(new HeaderField{Name=":path", Value=request.Path});
                headers.Add(new HeaderField{Name=":scheme", Value="http"});
                foreach (var kvp in request.Headers)
                {
                    // Skip Connection upgrade related headers
                    if (kvp.Key == "connection" ||
                        kvp.Key == "upgrade" ||
                        kvp.Key == "http2-settings")
                        continue;
                    headers.Add(new HeaderField
                    {
                        Name = kvp.Key,
                        Value = kvp.Value,
                    });
                }

                var upgradeBuilder = new ServerUpgradeRequestBuilder();
                upgradeBuilder.SetHeaders(headers);
                upgradeBuilder.SetHttp2Settings(http2SettingsHeader);
                upgrade = upgradeBuilder.Build();

                if (!upgrade.IsValid)
                {
                    await outputStream.WriteAsync(
                        new ArraySegment<byte>(UpgradeErrorResponse));
                    await outputStream.CloseAsync();
                    return;
                }

                // Respond to upgrade
                await outputStream.WriteAsync(
                    new ArraySegment<byte>(UpgradeSuccessResponse));
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("Error during connection upgrade: {0}", e.Message);
            await outputStream.CloseAsync();
            return;
        }

        // Build a HTTP connection on top of the stream abstraction
        var http2Con = new Connection(
            config, upgradeReadStream, outputStream,
            options: new Connection.Options
            {
                Logger = logProvider.CreateLogger("HTTP2Conn" + connectionId),
                ServerUpgradeRequest = upgrade,
            });

        // Close the connection if we get a GoAway from the client
        var remoteGoAwayTask = http2Con.RemoteGoAwayReason;
        var closeWhenRemoteGoAway = Task.Run(async () =>
        {
            await remoteGoAwayTask;
            await http2Con.GoAwayAsync(ErrorCode.NoError, true);
        });
    }
}
