using System.Security.Cryptography;
using System.Text;
using Lidarr.Plugin.LiveMusicArchive.Core.Http;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.EnvironmentInfo;

namespace Lidarr.Plugin.LiveMusicArchive.Core
{
    /// <summary>The archive.org APIs this plugin uses.</summary>
    public interface IArchiveOrgClient
    {
        /// <summary>Every etree recording in a collection, via the cursor-paged scrape API.</summary>
        /// <param name="collection">An archive.org collection, e.g. "GratefulDead".</param>
        /// <returns>The recordings, each with when archive.org last changed it.</returns>
        List<RecordingSummary> GetRecordings(string collection);

        /// <summary>
        /// The collection an artist's etree recordings live in, found by searching for the artist as creator.
        /// Used when MusicBrainz has no archive.org link for the artist.
        /// </summary>
        /// <param name="creator">The artist name.</param>
        /// <returns>The collection most of the matches belong to, or null.</returns>
        string FindCollectionByCreator(string creator);

        /// <summary>An item's metadata and file list, from cache unless archive.org has changed the item since.</summary>
        /// <param name="identifier">The archive.org identifier.</param>
        /// <param name="updatedAt">When the item last changed, from <see cref="RecordingSummary.UpdatedAt"/>; null when unknown.</param>
        /// <returns>The <c>/metadata/{identifier}</c> response.</returns>
        JObject GetItemMetadata(string identifier, DateTimeOffset? updatedAt = null);

        /// <summary>Downloads one file of an item, resuming a partial download when possible.</summary>
        /// <param name="identifier">The archive.org identifier.</param>
        /// <param name="fileName">The file name within the item.</param>
        /// <param name="destination">The local path to write.</param>
        /// <param name="cancellationToken">Cancels the download.</param>
        /// <returns>A task that completes when the file is written.</returns>
        Task DownloadFileAsync(string identifier, string fileName, string destination, CancellationToken cancellationToken);

        /// <summary>Checks that archive.org's search API answers.</summary>
        void Ping();
    }

    /// <summary>
    /// archive.org scrape, metadata and download APIs, with a change-aware on-disk cache. archive.org's
    /// metadata API sends no ETag or Last-Modified, but the scrape API reports each item's
    /// <c>oai_updatedate</c>; since we scrape the collection anyway, item metadata is refetched only when
    /// that says the item changed after our copy was saved. When archive.org can't be reached, or answers
    /// with something that can't be right (search results for another collection or artist, an empty
    /// listing, metadata for another item; all observed during a 2026-11-01 search incident), any cached copy
    /// is served regardless of age, and the bad answer is never cached.
    /// </summary>
    public class ArchiveOrgClient : IArchiveOrgClient
    {
        /// <summary>How long a collection listing (the change feed) is reused, before ±10 % jitter.</summary>
        public static readonly TimeSpan CollectionTtl = TimeSpan.FromHours(24);

        /// <summary>
        /// How long item metadata is kept even when the change feed reports no change, before ±15 % jitter.
        /// A safety net for changes the feed misses.
        /// </summary>
        public static readonly TimeSpan ItemTtl = TimeSpan.FromDays(180);

        private static readonly string[] ScrapeFields = { "identifier", "date", "venue", "coverage", "downloads", "oai_updatedate", "collection" };
        // More fields than the lookup needs, so per-query orders rarely collide (see FieldsFor).
        private static readonly string[] CreatorFields = { "identifier", "collection", "creator", "date", "title" };

        // Collections every etree item belongs to; never an artist's own collection.
        private static readonly HashSet<string> SharedCollections = new(StringComparer.OrdinalIgnoreCase) { "etree", "stream_only", "audio", "opensource_audio", "fav-collections" };

        private readonly IArchiveOrgHttp _http;
        private readonly TimeProvider _time;
        private readonly Logger _logger;
        private readonly string _cacheFolder;

        /// <summary>Initializes a new instance of the <see cref="ArchiveOrgClient"/> class.</summary>
        /// <param name="http">The throttled archive.org HTTP client.</param>
        /// <param name="appFolderInfo">Locates Lidarr's data folder for the cache.</param>
        /// <param name="clock">The clock used for cache ages.</param>
        /// <param name="logger">The logger.</param>
        public ArchiveOrgClient(IArchiveOrgHttp http, IAppFolderInfo appFolderInfo, IArchiveOrgClock clock, Logger logger)
        {
            _http = http;
            _time = clock.Time;
            _logger = logger;
            _cacheFolder = Path.Combine(appFolderInfo.AppDataFolder, "archiveorg-cache");
        }

        /// <inheritdoc />
        public List<RecordingSummary> GetRecordings(string collection)
        {
            var query = $"collection:{collection} AND mediatype:etree";
            var items = new List<RecordingSummary>();
            string cursor = null;

            do
            {
                var url = "https://archive.org/services/search/v1/scrape" +
                          $"?q={Uri.EscapeDataString(query)}&fields={FieldsFor(query, ScrapeFields)}&count=10000" +
                          (cursor != null ? $"&cursor={Uri.EscapeDataString(cursor)}" : "");
                var page = GetJson(url, Jittered(url, CollectionTtl, 0.10), changedAt: null, (answer, _) => !Items(answer).Any() || InCollection(answer, collection).Any());
                items.AddRange(InCollection(page, collection).Select(RecordingSummary.FromScrape));
                cursor = page.Value<string>("cursor");
            }
            while (cursor != null);

            return items;
        }

        /// <inheritdoc />
        public string FindCollectionByCreator(string creator)
        {
            var query = $"creator:\"{CreatorPhrase(creator)}\" AND collection:etree AND mediatype:etree";
            var url = $"https://archive.org/services/search/v1/scrape?q={Uri.EscapeDataString(query)}&fields={FieldsFor(query, CreatorFields)}&count=100";
            // A phrase search often finds only other creators (tribute bands for "Phish"), and SameArtist decides
            // whether any result is this artist, so such an answer is believable, and cached, on its own. It is not
            // believable as a replacement for a cached answer that did find the artist: that is what another query's
            // results look like when archive.org's search cache misbehaves.
            var page = GetJson(url, Jittered(url, CollectionTtl, 0.10), changedAt: null,
                (answer, cached) => ByCreator(answer, creator).Any() || cached == null || !ByCreator(cached, creator).Any());
            return MostCommonCollection(ByCreator(page, creator));
        }

        /// <summary>
        /// The phrase to search archive.org's creator field for. archive.org usually drops a leading "The" ("Disco
        /// Biscuits" for MusicBrainz's "The Disco Biscuits") and uses straight apostrophes; a phrase search is
        /// literal, so both are normalised. <see cref="SameArtist"/> then confirms each match.
        /// </summary>
        /// <param name="artistName">The artist name.</param>
        /// <returns>The search phrase.</returns>
        internal static string CreatorPhrase(string artistName)
        {
            var phrase = artistName.Trim().Replace('\u2019', '\'').Replace('\u2018', '\'').Replace("\"", "");
            return phrase.StartsWith("The ", StringComparison.OrdinalIgnoreCase) && phrase.Length > 4 ? phrase[4..] : phrase;
        }

        /// <summary>
        /// The field list for a scrape query, in an order derived from the query. On 2026-11-01 archive.org's
        /// scrape API cached responses by field list and ignored the query, so every query with the same fields got
        /// the first one's results. A per-query order keeps queries apart without extra requests; the order is
        /// stable for a query, so the plugin's own cache keys don't change. The plausibility checks still catch a clash.
        /// </summary>
        /// <param name="query">The search query.</param>
        /// <param name="fields">The fields wanted.</param>
        /// <returns>The comma-separated fields.</returns>
        internal static string FieldsFor(string query, IEnumerable<string> fields)
        {
            var seed = BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(query)), 0);
            var random = new Random(seed);
            return string.Join(",", fields.OrderBy(_ => random.Next()));
        }

        /// <summary>
        /// Whether an archive.org creator names the artist: equal once case, accents, punctuation, spacing and a
        /// leading "The" are ignored. Phrase search alone also matches tribute acts ("Chum, A Tribute to Phish").
        /// </summary>
        /// <param name="creator">The item's creator field.</param>
        /// <param name="artistName">The artist name.</param>
        /// <returns>True when they name the same artist.</returns>
        internal static bool SameArtist(string creator, string artistName) =>
            creator != null && NormalizeName(creator) == NormalizeName(artistName);

        /// <summary>The artist collection most items belong to, ignoring archive-wide collections.</summary>
        /// <param name="items">Scrape results carrying a "collection" field.</param>
        /// <returns>The collection, or null when there are no items.</returns>
        internal static string MostCommonCollection(IEnumerable<JObject> items) =>
            items.SelectMany(i => IaJson.All(i["collection"]))
                .Where(c => !SharedCollections.Contains(c))
                .GroupBy(c => c)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Key)
                .FirstOrDefault();

        /// <inheritdoc />
        public JObject GetItemMetadata(string identifier, DateTimeOffset? updatedAt = null)
        {
            var url = $"https://archive.org/metadata/{Uri.EscapeDataString(identifier)}";
            bool IsThisItem(JObject item) => IaJson.First(item["metadata"]?["identifier"]) == identifier;

            // Unlike an empty listing, metadata for the wrong item (or none, for a missing one) is never usable.
            var metadata = GetJson(url, Jittered(url, ItemTtl, 0.15), updatedAt, (answer, _) => IsThisItem(answer));
            return IsThisItem(metadata) ? metadata : throw new HttpRequestException($"archive.org returned no metadata for {identifier}");
        }

        /// <inheritdoc />
        public Task DownloadFileAsync(string identifier, string fileName, string destination, CancellationToken cancellationToken)
        {
            var path = string.Join("/", fileName.Split('/').Select(Uri.EscapeDataString));
            return _http.DownloadFileAsync($"https://archive.org/download/{Uri.EscapeDataString(identifier)}/{path}", destination, cancellationToken);
        }

        /// <inheritdoc />
        public void Ping() =>
            _http.GetStringAsync("https://archive.org/services/search/v1/scrape?q=collection%3Aetree&fields=identifier&count=100", CancellationToken.None)
                .GetAwaiter().GetResult();

        /// <summary>
        /// Spreads cache expiry by ±<paramref name="fraction"/>, the same for a given URL every time, so a
        /// catalogue cached in one go doesn't expire, and get refetched, all on the same day.
        /// </summary>
        /// <param name="url">The cached URL.</param>
        /// <param name="ttl">The nominal lifetime.</param>
        /// <param name="fraction">The jitter as a fraction of <paramref name="ttl"/>.</param>
        /// <returns>The lifetime for this URL.</returns>
        internal static TimeSpan Jittered(string url, TimeSpan ttl, double fraction)
        {
            var hash = BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(url)), 0);
            var unit = (hash / (double)uint.MaxValue * 2) - 1;
            return TimeSpan.FromTicks((long)(ttl.Ticks * (1 + (unit * fraction))));
        }

        private static IEnumerable<JObject> InCollection(JObject page, string collection) =>
            Items(page).Where(i => IaJson.All(i["collection"]).Contains(collection, StringComparer.OrdinalIgnoreCase));

        private static IEnumerable<JObject> ByCreator(JObject page, string creator) =>
            Items(page).Where(i => IaJson.All(i["creator"]).Any(c => SameArtist(c, creator)));

        private static IEnumerable<JObject> Items(JObject page) => (page["items"] as JArray ?? new JArray()).OfType<JObject>();

        private static string NormalizeName(string name)
        {
            var decomposed = name.Trim().Normalize(NormalizationForm.FormD);
            var letters = new string(decomposed.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            return letters.StartsWith("the", StringComparison.Ordinal) && name.Trim().StartsWith("the ", StringComparison.OrdinalIgnoreCase)
                ? letters[3..]
                : letters;
        }

        // A response that fails `isPlausible` (given the new answer and the cached copy, if any) is treated like a
        // failed request: the last good copy is used and
        // the bad answer is not cached.
        private JObject GetJson(string url, TimeSpan ttl, DateTimeOffset? changedAt, Func<JObject, JObject, bool> isPlausible)
        {
            var cachePath = Path.Combine(_cacheFolder, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))) + ".json");
            if (File.Exists(cachePath))
            {
                var savedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(cachePath), TimeSpan.Zero);
                var fresh = _time.GetUtcNow() - savedAt < ttl;
                var unchanged = changedAt == null || changedAt <= savedAt;
                if (fresh && unchanged)
                {
                    return IaJson.Parse(File.ReadAllText(cachePath));
                }

                _logger.Debug("Refreshing {0} ({1})", url, fresh ? "changed on archive.org" : "expired");
            }

            string content;
            try
            {
                content = _http.GetStringAsync(url, CancellationToken.None).GetAwaiter().GetResult();
                var answer = IaJson.Parse(content);

                // An empty answer is believable on its own (an artist with no shows), but not as a replacement for
                // results we already have: on 2026-11-01 archive.org also answered every search with nothing.
                var cached = File.Exists(cachePath) ? IaJson.Parse(File.ReadAllText(cachePath)) : null;
                var emptiesCachedResults = !Items(answer).Any() && cached != null && Items(cached).Any();
                if (!isPlausible(answer, cached) || emptiesCachedResults)
                {
                    if (File.Exists(cachePath))
                    {
                        _logger.Warn("archive.org returned an implausible answer for {0}; using the cached copy from {1:u}", url, File.GetLastWriteTimeUtc(cachePath));
                        return IaJson.Parse(File.ReadAllText(cachePath));
                    }

                    _logger.Warn("archive.org returned an implausible answer for {0}; not caching it", url);
                    return IaJson.Parse(content);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException || ex is IOException)
            {
                if (!File.Exists(cachePath))
                {
                    throw;
                }

                _logger.Warn("archive.org unavailable ({0}); using cached copy of {1} from {2:u}", ex.Message, url, File.GetLastWriteTimeUtc(cachePath));
                return IaJson.Parse(File.ReadAllText(cachePath));
            }

            var json = IaJson.Parse(content);

            Directory.CreateDirectory(_cacheFolder);
            File.WriteAllText(cachePath, content);
            File.SetLastWriteTimeUtc(cachePath, _time.GetUtcNow().UtcDateTime);
            return json;
        }
    }
}
