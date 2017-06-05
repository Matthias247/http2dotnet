using System.Collections.Generic;
using System.Globalization;

using Http2.Hpack;

namespace Http2
{
    /// <summary>
    /// Utility methods for working with header lists
    /// </summary>
    internal static class HeaderListExtensionsUtils
    {
        /// <summary>
        /// Searches for a content-length field in the header list and extracts
        /// it's value.
        /// </summary>
        /// <returns>
        /// The value of the content-length header field.
        /// -1 if the field is absent.
        /// Other negative numbers if the value is not a valid.
        /// </returns>
        public static long GetContentLength(this IEnumerable<HeaderField> headerFields)
        {
            foreach (var hf in headerFields)
            {
                if (hf.Name == "content-length")
                {
                    long result;
                    if (!long.TryParse(
                        hf.Value, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out result) || result < 0)
                    {
                        return -2;
                    }
                    return result;
                }
            }

            return -1;
        }

        /// <summary>
        /// Returns true if the given header fields contain a :status field
        /// with a status code in the 100 range.
        /// Returns false in all other cases - also if the :status field is
        /// malformed.
        /// </summary>
        public static bool IsInformationalHeaders(
            this IEnumerable<HeaderField> headerFields)
        {
            foreach (var hf in headerFields)
            {
                if (hf.Name == ":status")
                {
                    var statusValue = hf.Value;
                    if (statusValue.Length != 3) return false;
                    return
                        statusValue[0] == '1' &&
                        statusValue[1] >= '0' && statusValue[1] <= '9' &&
                        statusValue[2] >= '0' && statusValue[2] <= '9';
                }
                else if (hf.Name.Length > 0 && hf.Name[0] != ':')
                {
                    // Don't even look at non pseudoheaders.
                    // :status must be in front of them.
                    return false;
                }
            }

            return false;
        }
    }
}