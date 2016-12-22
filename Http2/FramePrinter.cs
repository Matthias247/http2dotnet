using System;
using System.Text;

namespace Http2
{
    /// <summary>
    /// Utilites for printing frames
    /// </summary>
    public static class FramePrinter
    {
        private static string FrameTypeString(FrameType t)
        {
            var str = Enum.GetName(typeof(FrameType), t);
            if (str == null) return "Unknown";
            return str;
        }

        private static void AddFlags(StringBuilder b, FrameHeader h)
        {
            var flagEnum = FrameFlagsMapping.GetFlagType(h.Type);
            if (flagEnum == null)
            {
                // No enumeration that defines the flags was found
                if (h.Flags == 0) return;
                b.Append("0x");
                b.Append(h.Flags.ToString("x2"));
                return;
            }

            var first = true;
            foreach (byte flagValue in Enum.GetValues(flagEnum))
            {
                if ((flagValue & h.Flags) != 0)
                {
                    // The flag is set
                    if (!first) b.Append(',');
                    first = false;
                    b.Append(Enum.GetName(flagEnum, flagValue));
                }
            }
        }

        /// <summary>
        /// Prints the frame header into human readable format
        /// </summary>
        /// <param name="h">The header to print</param>
        /// <returns>The printed header</returns>
        public static string PrintFrameHeader(FrameHeader h)
        {
            var b = new StringBuilder();
            b.Append($"{ FrameTypeString(h.Type)} flags=[");
            AddFlags(b, h);
            b.Append("] streamId=0x");
            b.Append(h.StreamId.ToString("x8"));
            b.Append(" length=");
            b.Append(h.Length);
            return b.ToString();
        }
    }
}
