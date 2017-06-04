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
    }
}