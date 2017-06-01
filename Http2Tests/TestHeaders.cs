using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

using Http2;
using Http2.Hpack;

namespace Http2Tests
{
    public static class TestHeaders
    {
        public static readonly HeaderField[] DefaultGetHeaders = new HeaderField[]
        {
            new HeaderField { Name = ":method", Value = "GET" },
            new HeaderField { Name = ":scheme", Value = "http" },
            new HeaderField { Name = ":path", Value = "/" },
            new HeaderField { Name = "abc", Value = "def" },
        };

        public static byte[] EncodedDefaultGetHeaders = new byte[]
        {
            0x82, // :method GET
            0x86, // :scheme http
            0x84, // :path /
            0x40, 0x03, (byte)'a', (byte)'b', (byte)'c',
            0x03, (byte)'d', (byte)'e', (byte)'f',
        };

        public static byte[] EncodedIndexedDefaultGetHeaders = new byte[]
        {
            0x82, // :method GET
            0x86, // :scheme http
            0x84, // :path /
            0xBE, // Indexed header
        };

        public static readonly HeaderField[] DefaultStatusHeaders = new HeaderField[]
        {
            new HeaderField { Name = ":status", Value = "200" },
            new HeaderField { Name = "xyz", Value = "ghi" },
        };

        public static byte[] EncodedDefaultStatusHeaders = new byte[]
        {
            0x88,
            0x40, 0x03, (byte)'x', (byte)'y', (byte)'z',
            0x03, (byte)'g', (byte)'h', (byte)'i',
        };

        public static readonly HeaderField[] DefaultTrailingHeaders = new HeaderField[]
        {
            new HeaderField { Name = "trai", Value = "ler" },
        };

        public static byte[] EncodedDefaultTrailingHeaders = new byte[]
        {
            0x40, 0x04, (byte)'t', (byte)'r', (byte)'a', (byte)'i',
            0x03, (byte)'l', (byte)'e', (byte)'r'
        };
    }
}