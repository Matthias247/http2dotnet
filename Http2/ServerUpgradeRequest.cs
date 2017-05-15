using System;
using System.Collections.Generic;
using System.Buffers;
using Http2.Hpack;

namespace Http2
{
    /// <summary>
    /// Represents the headers and payload of an upgrade request from
    /// HTTP/1.1 to HTTP/2
    /// </summary>
    public class ServerUpgradeRequest
    {
        /// <summary>
        /// The decoded settings that were included in the upgrade
        /// </summary>
        internal readonly Settings Settings;

        /// <summary>
        /// The headers including pseudo-headers that were included in the upgrade
        /// </summary>
        internal readonly List<HeaderField> Headers;

        /// <summary>The payload that was included in the upgrade</summary>
        internal readonly byte[] Payload;

        private readonly bool valid;

        /// <summary>
        /// Returns whether the upgrade request from HTTP/1 to HTTP/2 is valid.
        /// If the upgrade is not valid an application must either reject the
        /// upgrade or send a HTTP/1 response. It may not try to create a HTTP/2
        /// connection based on that upgrade request.
        /// </summary>
        public bool IsValid => valid;

        /// <summary>
        /// Constructs the ServerUpgradeRequest out ouf the information from
        /// the builder.
        /// </summary>
        internal ServerUpgradeRequest(
            Settings settings,
            List<HeaderField> headers,
            byte[] payload,
            bool valid)
        {
            this.Settings = settings;
            this.Headers = headers;
            this.Payload = payload;
            this.valid = valid;
        }
    }

    /// <summary>
    /// A builder for ServerUpgradeRequests
    /// </summary>
    public class ServerUpgradeRequestBuilder
    {
        Settings? settings;
        List<HeaderField> headers;
        ArraySegment<byte> payload;

        /// <summary>
        /// Creates a new ServerUpgradeRequestBuilder
        /// </summary>
        public ServerUpgradeRequestBuilder()
        {
        }

        /// <summary>
        /// Builds a ServerUpgradeRequest from the stored configuration.
        /// All other relevant configuration setter methods need to be called
        /// before.
        /// Only if the ServerUpgradeRequest.IsValid is true an upgrade
        /// to HTTP/2 may be performed.
        /// </summary>
        public ServerUpgradeRequest Build()
        {
            bool valid = true;
            if (settings == null) valid = false;
            
            var headers = this.headers;
            if (headers == null) valid = false;
            else
            {
                // Validate headers
                var hvr = HeaderValidator.ValidateRequestHeaders(headers);
                if (hvr != HeaderValidationResult.Ok)
                {
                    headers = null;
                    valid = false;
                }
            }

            long declaredContentLength = -1;
            if (headers != null)
            {
                declaredContentLength = headers.GetContentLength();
                // TODO: Handle invalid content lengths, which would yield
                // results like -2?
            }

            byte[] payload = null;
            if (this.payload != null && this.payload.Count > 0)
            {
                // Compare the declared content length against the actual
                // content length
                if (declaredContentLength != this.payload.Count)
                {
                    valid = false;
                }
                else
                {
                    payload = new byte[this.payload.Count];
                    Array.Copy(
                        this.payload.Array, this.payload.Offset,
                        payload, 0,
                        this.payload.Count);
                }
            }
            else if (declaredContentLength > 0)
            {
                // No payload but Content-Length header was found
                valid = false;
            }

            return new ServerUpgradeRequest(
                settings: settings ?? Settings.Default,
                headers: headers,
                payload: payload,
                valid: valid);
        }

        /// <summary>
        /// Sets the list of headers that were received with the upgrade request.
        /// This must include the pseudoheaderfields :host, :scheme, :method
        /// and optionally :authority. This means the request line from the
        /// upgrade request must be converted into pseudoheader fields before
        /// calling this methods. The pseudoheaderfields must be inserted at
        /// the beginning of the header list before any other headers.
        /// The header fields that are related to the upgrade should get filtered
        /// from the header list and should not get passed to the request builder,
        /// as they are not important for the remaining application.
        /// The remaining header fields must all utilize a lowercase headerfield
        /// name.
        /// The builder will take ownership of the list. The caller may not
        /// mutate the list after passing it to the builder.
        /// </summary>
        public ServerUpgradeRequestBuilder SetHeaders(List<HeaderField> headers)
        {
            this.headers = headers;
            return this;
        }

        /// <summary>
        /// Sets the HTTP/2 settings, which are received during the upgrade in
        /// a HTTP2-Settings header in base64 encoded string.
        /// The value of this header must be passed to this function.
        /// </summary>
        public ServerUpgradeRequestBuilder SetHttp2Settings(
            string base64EncodedHttp2Settings)
        {
            // Reset the settings to get into default-invalid state
            settings = null;

            // Work around the fact that the standard .NET API seems to support
            // only normal base64 encoding, not base64url encoding which uses
            // different characters.
            var chars = base64EncodedHttp2Settings.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (chars[i] == '_') chars[i] = '/';
                else if (chars[i] == '-') chars[i] = '+';
            }

            // This is an upgrade from a HTTP/1.1 connection
            // This contains a settings field.
            byte[] settingsBytes;
            try
            {
                settingsBytes =
                    Convert.FromBase64CharArray(chars, 0, chars.Length);
            }
            catch (Exception)
            {
                // Invalid base64 encoding
                return this;
            }

            // Deserialize the settings
            Settings newSettings = Settings.Default;
            var err = newSettings.UpdateFromData(
                new ArraySegment<byte>(settingsBytes));
            if (err == null)
            {
                // Only update the settings if deserialization did not cause error
                settings = newSettings;
            }

            return this;
        }

        /// <summary>
        /// Configures the payload that was received as the body of the upgrade
        /// request. This payload will be forwarded to the handler of Stream 1.
        /// The given view will be copied once the Build() method is invoked.
        /// </summary>
        public ServerUpgradeRequestBuilder SetPayload(
            ArraySegment<byte> payload)
        {
            this.payload = payload;
            return this;
        }
    }
}