using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text;

using Xunit;

using Http2;

namespace Http2Tests
{
    public class FrameTests
    {
        [Fact]
        public void FrameHeadersShouldBeDecodable()
        {
            var header = new byte[] { 0xFF, 0xFF, 0xFF, 0x00, 0xFF, 0x7F, 0xFF, 0xFF, 0xFF };
            var frame = FrameHeader.DecodeFrom(new ArraySegment<byte>(header));
            Assert.Equal((1 << 24) - 1, frame.Length);
            Assert.Equal(FrameType.Data, frame.Type);
            Assert.Equal(0xFF, frame.Flags);
            Assert.Equal(0x7FFFFFFFu, frame.StreamId);

            header = new byte[] { 0x03, 0x40, 0xF1, 0xFF, 0x09, 0x1A, 0xB8, 0x34, 0x12 };
            frame = FrameHeader.DecodeFrom(new ArraySegment<byte>(header));
            Assert.Equal(0x0340F1, frame.Length);
            Assert.Equal((FrameType)0xFF, frame.Type);
            Assert.Equal(0x09, frame.Flags);
            Assert.Equal(0x1AB83412u, frame.StreamId);
        }

        [Fact]
        public void FrameHeadersShouldBeEncodeable()
        {
            var buf = new byte[FrameHeader.HeaderSize];
            var bufView = new ArraySegment<byte>(buf);
            var frame = new FrameHeader
            {
                Length = 0x0340F1,
                Type = (FrameType)0xFF,
                Flags = 0x09,
                StreamId = 0x1AB83412u,
            };
            frame.EncodeInto(bufView);
            var expected = new byte[] { 0x03, 0x40, 0xF1, 0xFF, 0x09, 0x1A, 0xB8, 0x34, 0x12 };
            Assert.Equal(buf, expected);
            frame = new FrameHeader
            {
                Length = (1 << 24) - 1,
                Type = FrameType.Headers,
                Flags = 0xFF,
                StreamId = 0x7FFFFFFFu,
            };
            frame.EncodeInto(bufView);
            expected = new byte[] { 0xFF, 0xFF, 0xFF, 0x01, 0xFF, 0x7F, 0xFF, 0xFF, 0xFF };
            Assert.Equal(buf, expected);
        }
    }
}