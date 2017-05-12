http2dotnet
===========

This library implements the HTTP/2 and HPACK protocol for .NET standard.
The goal of the library is to cover all protocol handling parts of the HTTP/2
([RFC7540](http://httpwg.org/specs/rfc7540.html)) and HPACK
([RFC7541](http://httpwg.org/specs/rfc7541.html)) specifications.
The goal of this library is NOT to provide a ready-to-use HTTP/2 server or
client framework with concrete Request/Response abstractions. Instead it should
enable other .NET libraries and applications to easily integrate HTTP/2 support
by encapsulating the protocol support in easy to use and flexible .NET classes.
Examples for building simple applications on top of the library are provided
within the repository.

## Current state

This library is currently in an experimental state. The majority of HTTP/2
features have been implemented and there is already a pretty good test coverage.
It was tested in a h2c with prior knowledge configuration against **nghttp2**
and **h2load**. It was however not yet tested with an encrypted connection and
a browser based client due a missing SSL connection with ALPN negotiation.
Various features (especially for client side usage) have not yet been
implemented (see Current limitations).

## Design goals

- Enable an easy integration of HTTP/2  into different frameworks and
  applications (ASP.NET core, other webservers, gRPC, etc.)
- Provide an abstract interface for HTTP/2 Streams, on which custom Request
  and Response classes can be built. The Stream abstraction supports all
  features of the HTTP/2 specification, including sending HEADERS in both
  directions, flow-controlled DATA and trailing HEADERS. The Request and
  Response are handled fully independet, which enables features like pure server
  side data streaming.
- IO layer independence:  
  This library uses an abstract IO interface, which is defined in the
  `ByteStreams.cs` source file.  
  The IO layer can be implemented by .NET System.IO.Streams, Pipelines,
  TCP sockets, TLS sockets and other IO sources.  
  The abstract IO interface is inspired by the Go IO interfaces. It was chosen
  for simplicity, high performance (async reads/writes can be avoided) and 
  because it can easily provide the necessary flow control behavior.
- Correctness and reliability:  
  All HTTP/2 frames are validated according to the HTTP/2 specification.  
  Full backpressure behavior is provided for HTTP/2 DATA
  frames (through the specified flow control mechanism) as well as for other
  HTTP/2 frames (by suspending the reception of further frames until a suitable
  response for a frame could be sent).
- High performance:  
  To achieve a high performance and low overhead the library tries to minimize
  dynamic allocations whereever possible, e.g. through the use of `ValueTask`
  structures and reusing of buffers and data structures. If possible the library
  also avoids async operations by trying nonblocking operations first to avoid
  scheduling and continuation overhead.
- Full support of all HTTP/2 features (however not yet everything is implemented)

## Usage

The library exposes 2 important classes:
- `Connection` represents a HTTP/2 connection between a client and the server.  
  HTTP/2 connections are created on top of bidirectional streams. For encrypted
  HTTP/2 (the only variant support by browser - also called h2) the Connection
  must be created on top of SSL connection. For unencrypted connections (h2c)
  the Connection has to be created on top of a TCP stream.  
  On top of the connection multiple bidirectional streams can be established.
- `IStream` represents a single bidirectional HTTP/2 stream.  
  Each Request/Response pair in HTTP/2 is represented by a single Stream.

In the most common lifecycle a HTTP/2 client will create a new Stream on top
of the Connection, send all required headers for the Request through the Stream,
and then send optional data through the stream. The server will receive a
notification about a new create stream and invoke a request handler. The request
handler can then read the received headers, read all incoming data from the
stream and respond with headers and data.

Besides that there are some advanced features, e.g. both clients and servers
can also Cancel/Reset the stream during processing and both are able to send
trailing headers at the end of a Stream.

This library does not create the underlying TCP or SSL connections on server or
client side. This is the responsibility of the library user. However this
pattern allows to use the library on top of arbitrary IO stream types.

To create a `Connection` on top of IO streams the constructor of `Connection`
can be used:

```cs
ConnectionConfiguration config =
    new ConnectionConfigurationBuilder(isServer: true)
    .UseStreamListener(AcceptIncomingStream)
    .Build();

Connection http2Connection = new Connection(
    config: config,
    inputStream: inputStream,
    outputStream: outputStream);
```

Configuration settings which are shared between multiple connections are
configured through the `ConnectionConfigurationBuilder` which stores them in an
`ConnectionConfiguration` instance. The remaining parameters which are unique
for each `Connection` instance are configured directly thorugh the `Connection`
constructor parameters.

The most important arguments for the `Connection` are:
- `inputStream`/`outputStream`: The `Connection` will will use the IO streams
  to read data from the remote side and send data to the remote side. The
  connection will take ownership of these streams and close the `OutputStream`
  when done.
- `config`: The configuration for the connection, which is possibly also shared
  with other `Connection` instances.

The most important arguments for the `ConnectionConfiguration` are:
- `isServer`: Specifies whether the local side of the `Connection` represents a
  HTTP/2 server (`true`) or client (`false`).
- `UseStreamListener`: This sets up the callback function that will be invoked
  every time a new Stream is initiated from the remote side. This means it is
  currently only required for HTTP/2 server applications.
  An `IStream` instance which represents the initiated stream is passed to the
  callback function as an argument. The callback function should either return
  `true` if it wants to process the new stream or `false` otherwise.
  If the application wants to process the new stream it must use it in another
  `Task` and must not block the callback.
- `UseSettings`: The HTTP/2 settings that should be used. `Settings.Default`
  represents the default values from the HTTP/2 specification and should usually
  be a good choice. These are also utilized if not explicitely specified.

Besides that there are various optional arguments which allow further control
about the desired behavior.

After the `Connection` was set up it will handle all HTTP/2 protocol related
concerns up to the point where the underlying connection gets closed.

The `IStream` class allows to read and write headers and bytes for a single
HTTP/2 stream. The methods `ReadHeadersAsync()` and `WriteHeadersAsync()`
allow to read and write headers from a connection.
The returned `ValueTask` from `ReadHeadersAsync` will only be fulfilled once
headers from the remote side where received.
According to the HTTP/2 request/reponse lifecycle applications have to send
headers at the start of each Stream - which means it is not allowed to send
data before any headers have been sent. The received headers will contain the
pseudo-headers which are used in HTTP/2 for transmission of the HTTP method
(`:method`), status code (`:status`), etc. These are not automatically extracted
into seperated fields because `IStream` is used for requests and responses -
depending on whether the library is used for a client or server. However higher
level frameworks can easily extract the fields from the returned header lists.
This library will always validate received and sent headers for correctness.
This means a user of the library on the server side can rely on the fact that
the `:method`, `:scheme` and `:path` pseudo headers are preset and that after
the first real header field no pseudo header field follows.

After headers were sent for a Stream data can be written to the stream. As
`IStream` implements the ByteStream abstractions in this library, the data can
be written through the provided `WriteAsync` function. The returned `Task` will
only be fulfilled after the data could be sent through the underlying
connection. This will take the available flow control windows into account. The
stream can be closed from the by calling the `CloseAsync` function. Alternativly
the `endOfStream` parameter of `WriteHeadersAsync` can be used for closing the
Stream without sending any data (e.g. for HTTP GET requests). The read and write
directions of HTTP/2 are fully independet. This means closing the local side of
the stream will still allow reading headers and data from the remote side. A
stream is only fully closed after all headers and data were consumed from the
remote side in addition to the local side having been closed. To read data from
the stream the `ReadAsync` function can be used. This function will signal the
end of stream if the stream was closed from the remote side. After a stream was
closed from the remote side trailers can be read through the `ReadTrailersAsync`
function. To send trailers data must be transferred and instead of closing the
stream with `CloseAsync` or setting `endOfStream` to `true` the
`WriteTrailersAsync` function must be used.

The `Cancel` function on the `IStream` can be used to reset the stream. This
will move the Stream into the Closed state if it wasn't Closed before. In order
to notify the remote side a RST_STREAM frame will be sent. All streams must be
either fully processed (read side fully consumed - write side closed) or
cancelled/reset by the application. Otherwise the Stream will be leaked. The
`Dispose` method on stream will also cancel the stream.

The following example shows the handling of a stream on the server side:

```cs
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
            Encoding.ASCII.GetBytes("Hello World!")), true);

        // Request is fully handled here
    }
    catch (Exception)
    {
        stream.Cancel();
    }
}
```

## Server side connection upgrades

*This section only applies to unsecure HTTP. In the case of HTTPS the protocol
negiotiation would always happen by the means of Application-Layer Protocol
Negotiation (ALPN).*

### Upgrading from HTTP/1.1

The library supports HTTP connection upgrade requests on server side.
Example code for how an upgrade can be performed can be found in the
`UpgradeExampleServer`.

In order to upgrade from HTTP/1.1 to HTTP/2, an external HTTP/1.1 parser is
needed which reads the complete upgrade request into memory (including an
optional body).

If the headers of the received HTTP/1.1 request contain the related upgrade
headers (`connection: Upgrade, HTTP2-Settings`), they payload can be checked for
whether it is a valid upgrade request or not. In order to perform this step a
`ServerUpgradeRequestBuilder` instance must be created, which gets fed all the
information (headers, payload, request method, etc.) from the HTTP/1.1 request.
With the `ServerUpgradeRequestBuilder.build()` method a `ServerUpgradeRequest`
can be created, which exposes an `IsValid` property This property reflects
whether the incoming request is a fully valid upgrade request.

Only if the upgrade request is valid a HTTP/2 `Connection` object may be
created, which get's the inital upgrade request injected into the constructor
as part of the `Connection.Options` struct. In this case the `Connection` will
create a HTTP/2 `Stream` from the content of the initial upgrade request,
which can be handled like any other HTTP/2 Stream/Request.

In case the upgrade request is valid, it is the applications responsibility to
send `HTTP/1.1 101 Switching Protocols` response before creating the HTTP/2
connection object - which won't perform this step, and instead directly speak
HTTP/2.

In case the upgrade request is not valid, the application may choose to either
treat the request as a normal HTTP/1.1 request (by filtering all ugprade related)
headers, or by responding with an HTTP error.

Even if the upgrade headers are available and valid in the initial HTTP/1.1
request, applications may choose not to perform the upgrade but instead handle
the request as a normal HTTP/1.1 request. An example scenario for this is when
the upgrade request indicates a large body (through `content-length` header),
which would needed to be completely read into memory before the upgrade can be
performed: If the application doesn't perform the upgrade and just handles the
request in traditional fashion, the request body can be streamed in normal
HTTP/1.1 fashion. It is generelly not recommended to accept upgrade requests
which contain a body payload. However most client side libraries that perform
upgrades (e.g. nghttp), will perform the upgrade only via `GET` and `HEAD`
requests anyway.

### Handling HTTP/2 (with and without upgrade) and HTTP/1 in parallel

In some scenarios it might be required to implement a HTTP server that
supports all of the following scenarios:
- Support for standard HTTP/1.1 requests
- Support for HTTP/2 requests with prior knowledge (The client knows that the
  server speaks HTTP/2 and no upgrade is required).
- Support for upgrading from HTTP/1.1 to HTTP/2

This library supports this scenario, and a suitable example implementation can
be found in the `UpgradeExampleServer` example.

The idea is that the application (or web-server which is built around the
library) starts off by reading and parsing an initial HTTP/1.1 request header
through it's preferred HTTP parser.

- If the request header exactly equals `PRI * HTTP/2.0\r\n\r\n` it's the start
  of a HTTP/2 connection with prior knowledge. In this case the application can
  directly create a HTTP/2 `Connection` object on top of the incoming stream
  and delegate all further processing to it. In this special case, the
  application must make sure that the stream that is passed to the `Connection`
  object still emits the `PRI * HTTP/2.0\r\n\r\n` header when the `Connection`
  starts reading from it. Otherwise the `Connection` establishment will fail.
  This means the application must inspect, but not consume this header.
- If the request is another HTTP/1.1 request which contains ugprade headers, the
  application can handle it as described in the [Upgrading from HTTP/1.1 on server side](#upgrading-from-http-1-1-on-server-side)
  section. In this case it's the applications responsibility to consume the
  ugprade request from the underlying stream and send the necessary upgrade
  status response.
- If the request header is an HTTP/1.1 request without upgrade headers, the
  application can directly handle it in it's preferred way.
- If the incoming data resembles no HTTP request at all, the application can
  also handle it in it's preferred way, e.g. by optionally sending an HTTP error
  code and closing the connection.

In the first two cases the actual HTTP requests from the connection will further
flow to the application through by the means of this libraries `IStream`
abstraction. In the third case it depends on the approach for HTTP/1.1 parrsing
and processing. It's the applications responsibility to transform all request
kinds into a common `Request`/`Response` representation if needed.

## Ping handling

The library will automatically respond to received PING frames
by sending the associated PING ACKs.

The user can also issue PINGs from the local side of the connection through the
`PingAsync` method of the `Connection` class. Calling this method will issue
sending a PING frame to the remote side of the connection. It returns a task
that will be completed once either the remote side acknowledges the PING or the
connection was closed. If the connection closes before an ACK was received the
returned `Task` will fail with a `ConnectionClosedException`.

Example for measuring the connection latency through the ping mechanism:

```cs
try
{
    var stopWatch = new Stopwatch();
    stopWatch.Start();
    await http2Connection.PingAsync();
    // Ping request was sent and acknowledge has been received
    stopWatch.Stop();
    var latency = stopWatch.Elapsed;
}
catch (ConnectionClosedException)
{
    // The connection was closed before an acknowledge to the PING
    // was received
}
```

## Current limitations

The library currently faces the following limitations:
- Missing support for creating streams from client side.
- Missing support for the push promises.
- Missing support for reading the remote SETTINGS from application side.
- The scheduling of outgoing DATA frames is very basic and relies mostly on flow
  control windows and the maximum supported frame size. It is currently not
  guaranteed that concurrent streams with equal flow control windows will get
  the same amount of bandwith.
  HTTP/2 priorization features are also not supported - however these are optional
  according to the HTTP/2 specification and may not be required for lots of
  applications.