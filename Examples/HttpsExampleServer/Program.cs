using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
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
        var listener = new TcpListener(IPAddress.Any, 8889);
        listener.Start();
        Task.Run(() => AcceptTask(listener, logProvider)).Wait();
    }

    static bool AcceptIncomingStream(IStream stream)
    {
        Task.Run(() => ExampleShared.HandleIncomingStream(stream,responseBody));
        return true;
    }

    static byte[] responseBody = Encoding.ASCII.GetBytes(
        "<html><head>Hello World</head><body>Content</body></html>");

    private static SslServerAuthenticationOptions options = new SslServerAuthenticationOptions()
    {
        // get our self signed certificate
        ServerCertificate = new X509Certificate2(ReadWholeStream(Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("HttpsExampleServer.localhost.p12"))),
        // this line adds ALPN, critical for HTTP2 over SSL
        ApplicationProtocols = new List<SslApplicationProtocol>(){SslApplicationProtocol.Http2},
        ClientCertificateRequired = false,
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        EnabledSslProtocols = SslProtocols.Tls12
    };

    static byte[] ReadWholeStream(Stream stream)
    {
        byte[] buffer = new byte[16 * 1024];
        using (MemoryStream ms = new MemoryStream())
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                ms.Write(buffer, 0, read);
            }

            return ms.ToArray();
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
            // Create an SSL stream
            var sslStream = new SslStream(new NetworkStream(clientSocket, true));
            // Authenticate on the stream
            await sslStream.AuthenticateAsServerAsync(options,CancellationToken.None);
            // wrap the SslStream
            var wrappedStreams = sslStream.CreateStreams();
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