using System.Collections.Generic;
using System.Linq;

using Http2.Hpack;

namespace Http2
{
    internal enum HeaderValidationResult
    {
        /// <summary>Header fields are valid</summary>
        Ok,
        /// <summary>Invalid header field name</summary>
        ErrorInvalidHeaderFieldName,
        /// <summary>Invalid or unexpected pseudo header</summary>
        ErrorInvalidPseudoHeader,
        /// <summary>
        /// Invalid case header field name.
        /// Only lower case field names are allowed
        /// </summary>
        ErrorInvalidFieldNameCase,
        /// <summary>
        /// A Connection header, which is not valid in HTTP/2, was received
        /// </summary>
        ErrorInvalidConnectionHeader,
        /// <summary>The value of a pseudo header field is invalid</summary>
        ErrorInvalidPseudoHeaderFieldValue,
        /// <summary>The value of a header field is invalid</summary>
        ErrorInvalidHeaderFieldValue,
    }

    /// <summary>
    /// Valides header field lists according to preset rules
    /// </summary>
    internal static class HeaderValidator
    {
        /// <summary>
        /// Validates the headerfields of HTTP/2 requests
        /// </summary>
        public static HeaderValidationResult ValidateRequestHeaders(
            IEnumerable<HeaderField> headerFields)
        {
            // Search and validate all required pseudo headers here
            int nrMethod = 0;
            int nrScheme = 0;
            int nrAuthority = 0;
            int nrPath = 0;
            int nrPseudoFields = 0;

            foreach (var hf in headerFields)
            {
                if (hf.Name == null || hf.Name.Length == 0)
                {
                    return HeaderValidationResult.ErrorInvalidHeaderFieldName;
                }

                // If the header field is not a pseudo header stop here and use
                // the common validator afterwards
                if (hf.Name[0] != ':') break;

                switch (hf.Name)
                {
                    case ":method":
                        nrMethod++;
                        break;
                    case ":path":
                        nrPath++;
                        break;
                    case ":authority":
                        nrAuthority++;
                        break;
                    case ":scheme":
                        nrScheme++;
                        break;
                    default:
                        return HeaderValidationResult.ErrorInvalidPseudoHeader;
                }
                // Check if the header field is not empty
                // TODO: It may be allowed in some special scenarios that it is
                // empty. That needs to be checked out in the specifications
                if (hf.Value == null || hf.Value.Length < 1)
                {
                    return HeaderValidationResult.ErrorInvalidPseudoHeaderFieldValue;
                }
                nrPseudoFields++;
            }

            // Check if all relevant fields are set exactly one time
            // authority might be empty for asterisk requests
            if (nrMethod != 1 || nrScheme != 1 || nrPath != 1 ||(nrAuthority > 1))
            {
                return HeaderValidationResult.ErrorInvalidPseudoHeader;
            }

            return ValidateNormalHeaders(headerFields.Skip(nrPseudoFields));
        }

        /// <summary>
        /// Validates the headerfields of HTTP/2 responses
        /// </summary>
        public static HeaderValidationResult ValidateResponseHeaders(
            IEnumerable<HeaderField> headerFields)
        {
            // Search and validate all required pseudo headers here
            int nrStatus = 0;
            int nrPseudoFields = 0;

            foreach (var hf in headerFields)
            {
                if (hf.Name == null || hf.Name.Length == 0)
                {
                    return HeaderValidationResult.ErrorInvalidHeaderFieldName;
                }

                // If the header field is not a pseudo header stop here and use
                // the common validator afterwards
                if (hf.Name[0] != ':') break;

                switch (hf.Name)
                {
                    case ":status":
                        nrStatus++;
                        // Validate that the status code is a 3digit number
                        if (hf.Value == null ||
                            hf.Value.Length != 3 ||
                            hf.Value.Any(c => !char.IsNumber(c)))
                        {
                            return HeaderValidationResult.ErrorInvalidPseudoHeaderFieldValue;
                        }
                        break;
                    default:
                        return HeaderValidationResult.ErrorInvalidPseudoHeader;
                }
                // Check if the header field is not empty
                // TODO: It may be allowed in some special scenarios that it is
                // empty. That needs to be checked out in the specifications
                if (hf.Value == null || hf.Value.Length < 1)
                {
                    return HeaderValidationResult.ErrorInvalidPseudoHeaderFieldValue;
                }
                nrPseudoFields++;
            }

            // Check if all relevant fields are set exactly one time
            // authority might be empty for asterisk requests
            if (nrStatus != 1)
            {
                return HeaderValidationResult.ErrorInvalidPseudoHeader;
            }

            return ValidateNormalHeaders(headerFields.Skip(nrPseudoFields));
        }

        /// <summary>
        /// Validates HTTP trailing headers.
        /// These might not contain pseudo headers or uppercased header field names.
        /// </summary>
        public static HeaderValidationResult ValidateTrailingHeaders(
            IEnumerable<HeaderField> headerFields)
        {
            return ValidateNormalHeaders(headerFields);
        }

        /// <summary>
        /// Validates the list of non-pseudo headers.
        /// This may not contain any pseudo-header field.
        /// Additionally headerfields must be lowercased.
        /// </summary>
        public static HeaderValidationResult ValidateNormalHeaders(
            IEnumerable<HeaderField> headerFields)
        {
            foreach (var hf in headerFields)
            {
                if (hf.Name == null || hf.Name.Length == 0)
                {
                    return HeaderValidationResult.ErrorInvalidHeaderFieldName;
                }

                // Normal header fields may not start with :, because this means
                // there's a pseudo header among the normal headers
                if (hf.Name[0] == ':')
                {
                    return HeaderValidationResult.ErrorInvalidPseudoHeader;
                }

                // Check if header field name is completly lowercased
                if (hf.Name.Any(char.IsUpper))
                {
                    return HeaderValidationResult.ErrorInvalidFieldNameCase;
                }

                // Check if the value of the header field is not null
                if (hf.Value == null)
                {
                    return HeaderValidationResult.ErrorInvalidHeaderFieldValue;
                }

                // Don't allow Connection headers, which are not supported by HTTP/2
                if (hf.Name == "connection")
                {
                    return HeaderValidationResult.ErrorInvalidConnectionHeader;
                }
                if (hf.Name == "te" && hf.Value != "trailers")
                {
                    return HeaderValidationResult.ErrorInvalidConnectionHeader;
                }

                // TODO: We might want to report that error also on all other
                // connection related header fields, but which are they?
                // The spec mentions Keep-Alive, Proxy-Connection, Transfer-Encoding, and Upgrade
            }

            return HeaderValidationResult.Ok;
        }
    }
}