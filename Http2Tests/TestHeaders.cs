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

        public static readonly HeaderField[] DefaultStatusHeaders = new HeaderField[]
        {
            new HeaderField { Name = ":status", Value = "200" },
            new HeaderField { Name = "xyz", Value = "ghi" },
        };

        public static byte[] EncodedDefaultStatusHeaders = new byte[]
        {
            0x88, 0x40, 0x03, 0x78, 0x79, 0x7a, 0x03, 0x67, 0x68, 0x69,
        };

        public static readonly HeaderField[] DefaultTrailingHeaders = new HeaderField[]
        {
            new HeaderField { Name = "trai", Value = "ler" },
        };

        public static byte[] EncodedDefaultTrailingHeaders = new byte[]
        {
            0x40, 0x04, 0x74, 0x72, 0x61, 0x69, 0x03, 0x6c, 0x65, 0x72,
        };
    }
}