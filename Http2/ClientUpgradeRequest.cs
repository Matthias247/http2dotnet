using System;
using System.Collections.Generic;
using System.Buffers;
using System.Threading.Tasks;
using Http2.Hpack;

namespace Http2
{
    /// <summary>
    /// Represents the headers and payload of an upgrade request from
    /// HTTP/1.1 to HTTP/2
    /// </summary>
    public class ClientUpgradeRequest
    {
        /// <summary>
        /// The decoded settings that were included in the upgrade
        /// </summary>
        internal readonly Settings Settings;

        /// <summary>
        /// Returns the base64 encoded settings, which have to be sent inside
        /// Http2-Settings headerfield to the server during connection upgrade.
        /// </summary>
        public string Base64EncodedSettings => base64Settings;

        private readonly string base64Settings;
        private readonly bool valid;
        internal TaskCompletionSource<IStream> UpgradeRequestStreamTcs
            = new TaskCompletionSource<IStream>();

        /// <summary>
        /// Returns whether the upgrade request from HTTP/1 to HTTP/2 is valid.
        /// If the upgrade is not valid an application must either reject the
        /// upgrade or send a HTTP/1 response. It may not try to create a HTTP/2
        /// connection based on that upgrade request.
        /// </summary>
        public bool IsValid => valid;

        /// <summary>
        /// Returns the stream which represents the initial upgraderequest.
        /// The server will send the HTTP/2 response to the HTTP/1.1 request
        /// which triggered the upgrade inside of this stream.
        /// The stream will only be available once the Connection has been fully
        /// established, therefore this property returns an awaitable Task.
        /// </summary>
        public Task<IStream> UpgradeRequestStream => UpgradeRequestStreamTcs.Task;

        /// <summary>
        /// Constructs the ClientUpgradeRequest out ouf the information from
        /// the builder.
        /// </summary>
        internal ClientUpgradeRequest(
            Settings settings,
            string base64Settings,
            bool valid)
        {
            this.Settings = settings;
            this.base64Settings = base64Settings;
            this.valid = valid;
        }
    }

    /// <summary>
    /// A builder for ClientUpgradeRequests
    /// </summary>
    public class ClientUpgradeRequestBuilder
    {
        Settings settings = Settings.Default;

        /// <summary>
        /// Creates a new ClientUpgradeRequestBuilder
        /// </summary>
        public ClientUpgradeRequestBuilder()
        {
            settings.EnablePush = false; // Not available
        }

        /// <summary>
        /// Builds a ClientUpgradeRequest from the stored configuration.
        /// All other relevant configuration setter methods need to be called
        /// before.
        /// Only if the ClientUpgradeRequest.IsValid is true an upgrade
        /// to HTTP/2 may be performed.
        /// </summary>
        public ClientUpgradeRequest Build()
        {
            return new ClientUpgradeRequest(
                settings: settings,
                base64Settings: SettingsToBase64String(settings),
                valid: true);
        }

        /// <summary>
        /// Sets the HTTP/2 settings, which will be base64 encoded and must be
        /// sent together with the upgrade request in the Http2-Settings header.
        /// </summary>
        public ClientUpgradeRequestBuilder SetHttp2Settings(Settings settings)
        {
            if (!settings.Valid)
                throw new ArgumentException(
                    "Can not use invalid settings", nameof(settings));

            // Deactivate push - we do not support it and it will just cause
            // errors
            settings.EnablePush = false;
            this.settings = settings;
            return this;
        }

        /// <summary>
        /// Encode settings into base64 string
        /// </summary>
        private string SettingsToBase64String(Settings settings)
        {
            // Base64 encode the settings
            var settingsAsBytes = new byte[settings.RequiredSize];
            settings.EncodeInto(new ArraySegment<byte>(settingsAsBytes));
            var encodeBuf = new char[2*settings.RequiredSize];
            var encodedLength = Convert.ToBase64CharArray(
                settingsAsBytes, 0, settingsAsBytes.Length,
                encodeBuf, 0);

            // Work around the fact that the standard .NET API seems to support
            // only normal base64 encoding, not base64url encoding which uses
            // different characters.
            for (var i = 0; i < encodedLength; i++)
            {
                if (encodeBuf[i] == '/') encodeBuf[i] = '_';
                else if (encodeBuf[i] == '+') encodeBuf[i] = '-';
            }

            return new string(encodeBuf, 0, encodedLength);
        }
    }
}