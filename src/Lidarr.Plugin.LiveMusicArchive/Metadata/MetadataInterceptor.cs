using System.Net;
using System.Text.RegularExpressions;
using Lidarr.Plugin.LiveMusicArchive.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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

        private static readonly Regex ArtistRoute = new(@"/artist/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$", RegexOptions.Compiled);
        private static readonly Regex AlbumRoute = new(@"/album/(lma-[^/?]+)$", RegexOptions.Compiled);
        private static readonly JsonSerializerSettings SerializerSettings = new() { NullValueHandling = NullValueHandling.Ignore };

        // Lazy: IHttpClient depends on every interceptor, so eager dependencies here would be circular.
        private readonly Lazy<ILiveMusicArchiveService> _service;
        private readonly Lazy<IMetadataFactory> _metadataFactory;
        private readonly Lazy<IConfigService> _configService;
        private readonly Lazy<IHttpClient> _httpClient;
        private readonly Logger _logger;

        /// <summary>Initializes a new instance of the <see cref="MetadataInterceptor"/> class.</summary>
        /// <param name="service">The Live Music Archive service.</param>
        /// <param name="metadataFactory">Lidarr's metadata consumers, where this plugin's settings live.</param>
        /// <param name="configService">Lidarr's configuration, for a custom metadata server address.</param>
        /// <param name="httpClient">Lidarr's HTTP client, to fetch the artist for a show.</param>
        /// <param name="logger">The logger.</param>
        public MetadataInterceptor(Lazy<ILiveMusicArchiveService> service,
                                   Lazy<IMetadataFactory> metadataFactory,
                                   Lazy<IConfigService> configService,
                                   Lazy<IHttpClient> httpClient,
                                   Logger logger)
        {
            _service = service;
            _metadataFactory = metadataFactory;
            _configService = configService;
            _httpClient = httpClient;
            _logger = logger;
        }

        /// <inheritdoc />
        public HttpRequest PreRequest(HttpRequest request) => request;

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

            var artistMatch = ArtistRoute.Match(request.Url.Path);
            var albumMatch = AlbumRoute.Match(request.Url.Path);
            if (!artistMatch.Success && !albumMatch.Success)
            {
                return response;
            }

            var settings = GetSettings();
            if (settings == null)
            {
                return response;
            }

            try
            {
                if (artistMatch.Success && response.StatusCode == HttpStatusCode.OK)
                {
                    var artist = IaJson.Parse(response.Content);
                    return _service.Value.AddShows(artist, settings.ToOptions())
                        ? Replace(response, artist.ToString(Formatting.None))
                        : response;
                }

                if (albumMatch.Success && SyntheticIds.TryParseAlbumId(albumMatch.Groups[1].Value, out var artistMbid, out var showDate))
                {
                    // Upstream answered 404 for our id; answer with the show instead.
                    var artist = FetchUpstreamArtist(request, artistMbid);
                    var album = _service.Value.BuildShow(artist, showDate, settings.ToOptions());
                    return album == null ? response : Replace(response, JsonConvert.SerializeObject(album, SerializerSettings));
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

        private JObject FetchUpstreamArtist(HttpRequest albumRequest, string artistMbid)
        {
            var url = albumRequest.Url.FullUri;
            url = url[..url.LastIndexOf("/album/", StringComparison.Ordinal)] + "/artist/" + artistMbid;

            var request = new HttpRequest(url);
            request.Headers.Set(InternalHeader, "1");
            return IaJson.Parse(_httpClient.Value.Get(request).Content);
        }

        private static HttpResponse Replace(HttpResponse original, string json) =>
            new(original.Request, original.Headers, json, HttpStatusCode.OK);
    }
}
