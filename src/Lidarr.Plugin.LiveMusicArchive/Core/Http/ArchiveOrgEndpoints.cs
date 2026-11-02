namespace Lidarr.Plugin.LiveMusicArchive.Core.Http
{
    /// <summary>Where archive.org is. Every archive.org URL the plugin builds starts here.</summary>
    public static class ArchiveOrgEndpoints
    {
        /// <summary>
        /// Environment variable that points the plugin at a different archive.org, for the end-to-end tests'
        /// local fake. Read once when the plugin loads; not meant for real use.
        /// </summary>
        public const string BaseUrlVariable = "LIDARR_LMA_ARCHIVE_URL";

        /// <summary>The real archive.org.</summary>
        public const string DefaultBaseUrl = "https://archive.org";

        /// <summary>Gets the base URL, without a trailing slash.</summary>
        public static string BaseUrl { get; } = Resolve(Environment.GetEnvironmentVariable(BaseUrlVariable));

        /// <summary>Picks the base URL: the override when it's an absolute http(s) URL, otherwise archive.org.</summary>
        /// <param name="value">The environment variable's value.</param>
        /// <returns>The base URL, without a trailing slash.</returns>
        internal static string Resolve(string value) =>
            Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? value.TrimEnd('/')
                : DefaultBaseUrl;
    }
}
