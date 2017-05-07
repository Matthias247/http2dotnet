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

    static byte[] responseBody = Encoding.ASCII.GetBytes(
        "<html><head>Hello World</head><body>Content</body></html>");

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

    static byte[] UpgradeSuccessResponse = Encoding.ASCII.GetBytes(
        "HTTP/1.1 101 Switching Protocols\r\n" +
        "Connection: Upgrade\r\n" +
        "Upgrade: h2c\r\n\r\n");

    static byte[] UpgradeErrorResponse = Encoding.ASCII.GetBytes(
        "HTTP/1.1 400 Bad request\r\n\r\n");

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
        if (useUpgrade)
        {
            var upgradeReadStream = new UpgradeReadStream(inputStream);
            ServerUpgradeRequest upgrade;
            try
            {
                var headerStr = await upgradeReadStream.ReadHttpHeader();
                var request = RequestHeader.ParseFrom(headerStr);

                if (!(request.Method == "GET" || request.Method == "OPTIONS") ||
                    request.Protocol != "HTTP/1.1")
                    throw new Exception("Invalid upgrade request");

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
                headers.Add(new HeaderField{Name=":scheme", Value=request.Protocol});
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
            catch (Exception e)
            {
                Console.WriteLine("Error during connection upgrade: {0}", e.Message);
                await outputStream.CloseAsync();
                return;
            }

            // Build a HTTP connection on top of the stream abstraction
            var http2Con = new Connection(
                config, inputStream, outputStream,
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

    class RequestHeader
    {
        public string Method;
        public string Path;
        public string Protocol;

        private static Exception InvalidRequestHeaderException =
            new Exception("Invalid request header");

        public Dictionary<string, string> Headers = new Dictionary<string, string>();

        private static System.Text.RegularExpressions.Regex requestLineRegExp =
            new System.Text.RegularExpressions.Regex(
                @"([^\s]*) ([^\s]*) ([^\s]*)");

        public static RequestHeader ParseFrom(string requestHeader)
        {
            var lines = requestHeader.Split(new string[]{"\r\n"}, StringSplitOptions.None);
            if (lines.Length < 1) throw InvalidRequestHeaderException;

            // Parse request line in form GET /page HTTP/1.1
            // This is a super simple and bad parser
            
            var match = requestLineRegExp.Match(lines[0]);
            if (!match.Success) throw InvalidRequestHeaderException;

            var method = match.Groups[1].Value;
            var path = match.Groups[2].Value;
            var proto = match.Groups[3].Value;
            if (string.IsNullOrEmpty(method) ||
                string.IsNullOrEmpty(path) ||
                string.IsNullOrEmpty(proto))
                throw InvalidRequestHeaderException;

            var headers = new Dictionary<string, string>();
            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                var colonIdx = line.IndexOf(':');
                if (colonIdx == -1) throw InvalidRequestHeaderException;
                var name = line.Substring(0, colonIdx).Trim().ToLowerInvariant();
                var value = line.Substring(colonIdx+1).Trim();
                headers[name] = value;
            }

            return new RequestHeader()
            {
                Method = method,
                Path = path,
                Protocol = proto,
                Headers = headers,
            };
        }
    }

    class UpgradeReadStream : IReadableByteStream
    {
        IReadableByteStream stream;
        ArraySegment<byte> remains;

        const int MaxHeaderLength = 1024;

        public UpgradeReadStream(IReadableByteStream stream)
        {
            this.stream = stream;
        }

        public async Task<string> ReadHttpHeader()
        {
            byte[] httpBuffer = new byte[MaxHeaderLength];
            var offset = 0;

            while (true)
            {
                var res = await stream.ReadAsync(
                    new ArraySegment<byte>(httpBuffer, offset, httpBuffer.Length - offset));

                if (res.EndOfStream)
                    throw new System.IO.EndOfStreamException();
                offset += res.BytesRead;

                // Check if stream theres and end of headers in the received data
                var str = Encoding.ASCII.GetString(httpBuffer, 0, offset);
                var endOfHeaderIndex = str.IndexOf("\r\n\r\n");
                if (endOfHeaderIndex == -1)
                {
                    // Header end not yet found
                    if (offset == httpBuffer.Length)
                    {
                        throw new Exception("No HTTP header received");
                    }
                    // else read more bytes by looping around
                }
                else
                {
                    var remainOffset = endOfHeaderIndex + 4;
                    if (remainOffset != offset)
                    {
                        remains = new ArraySegment<byte>(
                            httpBuffer, remainOffset, offset - remainOffset);
                    }

                    var header = str.Substring(0, endOfHeaderIndex);
                    return header;
                }
            }
        }

        public ValueTask<StreamReadResult> ReadAsync(ArraySegment<byte> buffer)
        {
            if (remains != null)
            {
                // Return leftover bytes from upgrade request
                var toCopy = Math.Min(remains.Count, buffer.Count);
                Array.Copy(
                    remains.Array, remains.Offset,
                    buffer.Array, buffer.Offset,
                    toCopy);
                var newOffset = remains.Offset + toCopy;
                var newCount = remains.Count - toCopy;
                if (newCount != 0)
                {
                    remains = new ArraySegment<byte>(remains.Array, newOffset, newCount);
                }
                else
                {
                    remains = new ArraySegment<byte>();
                }
                return new ValueTask<StreamReadResult>(
                    new StreamReadResult()
                    {
                        BytesRead = toCopy,
                        EndOfStream = false,
                    });
            }

            return stream.ReadAsync(buffer);
        }
    }
}
