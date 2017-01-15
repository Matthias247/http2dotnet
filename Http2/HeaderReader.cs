using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Http2.Hpack;

namespace Http2
{
    /// <summary>
    /// Stores the data of a HEADE frame and all following CONTINUATION frames
    /// </summary>
    public struct CompleteHeadersFrameData
    {
        public uint StreamId;
        public PriorityData? Priority;
        public List<HeaderField> Headers;
        public bool EndOfStream;
    }

    /// <summary>
    /// Reads and decodes a sequence of HEADERS and CONTINUATION frames into
    /// complete decoded header lists.
    /// </summary>
    public class HeaderReader
    {
        /// <summary>
        /// Stores the result of a ReadHeaders operation
        /// </summary>
        public struct Result
        {
            public Http2Error? Error;
            public CompleteHeadersFrameData HeaderData;
        }

        uint maxFrameSize;
        uint maxHeaderFieldsSize;
        Decoder hpackDecoder;
        byte[] buffer;
        IStreamReader reader;
        ILogger logger;

        public HeaderReader(
            Decoder hpackDecoder,
            uint maxFrameSize, uint maxHeaderFieldsSize,
            byte[] buffer,
            IStreamReader reader,
            ILogger logger
        )
        {
            this.reader = reader;
            this.hpackDecoder = hpackDecoder;
            this.buffer = buffer;
            this.maxFrameSize = maxFrameSize;
            this.maxHeaderFieldsSize = maxHeaderFieldsSize;
            this.logger = logger;

            if (buffer == null || buffer.Length < maxFrameSize)
                throw new ArgumentException(nameof(buffer));
        }

        /// <summary>
        /// Reads and decodes a header block which consists of a single HEADER
        /// frame and 0 or more CONTINUATION frames.
        /// </summary>
        /// <param name="firstHeader">
        /// The frame header of the HEADER frame which indicates that headers
        /// must be read.
        /// </param>
        public async ValueTask<Result> ReadHeaders(FrameHeader firstHeader)
        {
            // Check maximum frame size
            if (firstHeader.Length > maxFrameSize)
            {
                return new Result
                    {
                        Error = new Http2Error
                        {
                            StreamId = 0,
                            Code = ErrorCode.FrameSizeError,
                            Message = "Maximum frame size exceeded",
                        },
                    };
            }

            PriorityData? prioData = null;
            var allowedHeadersSize = maxHeaderFieldsSize;
            var headers = new List<HeaderField>();
            var initialFlags = firstHeader.Flags;

            var f = (HeadersFrameFlags)firstHeader.Flags;
            var isEndOfStream = f.HasFlag(HeadersFrameFlags.EndOfStream);
            var isEndOfHeaders = f.HasFlag(HeadersFrameFlags.EndOfHeaders);

            // Read the content of the initial frame
            await reader.ReadAll(new ArraySegment<byte>(buffer, 0, firstHeader.Length));

            var offset = 0;
            var padLen = 0;

            if (f.HasFlag(HeadersFrameFlags.Padded))
            {
                // Extract padding Length
                padLen = buffer[0];
                offset++;
            }

            if (f.HasFlag(HeadersFrameFlags.Priority))
            {
                // Extract priority
                prioData = PriorityData.DecodeFrom(
                    new ArraySegment<byte>(buffer, offset, 5));
                offset += 5;
            }

            var contentLen = firstHeader.Length - offset - padLen;
            if (contentLen < 0)
            {
                return new Result
                {
                    Error = new Http2Error
                    {
                        StreamId = 0,
                        Code = ErrorCode.ProtocolError,
                        Message = "Invalid frame content size",
                    },
                };
            }

            // Decode headers from the first header block
            var decodeResult = hpackDecoder.DecodeHeaderBlockFragment(
                new ArraySegment<byte>(buffer, offset, contentLen),
                allowedHeadersSize,
                headers);

            if (decodeResult.Status != DecoderExtensions.DecodeStatus.Success)
            {
                return new Result
                {
                    Error = new Http2Error
                    {
                        StreamId = 0,
                        Code = ErrorCode.ProtocolError,
                        Message = decodeResult.Status.ToString(),
                    },
                };
            }

            allowedHeadersSize -= decodeResult.HeaderFieldsSize;

            while (!isEndOfHeaders)
            {
                // Read the next frame header
                // This must be a continuation frame
                var contHeader = await FrameHeader.ReceiveAsync(reader, buffer);
                if (logger != null && logger.IsEnabled(LogLevel.Trace))
                {
                    logger.LogTrace("recv " + FramePrinter.PrintFrameHeader(contHeader));
                }
                if (contHeader.Type != FrameType.Continuation
                    || contHeader.StreamId != firstHeader.StreamId
                    || contHeader.Length > maxFrameSize
                    || contHeader.Length == 0)
                {
                    return new Result
                    {
                        Error = new Http2Error
                        {
                            StreamId = 0,
                            Code = ErrorCode.ProtocolError,
                            Message = "Invalid continuation frame",
                        },
                    };
                }

                var contFlags = ((HeadersFrameFlags)contHeader.Flags);
                isEndOfHeaders = contFlags.HasFlag(ContinuationFrameFlags.EndOfHeaders);

                // Read the HeaderBlockFragment of the continuation frame
                await reader.ReadAll(new ArraySegment<byte>(buffer, 0, contHeader.Length));

                offset = 0;
                contentLen = contHeader.Length;

                // Decode headers from the first header block
                decodeResult = hpackDecoder.DecodeHeaderBlockFragment(
                    new ArraySegment<byte>(buffer, offset, contentLen),
                    allowedHeadersSize,
                    headers);

                if (decodeResult.Status != DecoderExtensions.DecodeStatus.Success)
                {
                    return new Result
                    {
                        Error = new Http2Error
                        {
                            StreamId = 0,
                            Code = ErrorCode.ProtocolError,
                            Message = decodeResult.Status.ToString(),
                        },
                    };
                }

                allowedHeadersSize -= decodeResult.HeaderFieldsSize;
            }

            return new Result
            {
                Error = null,
                HeaderData = new CompleteHeadersFrameData
                {
                    StreamId = firstHeader.StreamId,
                    Headers = headers,
                    Priority = prioData,
                    EndOfStream = isEndOfStream,
                },
            };
        }
    }
}