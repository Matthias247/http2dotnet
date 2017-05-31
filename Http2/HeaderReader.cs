using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Http2.Hpack;
using static Http2.Hpack.DecoderExtensions;

namespace Http2
{
    /// <summary>
    /// Stores the data of a HEADE frame and all following CONTINUATION frames
    /// </summary>
    internal struct CompleteHeadersFrameData
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
    internal class HeaderReader : IDisposable
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
        IReadableByteStream reader;
        ILogger logger;

        public HeaderReader(
            Decoder hpackDecoder,
            uint maxFrameSize, uint maxHeaderFieldsSize,
            IReadableByteStream reader,
            ILogger logger
        )
        {
            this.reader = reader;
            this.hpackDecoder = hpackDecoder;
            this.maxFrameSize = maxFrameSize;
            this.maxHeaderFieldsSize = maxHeaderFieldsSize;
            this.logger = logger;
        }

        public void Dispose()
        {
            hpackDecoder.Dispose();
        }

        /// <summary>
        /// Transforms the result of an HPACK decode operation into a possible
        /// error code.
        /// </summary>
        private static Http2Error? DecodeResultToError(DecodeFragmentResult res)
        {
            if (res.Status != DecodeStatus.Success)
            {
                var errc =
                    (res.Status == DecodeStatus.MaxHeaderListSizeExceeded)
                    ? ErrorCode.ProtocolError
                    : ErrorCode.CompressionError;
                return new Http2Error
                {
                    StreamId = 0,
                    Code = errc,
                    Message = res.Status.ToString(),
                };
            }
            return null;
        }

        /// <summary>
        /// Reads and decodes a header block which consists of a single HEADER
        /// frame and 0 or more CONTINUATION frames.
        /// </summary>
        /// <param name="firstHeader">
        /// The frame header of the HEADER frame which indicates that headers
        /// must be read.
        /// </param>
        public async ValueTask<Result> ReadHeaders(
            FrameHeader firstHeader,
            Func<int, byte[]> ensureBuffer)
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
            var isPadded = f.HasFlag(HeadersFrameFlags.Padded);
            var hasPriority = f.HasFlag(HeadersFrameFlags.Priority);

            // Do a first check whether frame is big enough for the given flags
            var minLength = 0;
            if (isPadded) minLength += 1;
            if (hasPriority) minLength += 5;
            if (firstHeader.Length < minLength)
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

            // Get a buffer for the initial frame
            // TODO: We now always read the initial frame at once, but we could
            // split it up into multiple smaller reads if the receive buffer is
            // smaller. The code needs to be slightly adapted for this.
            byte[] buffer = ensureBuffer(firstHeader.Length);
            // Read the content of the initial frame
            await reader.ReadAll(new ArraySegment<byte>(buffer, 0, firstHeader.Length));

            var offset = 0;
            var padLen = 0;

            if (isPadded)
            {
                // Extract padding Length
                padLen = buffer[0];
                offset++;
            }

            if (hasPriority)
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

            // Allow table updates at the start of header header block
            // This will be reset once the first header was decoded and will
            // persist also during the continuation frame
            hpackDecoder.AllowTableSizeUpdates = true;

            // Decode headers from the first header block
            var decodeResult = hpackDecoder.DecodeHeaderBlockFragment(
                new ArraySegment<byte>(buffer, offset, contentLen),
                allowedHeadersSize,
                headers);

            var err = DecodeResultToError(decodeResult);
            if (err != null)
            {
                return new Result { Error = err };
            }

            allowedHeadersSize -= decodeResult.HeaderFieldsSize;

            while (!isEndOfHeaders)
            {
                // Read the next frame header
                // This must be a continuation frame
                // Remark: No need for ensureBuffer, since the buffer is
                // guaranteed to be larger than the first frameheader buffer
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

                var contFlags = ((ContinuationFrameFlags)contHeader.Flags);
                isEndOfHeaders = contFlags.HasFlag(ContinuationFrameFlags.EndOfHeaders);

                // Read the HeaderBlockFragment of the continuation frame
                // TODO: We now always read the frame at once, but we could
                // split it up into multiple smaller reads if the receive buffer
                // is smaller. The code needs to be slightly adapted for this.
                buffer = ensureBuffer(contHeader.Length);
                await reader.ReadAll(new ArraySegment<byte>(buffer, 0, contHeader.Length));

                offset = 0;
                contentLen = contHeader.Length;

                // Decode headers from continuation fragment
                decodeResult = hpackDecoder.DecodeHeaderBlockFragment(
                    new ArraySegment<byte>(buffer, offset, contentLen),
                    allowedHeadersSize,
                    headers);

                var err2 = DecodeResultToError(decodeResult);
                if (err2 != null)
                {
                    return new Result { Error = err2 };
                }

                allowedHeadersSize -= decodeResult.HeaderFieldsSize;
            }

            // Check if decoder is initial state, which means a complete header
            // block was received
            if (!hpackDecoder.HasInitialState)
            {
                return new Result
                {
                    Error = new Http2Error
                    {
                        Code = ErrorCode.CompressionError,
                        StreamId = 0u,
                        Message = "Received incomplete header block",
                    },
                };
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