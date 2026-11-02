using System.Net;
using System.Text.RegularExpressions;
using Lidarr.Plugin.LiveMusicArchive.Core;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Extras.Metadata;

namespace Lidarr.Plugin.LiveMusicArchive.Metadata
{
    /// <summary>
    /// Delivers Live Music Archive shows by editing Lidarr metadata-server responses. Every registered
    /// interceptor runs on every request, so this composes with SkyHookProxy and with plugins that replace
    /// the metadata provider (e.g. Tubifarry), unlike replacing IProvideArtistInfo ourselves.
    /// This is the only class that knows about the hook; the mapping lives in Core.
    /// </summary>
    public class MetadataInterceptor : IHttpRequestInterceptor
    {
        private const string DefaultMetadataHost = "api.lidarr.audio";
        // Marks our own upstream fetches so we don't re-augment them.
        private const string InternalHeader = "X-Lidarr-Plugin-LiveMusicArchive";
        // Carries a show's album id on a request PreRequest turned into an artist request.
        private const string ShowHeader = "X-Lidarr-Plugin-LiveMusicArchive-Show";

        private static readonly Regex ArtistRoute = new(@"/artist/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$", RegexOptions.Compiled);
        private static readonly Regex AlbumRoute = new(@"/album/(lma-[^/?]+)$", RegexOptions.Compiled);
        private static readonly JsonSerializerSettings SerializerSettings = new() { NullValueHandling = NullValueHandling.Ignore };

        // Lazy: IHttpClient depends on every interceptor, so eager dependencies here would be circular.
        private readonly Lazy<ILiveMusicArchiveService> _service;
        private readonly Lazy<IMetadataFactory> _metadataFactory;
        private readonly Lazy<IConfigService> _configService;
        private readonly Logger _logger;

        /// <summary>Initializes a new instance of the <see cref="MetadataInterceptor"/> class.</summary>
        /// <param name="service">The Live Music Archive service.</param>
        /// <param name="metadataFactory">Lidarr's metadata consumers, where this plugin's settings live.</param>
        /// <param name="configService">Lidarr's configuration, for a custom metadata server address.</param>
        /// <param name="logger">The logger.</param>
        public MetadataInterceptor(Lazy<ILiveMusicArchiveService> service,
                                   Lazy<IMetadataFactory> metadataFactory,
                                   Lazy<IConfigService> configService,
                                   Logger logger)
        {
            _service = service;
            _metadataFactory = metadataFactory;
            _configService = configService;
            _logger = logger;
        }

        /// <summary>
        /// Turns Lidarr's request for one of our shows into a request for the show's artist. The metadata server
        /// has never heard of our album ids, so the album request could only come back 404, and the artist is
        /// what a show is built from anyway: one request per show instead of a wasted one plus an artist fetch.
        /// </summary>
        /// <param name="request">The request Lidarr is about to send.</param>
        /// <returns>The same request, pointed at the artist when it was for a show.</returns>
        public HttpRequest PreRequest(HttpRequest request)
        {
            if (request.Headers.ContainsKey(InternalHeader) || !IsMetadataServer(request.Url))
            {
                return request;
            }

            var albumMatch = AlbumRoute.Match(request.Url.Path);
            if (!albumMatch.Success || !SyntheticIds.TryParseAlbumId(albumMatch.Groups[1].Value, out var artistMbid, out _) || GetSettings() == null)
            {
                return request;
            }

            var url = request.Url.FullUri;
            request.Url = new HttpUri(url[..url.LastIndexOf("/album/", StringComparison.Ordinal)] + "/artist/" + artistMbid);
            request.Headers.Set(ShowHeader, albumMatch.Groups[1].Value);
            return request;
        }

        /// <summary>Adds shows to artist responses and answers synthetic album requests.</summary>
        /// <param name="response">The response Lidarr received.</param>
        /// <returns>The original response, or a replacement carrying Live Music Archive data.</returns>
        public HttpResponse PostResponse(HttpResponse response)
        {
            var request = response.Request;
            if (request.Headers.ContainsKey(InternalHeader) || !IsMetadataServer(request.Url))
            {
                return response;
            }

            var showId = request.Headers.ContainsKey(ShowHeader) ? request.Headers[ShowHeader] : null;
            var artistMatch = ArtistRoute.Match(request.Url.Path);
            if (!artistMatch.Success)
            {
                return response;
            }

            var settings = GetSettings();
            if (settings == null)
            {
                // Disabled since PreRequest: answer the show request as the metadata server would have.
                return showId == null ? response : new HttpResponse(request, response.Headers, "", HttpStatusCode.NotFound);
            }

            try
            {
                if (SyntheticIds.TryParseAlbumId(showId, out _, out var showDate))
                {
                    // Anything but the artist (an outage, a removed artist) must not read as "no such album",
                    // which makes Lidarr delete the show.
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        return new HttpResponse(request, response.Headers, "", HttpStatusCode.ServiceUnavailable);
                    }

                    var album = _service.Value.BuildShow(IaJson.Parse(response.Content), showDate, settings.ToOptions());
                    return album == null
                        ? new HttpResponse(request, response.Headers, "", HttpStatusCode.NotFound)
                        : Replace(response, JsonConvert.SerializeObject(album, SerializerSettings));
                }

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var artist = IaJson.Parse(response.Content);
                    return _service.Value.AddShows(artist, settings.ToOptions())
                        ? Replace(response, artist.ToString(Formatting.None))
                        : response;
                }
            }
            catch (Exception ex)
            {
                // Passing the upstream response through here would be harmful: without our shows the artist
                // looks like it lost them, and our album ids 404 upstream; Lidarr deletes albums in both
                // cases. A 503 makes Lidarr log the failure and keep everything until the next refresh.
                _logger.Error(ex, "Live Music Archive: failed to process {0}; reporting the metadata server as unavailable so nothing is deleted", request.Url);
                return new HttpResponse(request, response.Headers, "", HttpStatusCode.ServiceUnavailable);
            }

            return response;
        }

        private bool IsMetadataServer(HttpUri url)
        {
            if (url.Host == DefaultMetadataHost)
            {
                return true;
            }

            var custom = _configService.Value.MetadataSource;
            return !string.IsNullOrWhiteSpace(custom) && url.FullUri.StartsWith(custom.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        }

        // Lidarr keeps one entry per metadata consumer, but the API can create more. If several are enabled,
        // the newest wins, since it's the one someone set up most recently.
        private LiveMusicArchiveSettings GetSettings()
        {
            var enabled = _metadataFactory.Value.All()
                .Where(d => d.Enable && d.Implementation == nameof(LiveMusicArchiveMetadata))
                .OrderByDescending(d => d.Id)
                .ToList();

            if (enabled.Count > 1)
            {
                _logger.Warn("{0} Live Music Archive metadata entries are enabled; using '{1}' (id {2}). Disable or delete the others.",
                    enabled.Count, enabled[0].Name, enabled[0].Id);
            }

            return enabled.FirstOrDefault()?.Settings as LiveMusicArchiveSettings;
        }

        private static HttpResponse Replace(HttpResponse original, string json) =>
            new(original.Request, original.Headers, json, HttpStatusCode.OK);
    }
}
