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
/// A primitive HTTP/1 request header parser
/// </summary>
class Http1Request
{
    public string Method;
    public string Path;
    public string Protocol;

    private static Exception InvalidRequestHeaderException =
        new Exception("Invalid request header");

    public Dictionary<string, string> Headers = new Dictionary<string, string>();

    private static System.Text.RegularExpressions.Regex requestLineRegExp =
        new System.Text.RegularExpressions.Regex(
            @"([^\s]+) ([^\s]+) ([^\s]+)");

    public static Http1Request ParseFrom(string requestHeader)
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

        return new Http1Request()
        {
            Method = method,
            Path = path,
            Protocol = proto,
            Headers = headers,
        };
    }
}

/// <summary>
/// A primitive HTTP/1 response parser
/// </summary>
class Http1Response
{
    public string HttpVersion;
    public string StatusCode;
    public string Reason;

    private static Exception InvalidResponseHeaderException =
        new Exception("Invalid response header");

    public Dictionary<string, string> Headers = new Dictionary<string, string>();

    private static System.Text.RegularExpressions.Regex responseLineRegExp =
        new System.Text.RegularExpressions.Regex(
            @"([^\s]+) ([^\s]+) ([^\s]+)");

    public static Http1Response ParseFrom(string responseHeader)
    {
        var lines = responseHeader.Split(new string[]{"\r\n"}, StringSplitOptions.None);
        if (lines.Length < 1) throw InvalidResponseHeaderException;

        // Parse request line in form GET /page HTTP/1.1
        // This is a super simple and bad parser
        
        var match = responseLineRegExp.Match(lines[0]);
        if (!match.Success) throw InvalidResponseHeaderException;

        var httpVersion = match.Groups[1].Value;
        var statusCode = match.Groups[2].Value;
        var reason = match.Groups[3].Value;
        if (string.IsNullOrEmpty(httpVersion) ||
            string.IsNullOrEmpty(statusCode) ||
            string.IsNullOrEmpty(reason))
            throw InvalidResponseHeaderException;

        var headers = new Dictionary<string, string>();
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var colonIdx = line.IndexOf(':');
            if (colonIdx == -1) throw InvalidResponseHeaderException;
            var name = line.Substring(0, colonIdx).Trim().ToLowerInvariant();
            var value = line.Substring(colonIdx+1).Trim();
            headers[name] = value;
        }

        return new Http1Response()
        {
            HttpVersion = httpVersion,
            StatusCode = statusCode,
            Reason = reason,
            Headers = headers,
        };
    }
}