using System;
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
        var mainTask = Task.Run(async () =>
        {
            var p = new Program();
            try
            {
                await p.RunAsync(args);
            }
            catch (Exception e)
            {
                p.logger.LogError("Error during exception:\n" + e.Message);
            }
        });
        mainTask.Wait();
    }

    ILoggerProvider logProvider;
    ILogger logger;

    string host;
    string scheme;
    string method;
    string path;
    int port;
    string authority;

    public Program()
    {
        logProvider = new ConsoleLoggerProvider(
            (s, level) => true, true);
        logger = logProvider.CreateLogger("app");
    }

    public async Task RunAsync(string[] args)
    {
        if (args.Length < 1)
            throw new Exception(
                "Program requires at least url argument.\n" +
                "Example usage: dotnet run http://127.0.0.1:8888/");

        method = "GET";
        var uri = new Uri(args[args.Length - 1]);
        scheme = uri.Scheme.ToLowerInvariant();
        host = uri.Host;
        port = uri.Port;
        path = uri.PathAndQuery;
        authority = host + ":" + port;

        if (scheme != "http")
            throw new Exception("Only http scheme is supported");

        var conn = await CreateConnection(host, port);
        for (var i = 0; i < 30; i++)
        {
            await PerformRequest(conn);
        }
        await conn.GoAwayAsync(ErrorCode.NoError, true);
    }

    async Task PerformRequest(Connection conn)
    {
        // nghttp requires :authority header, so we include it
        var headers = new HeaderField[]
        {
            new HeaderField { Name = ":method", Value = method },
            new HeaderField { Name = ":scheme", Value = scheme },
            new HeaderField { Name = ":path", Value = path },
            new HeaderField { Name = ":authority", Value = authority },
        };
        var stream = await conn.CreateStreamAsync(headers, true);

        // Read the response headers
        var responseHeaders = await stream.ReadHeadersAsync();
        // Print all of them
        Console.WriteLine("Response headers:");
        foreach (var hdr in responseHeaders)
        {
            Console.WriteLine($"{hdr.Name}: {hdr.Value}");
        }

        Console.WriteLine("Response body:");
        byte[] buf = new byte[8192];
        while (true)
        {
            var res = await stream.ReadAsync(new ArraySegment<byte>(buf));
            if (res.EndOfStream) break;
            var text = Encoding.UTF8.GetString(buf, 0, res.BytesRead);
            Console.WriteLine(text);
        }
        Console.WriteLine();

        // Read the response trailers
        var responseTrailers = await stream.ReadTrailersAsync();
        if (responseTrailers.Count() > 0)
        {
            // Print all of them
            Console.WriteLine("Response trailers:");
            foreach (var hdr in responseTrailers)
            {
                Console.WriteLine($"{hdr.Name}: {hdr.Value}");
            }
        }
    }

    async Task<Connection> CreateConnection(string host, int port)
    {
        // HTTP/2 settings
        var config =
            new ConnectionConfigurationBuilder(false)
            .UseSettings(Settings.Default)
            .UseHuffmanStrategy(HuffmanStrategy.IfSmaller)
            .Build();

        // Create a TCP connection
        logger.LogInformation($"Starting to connect to {host}:{port}");
        var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(host, port);
        logger.LogInformation("Connected to remote");
        tcpClient.Client.NoDelay = true;
        // Create HTTP/2 stream abstraction on top of the socket
        var wrappedStreams = tcpClient.Client.CreateStreams();

        // Build a HTTP connection on top of the stream abstraction
        var conn = new Connection(
            config, wrappedStreams.ReadableStream, wrappedStreams.WriteableStream,
            options: new Connection.Options
            {
                Logger = logProvider.CreateLogger("HTTP2Conn"),
            });

        return conn;
    }
}
