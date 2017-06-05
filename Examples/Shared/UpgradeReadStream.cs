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

/// <summary>
/// Wrapper around the readable stream, which allows to read HTTP/1
/// request headers.
/// </summary>
class UpgradeReadStream : IReadableByteStream
{
    IReadableByteStream stream;
    byte[] httpBuffer = new byte[MaxHeaderLength];
    int httpBufferOffset = 0;
    int httpHeaderLength = 0;

    ArraySegment<byte> remains;

    const int MaxHeaderLength = 1024;

    public int HttpHeaderLength => httpHeaderLength;

    public ArraySegment<byte> HeaderBytes =>
        new ArraySegment<byte>(httpBuffer, 0, HttpHeaderLength);

    public UpgradeReadStream(IReadableByteStream stream)
    {
        this.stream = stream;
    }

    /// <summary>
    /// Waits until a whole HTTP/1 header, terminated by \r\n\r\n was received.
    /// This may only be called once at the beginning of the stream.
    /// If the header was found it can be accessed with HeaderBytes.
    /// Then it must be either consumed or marked as unread.
    /// </summary>
    public async Task WaitForHttpHeader()
    {
        while (true)
        {
            var res = await stream.ReadAsync(
                new ArraySegment<byte>(httpBuffer, httpBufferOffset, httpBuffer.Length - httpBufferOffset));

            if (res.EndOfStream)
                throw new System.IO.EndOfStreamException();
            httpBufferOffset += res.BytesRead;

            // Check for end of headers in the received data
            var str = Encoding.ASCII.GetString(httpBuffer, 0, httpBufferOffset);
            var endOfHeaderIndex = str.IndexOf("\r\n\r\n");
            if (endOfHeaderIndex == -1)
            {
                // Header end not yet found
                if (httpBufferOffset == httpBuffer.Length)
                {
                    httpBuffer = null;
                    throw new Exception("No HTTP header received");
                }
                // else read more bytes by looping around
            }
            else
            {
                httpHeaderLength = endOfHeaderIndex + 4;
                return;
            }
        }
    }

    /// <summary>
    /// Marks the HTTP reader as unread, which means following
    /// ReadAsync calls will reread the header.
    /// </summary>
    public void UnreadHttpHeader()
    {
        remains = new ArraySegment<byte>(
            httpBuffer, 0, httpBufferOffset);
    }

    /// <summary>Removes the received HTTP header from the input buffer</summary>
    public void ConsumeHttpHeader()
    {
        if (httpHeaderLength != httpBufferOffset)
        {
            // Not everything was consumed
            remains = new ArraySegment<byte>(
                httpBuffer, httpHeaderLength, httpBufferOffset - httpHeaderLength);
        }
        else
        {
            remains = new ArraySegment<byte>();
            httpBuffer = null;
        }
    }

    public ValueTask<StreamReadResult> ReadAsync(ArraySegment<byte> buffer)
    {
        if (remains.Count != 0)
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
                httpBuffer = null;
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