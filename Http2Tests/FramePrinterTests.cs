using Http2;
using Xunit;

namespace Http2Tests
{
    public class FramePrinterTests
    {
        [Fact]
        public void ShouldPrintFramesWithASingleFlag()
        {
            var fh = new FrameHeader
            {
                Type = FrameType.Continuation,
                Flags = (byte)ContinuationFrameFlags.EndOfHeaders,
                StreamId = 0x123,
                Length = 999,
            };
            var res = FramePrinter.PrintFrameHeader(fh);
            Assert.Equal("Continuation flags=[EndOfHeaders] streamId=0x00000123 length=999", res);
        }

        [Fact]
        public void ShouldPrintFramesWithMultipleFlags()
        {
            var fh = new FrameHeader
            {
                Type = FrameType.Data,
                Flags = (byte)(DataFrameFlags.EndOfStream | DataFrameFlags.Padded),
                StreamId = 0x123,
                Length = 999,
            };
            var res = FramePrinter.PrintFrameHeader(fh);
            Assert.Equal("Data flags=[EndOfStream,Padded] streamId=0x00000123 length=999", res);
        }

        [Fact]
        public void ShouldPrintFramesWithoutFlags()
        {
            var fh = new FrameHeader
            {
                Type = FrameType.ResetStream,
                Flags = 0,
                StreamId = 0x7FFFFFFF,
                Length = 999,
            };
            var res = FramePrinter.PrintFrameHeader(fh);
            Assert.Equal("ResetStream flags=[] streamId=0x7fffffff length=999", res);
        }

        [Fact]
        public void ShouldPrintFramesWithUnknownFlags()
        {
            var fh = new FrameHeader
            {
                Type = FrameType.ResetStream,
                Flags = 0x43,
                StreamId = 0x123,
                Length = 999,
            };
            var res = FramePrinter.PrintFrameHeader(fh);
            Assert.Equal("ResetStream flags=[0x43] streamId=0x00000123 length=999", res);
        }
    }
}
