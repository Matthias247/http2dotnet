using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.CommandLineUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Http2;
using Http2.Hpack;

class Program
{
    static void Main(string[] args)
    {
        var p = new Program();
        try
        {
            p.Run(args);
        }
        catch (Exception e)
        {
            Console.WriteLine("Error during execution:\n" + e.Message);
        }
    }

    bool verbose = false;

    ILoggerProvider logProvider;
    ILogger logger;

    string host;
    string scheme;
    string method = "GET";
    string path;
    int port;
    string authority;

    /// Whether to upgrade from HTTP/1.1
    bool useHttp1Upgrade = false;

    /// Benchmark mode or not
    bool isBenchmark = false;

    // Benchmark related settings
    int totalRequests = 10000;
    int cpuCores = Environment.ProcessorCount;
    int concurrentRequests = 1;

    public void Run(string[] args)
    {
        var app = new CommandLineApplication();
        app.Name = "CliClient";
        app.HelpOption("-?|-h|--help");

        var methodOption = app.Option(
            "-X|--method",
            "The HTTP method to use. The default is GET",
            CommandOptionType.SingleValue);
        var verboseOption = app.Option(
            "-v|--verbose",
            "Activate verbose logging",
            CommandOptionType.NoValue);

        var benchmarkOption = app.Option(
            "--benchmark",
            "Run in benchmark mode",
            CommandOptionType.NoValue);
         var cpuCoresOption = app.Option(
            "-c|--cores",
            "Override the number of CPU cores to use for benchmarks. " +
            "By default the amount the available CPU cores will be utilized. " +
            "This also equals the amount of utilized connections.",
            CommandOptionType.SingleValue);
        var numberRequestsOption = app.Option(
            "-n|--nrRequests",
            "The number of requests that should be performed in benchmark mode",
            CommandOptionType.SingleValue);
        var concurrentRequestsOption = app.Option(
            "-m|--maxConcurrentRequests",
            "The number of concurrent requests that should be performed in " +
            "benchmark mode per connection",
            CommandOptionType.SingleValue);
        var upgradeOption = app.Option(
            "-u|--upgrade",
            "Perform the request via HTTP/1.1 upgrade mechanism",
            CommandOptionType.NoValue);

        var uriArgument = app.Argument(
            "uri",
            "address to fetch");

        app.OnExecute(async () =>
        {
            // Setup log filtering
            Func<string, LogLevel, bool> logFilter =
                (s, level) => level >= LogLevel.Information;
            if (verboseOption.HasValue())
            {
                verbose = true;
                logFilter = (s, level) => true;
            }
            logProvider = new ConsoleLoggerProvider(logFilter, true);
            logger = logProvider.CreateLogger("app");

            if (String.IsNullOrEmpty(uriArgument.Value))
                throw new Exception(
                    "Program requires at least url argument.\n" +
                    "Example usage: dotnet run http://127.0.0.1:8888/");

            var uri = new Uri(uriArgument.Value);
            scheme = uri.Scheme.ToLowerInvariant();
            host = uri.Host;
            port = uri.Port;
            path = uri.PathAndQuery;
            authority = host + ":" + port;

            if (methodOption.HasValue())
                method = methodOption.Value();

            if (upgradeOption.HasValue())
                useHttp1Upgrade = true;
            if (benchmarkOption.HasValue())
                isBenchmark = true;
            if (cpuCoresOption.HasValue())
            {
                if (!Int32.TryParse(cpuCoresOption.Value(), out cpuCores) ||
                    cpuCores < 1)
                    throw new Exception("Invalid number of cpu cores");
            }
            if (numberRequestsOption.HasValue())
            {
                if (!Int32.TryParse(numberRequestsOption.Value(), out totalRequests) ||
                    totalRequests < 1)
                    throw new Exception("Invalid number of requests");
            }
            if (concurrentRequestsOption.HasValue())
            {
                if (!Int32.TryParse(concurrentRequestsOption.Value(), out concurrentRequests) ||
                    concurrentRequests < 1)
                    throw new Exception("Invalid number of concurrent requests");
            }

            if (scheme != "http" && scheme != "https")
                throw new Exception("Only http and https schemes are supported");

            if (isBenchmark)
            {
                await RunBenchmark();
            }
            else
            {
                await RunSingleRequest();
            }

            return 0;
        });

        if (args.Length < 1)
        {
            app.ShowHelp();
        }
        else
        {
            app.Execute(args);
        }
    }

    async Task RunSingleRequest()
    {
        var sw = new System.Diagnostics.Stopwatch();
        sw.Start();

        var conn = await CreateConnection(scheme, host, port);
        await PerformRequest(conn);
        await conn.GoAwayAsync(ErrorCode.NoError, true);

        sw.Stop();
        logger.LogInformation($"Elapsed time: {sw.Elapsed}");
    }

    async Task RunBenchmark()
    {
        // Make some adjustments:
        // If the number of requests is smaller than the amount of cores then
        // use lower core amount, since making half of a request doesn't make
        // sense
        if (totalRequests < cpuCores)
            cpuCores = totalRequests;

        logger.LogInformation(
            $"Starting benchmark with following settings:\n" +
            $"total requests: {totalRequests}\n" +
            $"concurrent requests per connection: {concurrentRequests}\n" +
            $"CPU cores and worker threads: {cpuCores}");

        var requestsPerCore = totalRequests / cpuCores;

        var sw = new System.Diagnostics.Stopwatch();
        sw.Start();
        var tasks = new Task[cpuCores];
        for (var i = 0; i < cpuCores; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                var requestTasks = new Task[concurrentRequests];
                var conn = await CreateConnection(scheme, host, port);
                var remainingRequests = requestsPerCore;

                while (remainingRequests > 0)
                {
                    var batchSize = Math.Min(remainingRequests, concurrentRequests);
                    for (var j = 0; j < batchSize; j++)
                    {
                        requestTasks[j] = PerformRequest(conn);
                    }
                    await Task.WhenAll(requestTasks.Take(batchSize));
                    remainingRequests -= batchSize;
                }
                await conn.GoAwayAsync(ErrorCode.NoError, true);
            });
        }
        await Task.WhenAll(tasks);
        sw.Stop();
        logger.LogInformation($"Elapsed time: {sw.Elapsed}");

        var requestsPerSecond =
            (double)totalRequests / (double)sw.ElapsedMilliseconds * 1000.0;
        logger.LogInformation($"{(int)requestsPerSecond} requests/s");
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
        if (!isBenchmark)
        {
            // Print all of them
            Console.WriteLine("Response headers:");
            foreach (var hdr in responseHeaders)
            {
                Console.WriteLine($"{hdr.Name}: {hdr.Value}");
            }
        }

        // Console.WriteLine("Response body:");
        byte[] buf = new byte[8192];
        while (true)
        {
            var res = await stream.ReadAsync(new ArraySegment<byte>(buf));
            if (res.EndOfStream) break;
            if (!isBenchmark)
            {
                var text = Encoding.UTF8.GetString(buf, 0, res.BytesRead);
                Console.WriteLine(text);
            }
        }
        if (!isBenchmark)
        {
            Console.WriteLine();
        }

        // Read the response trailers
        var responseTrailers = await stream.ReadTrailersAsync();
        if (!isBenchmark)
        {
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
    }

    /// <summary>Create a HTTP/2 connection to the remote peer</summary>
    Task<Connection> CreateConnection(string scheme, string host, int port)
    {
        if (useHttp1Upgrade) return CreateUpgradeConnection(scheme, host, port);
        else return CreateDirectConnection(scheme, host, port);
    }

    async Task<(IReadableByteStream, IWriteAndCloseableByteStream)> CreateStreams(string scheme, string host, int port)
    {
        // Create a TCP connection
        logger.LogInformation($"Starting to connect to {scheme}://{host}:{port}");
        var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(host, port);
        logger.LogInformation("Connected to remote");
        tcpClient.Client.NoDelay = true;
        if (scheme == "https")
        {
            var stream = new SslStream(tcpClient.GetStream());
            logger.LogInformation("Negotiating SSL...");
            var options = new SslClientAuthenticationOptions
            {
                TargetHost = host,
                ApplicationProtocols = new List<SslApplicationProtocol>
                {
                    SslApplicationProtocol.Http2
                },
            };
            await stream.AuthenticateAsClientAsync(options, default(CancellationToken));
            if (stream.NegotiatedApplicationProtocol != SslApplicationProtocol.Http2)
            {
                throw new NotSupportedException("HTTP2 is not supported by the remote host.");
            }
            logger.LogInformation("SSL Authenticated");
            var result = stream.CreateStreams();
            return (result.ReadableStream, result.WriteableStream);
        }
        else
        {
            // Create HTTP/2 stream abstraction on top of the socket
            var result = tcpClient.Client.CreateStreams();
            return (result.ReadableStream, result.WriteableStream);
        }
    }

    async Task<Connection> CreateDirectConnection(string scheme, string host, int port)
    {
        // HTTP/2 settings
        var config =
            new ConnectionConfigurationBuilder(false)
            .UseSettings(Settings.Default)
            .UseHuffmanStrategy(HuffmanStrategy.IfSmaller)
            .Build();

        var (readableStream, writeableStream) = await CreateStreams(scheme, host, port);

        // Build a HTTP connection on top of the stream abstraction
        var connLogger = verbose ? logProvider.CreateLogger("HTTP2Conn") : null;
        var conn = new Connection(
            config, readableStream, writeableStream,
            options: new Connection.Options
            {
                Logger = connLogger,
            });

        return conn;
    }

    async Task<Connection> CreateUpgradeConnection(string scheme, string host, int port)
    {
        // HTTP/2 settings
        var config =
            new ConnectionConfigurationBuilder(false)
            .UseSettings(Settings.Default)
            .UseHuffmanStrategy(HuffmanStrategy.IfSmaller)
            .Build();

        // Prepare connection upgrade
        var upgrade =
            new ClientUpgradeRequestBuilder()
            .SetHttp2Settings(config.Settings)
            .Build();

        var (readableStream, writeableStream) = await CreateStreams(scheme, host, port);
        var upgradeReadStream = new UpgradeReadStream(readableStream);

        var needExplicitStreamClose = true;
        try
        {
            var upgradeValue = scheme == "https" ? "h2" : "h2c";
            // Send a HTTP/1.1 upgrade request with the necessary fields
            var upgradeHeader =
                "OPTIONS / HTTP/1.1\r\n" +
                "Host: " + host + "\r\n" +
                "Connection: Upgrade, HTTP2-Settings\r\n" +
                $"Upgrade: {upgradeValue}\r\n" +
                "HTTP2-Settings: " + upgrade.Base64EncodedSettings + "\r\n\r\n";
            logger.LogInformation("Sending upgrade request:\n" + upgradeHeader);
            var encodedHeader = Encoding.ASCII.GetBytes(upgradeHeader);
            await writeableStream.WriteAsync(
                new ArraySegment<byte>(encodedHeader));

            // Wait for the upgrade response
            await upgradeReadStream.WaitForHttpHeader();
            var headerBytes = upgradeReadStream.HeaderBytes;

            logger.LogInformation(
                "Received HTTP/1.1 response: " +
                Encoding.ASCII.GetString(headerBytes.Array, 0, headerBytes.Count));

            // Try to parse the upgrade response as HTTP/1 status and check whether
            // the upgrade was successful.
            var response = Http1Response.ParseFrom(
                Encoding.ASCII.GetString(
                    headerBytes.Array, headerBytes.Offset, headerBytes.Count-4));
            // Mark the HTTP/1.1 bytes as read
            upgradeReadStream.ConsumeHttpHeader();

            if (response.StatusCode != "101")
                throw new Exception("Upgrade failed");
            if (!response.Headers.Any(hf => hf.Key == "connection" && hf.Value == "Upgrade") ||
                !response.Headers.Any(hf => hf.Key == "upgrade" && hf.Value == "h2c"))
                throw new Exception("Upgrade failed");

            logger.LogInformation("Connection upgrade successful!");

            // If we get here then the connection will be responsible for closing
            // the stream
            needExplicitStreamClose = false;

            // Build a HTTP connection on top of the stream abstraction
            var connLogger = verbose ? logProvider.CreateLogger("HTTP2Conn") : null;
            var conn = new Connection(
                config, upgradeReadStream, writeableStream,
                options: new Connection.Options
                {
                    Logger = connLogger,
                    ClientUpgradeRequest = upgrade,
                });

            // Retrieve the response stream for the connection upgrade.
            var upgradeStream = await upgrade.UpgradeRequestStream;
            // As we made the upgrade via a dummy OPTIONS request we are not
            // really interested in the result of the upgrade request
            upgradeStream.Cancel();

            return conn;
        }
        finally
        {
            if (needExplicitStreamClose)
            {
                await writeableStream.CloseAsync();
            }
        }
    }
}
