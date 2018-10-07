﻿using System;
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

            // Build a HTTP connection on top of the stream abstraction
            var http2Con = new Connection(
                config, wrappedStreams.ReadableStream, wrappedStreams.WriteableStream,
                options: new Connection.Options
                {
                    Logger = logProvider.CreateLogger("HTTP2Conn" + connectionId),
                });

            // Close the connection if we get a GoAway from the client
            var remoteGoAwayTask = http2Con.RemoteGoAwayReason;
            var closeWhenRemoteGoAway = Task.Run(async () =>
            {
                await remoteGoAwayTask;
                await http2Con.GoAwayAsync(ErrorCode.NoError, true);
            });

            connectionId++;
        }
    }
}
