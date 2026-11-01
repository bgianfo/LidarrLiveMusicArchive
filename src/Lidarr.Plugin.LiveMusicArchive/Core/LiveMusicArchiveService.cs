using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Core.MetadataSource.SkyHook.Resource;
using NzbDrone.Core.Parser.Model;

namespace Lidarr.Plugin.LiveMusicArchive.Core
{
    /// <summary>Options shared by metadata and search.</summary>
    public class LiveMusicArchiveOptions
    {
        /// <summary>Gets or sets the years whose shows are included.</summary>
        public YearRange Years { get; set; } = new();

        /// <summary>Gets or sets how many recordings (most downloaded first) represent each show.</summary>
        public int MaxRecordingsPerShow { get; set; } = 5;
    }

    /// <summary>The Live Music Archive operations the Lidarr adapters (metadata interceptor, indexer) need.</summary>
    public interface ILiveMusicArchiveService
    {
        /// <summary>Adds the artist's archive.org shows to its album list.</summary>
        /// <param name="artist">The artist JSON from the metadata server; modified in place.</param>
        /// <param name="options">Which shows to include.</param>
        /// <returns>False when the artist has no archive.org collection, leaving it unchanged.</returns>
        bool AddShows(JObject artist, LiveMusicArchiveOptions options);

        /// <summary>Builds the album for a synthetic show id.</summary>
        /// <param name="artist">The artist JSON from the metadata server.</param>
        /// <param name="showDate">The show date (yyyy-MM-dd).</param>
        /// <param name="options">Which recordings to include.</param>
        /// <returns>The album, or null when the artist played no show that day.</returns>
        AlbumResource BuildShow(JObject artist, string showDate, LiveMusicArchiveOptions options);

        /// <summary>Indexer results for one show.</summary>
        /// <param name="artistLinks">The artist's external link URLs; an archive.org link names its collection.</param>
        /// <param name="artistName">The artist name as Lidarr knows it.</param>
        /// <param name="albumTitle">The album title as Lidarr knows it.</param>
        /// <param name="showDate">The show date (yyyy-MM-dd).</param>
        /// <param name="identifiers">The recordings to offer: the album's releases, so every result can be imported.</param>
        /// <param name="torrents">True for archive.org's whole-item torrents instead of per-format HTTP downloads.</param>
        /// <returns>The results; empty when the artist has no collection or none of the recordings is listed.</returns>
        List<ReleaseInfo> FindReleases(IEnumerable<string> artistLinks, string artistName, string albumTitle, string showDate, IReadOnlyCollection<string> identifiers, bool torrents = false);
    }

    /// <summary>Default <see cref="ILiveMusicArchiveService"/>, backed by <see cref="IArchiveOrgClient"/>.</summary>
    public class LiveMusicArchiveService : ILiveMusicArchiveService
    {
        /// <summary>More shows than this without a year filter earns a warning (log and health check).</summary>
        public const int LargeCatalogue = 300;

        private static readonly Regex CollectionLink = new(@"^https?://(?:www\.)?archive\.org/details/([^/?#]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly IArchiveOrgClient _client;
        private readonly IShowArchive _archive;
        private readonly ICached<Dictionary<string, List<RecordingSummary>>> _showCache;
        private readonly Logger _logger;

        /// <summary>Initializes a new instance of the <see cref="LiveMusicArchiveService"/> class.</summary>
        /// <param name="client">The archive.org client.</param>
        /// <param name="archive">Saved copies of served shows, for recordings archive.org later removes.</param>
        /// <param name="cacheManager">Lidarr's cache manager, for the grouped show list.</param>
        /// <param name="logger">The logger.</param>
        public LiveMusicArchiveService(IArchiveOrgClient client, IShowArchive archive, ICacheManager cacheManager, Logger logger)
        {
            _client = client;
            _archive = archive;
            _showCache = cacheManager.GetCache<Dictionary<string, List<RecordingSummary>>>(GetType(), "shows");
            _logger = logger;
        }

        /// <inheritdoc />
        public bool AddShows(JObject artist, LiveMusicArchiveOptions options)
        {
            var collection = Collection(LinkTargets(artist), artist.Value<string>("artistname"));
            if (collection == null)
            {
                return false;
            }

            // Every show is added, even on dates MusicBrainz lists a bootleg for: a MusicBrainz bootleg describes
            // one specific bootleg CD, which archive.org tapes rarely match closely enough for Lidarr to import.
            // Metadata profiles decide whether MusicBrainz's bootlegs are shown as well.
            var albums = artist["Albums"] as JArray ?? new JArray();
            var artistId = artist.Value<string>("id");

            var shows = Shows(collection, options.Years);
            var added = 0;
            foreach (var (date, recordings) in shows.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                albums.Add(JObject.FromObject(ShowMapper.AlbumSummary(artistId, date, ShowCatalog.ShowTitle(date, recordings))));
                added++;
            }

            // Shows served before that archive.org has since removed stay in the catalogue, unless the
            // year filter now excludes them.
            var kept = 0;
            foreach (var saved in _archive.ForArtist(artistId))
            {
                if (SyntheticIds.TryParseAlbumId(saved.Id, out _, out var date) && !shows.ContainsKey(date) &&
                    options.Years.Contains(int.Parse(date[..4], System.Globalization.CultureInfo.InvariantCulture)))
                {
                    albums.Add(JObject.FromObject(ShowMapper.AlbumSummary(artistId, date, saved.Title)));
                    kept++;
                }
            }

            artist["Albums"] = albums;
            if (options.Years.From == null && added > LargeCatalogue)
            {
                _logger.Warn("{0} has {1} Live Music Archive shows and no year filter: the first refresh fetches metadata for up to {2} recordings. " +
                             "Set Years under Settings → Metadata → Live Music Archive to limit it.",
                    artist.Value<string>("artistname"), added, added * options.MaxRecordingsPerShow);
            }

            _logger.Info("Added {0} Live Music Archive shows to {1} from archive.org/details/{2} ({3} kept after removal from archive.org)",
                added, artist.Value<string>("artistname"), collection, kept);
            return true;
        }

        /// <inheritdoc />
        public AlbumResource BuildShow(JObject artist, string showDate, LiveMusicArchiveOptions options)
        {
            var artistId = artist.Value<string>("id");
            var albumId = SyntheticIds.AlbumId(artistId, showDate);
            if (!options.Years.Contains(int.Parse(showDate[..4], System.Globalization.CultureInfo.InvariantCulture)))
            {
                // The one deliberate removal: the user narrowed the year filter.
                return null;
            }

            var saved = _archive.Load(albumId);
            var collection = Collection(LinkTargets(artist), artist.Value<string>("artistname"));
            if (collection == null || !Shows(collection, options.Years).TryGetValue(showDate, out var recordings))
            {
                if (saved != null)
                {
                    _logger.Info("{0} is no longer on archive.org; keeping the saved copy", saved.Title);
                    return ShowMapper.MarkRemoved(saved);
                }

                return null;
            }

            var items = MostDownloaded(recordings, options.MaxRecordingsPerShow)
                .AsParallel().AsOrdered().WithDegreeOfParallelism(4)
                .Select(r => (Item: _client.GetItemMetadata(r.Identifier, r.UpdatedAt), r.Downloads))
                .ToList();
            var releases = items.Select(x => (ShowMapper.Release(x.Item, artistId, showDate), x.Downloads)).ToList();

            var show = ShowMapper.Album(artist.ToObject<ArtistResource>(), collection, showDate, ShowCatalog.ShowTitle(showDate, recordings), ShowMapper.OrderReleases(releases));
            if (Artwork.Cover(items.Select(x => x.Item)) is { } cover)
            {
                show.Images.Add(new ImageResource { CoverType = "Cover", Url = cover });
            }
            else if (saved?.Images?.Count > 0)
            {
                show.Images = saved.Images;
            }
            show = ShowMapper.KeepRemovedReleases(show, saved, recordings.Select(r => SyntheticIds.ReleaseId(r.Identifier)).ToHashSet());
            _archive.Save(show);
            return show;
        }

        /// <inheritdoc />
        public List<ReleaseInfo> FindReleases(IEnumerable<string> artistLinks, string artistName, string albumTitle, string showDate, IReadOnlyCollection<string> identifiers, bool torrents = false)
        {
            var collection = identifiers?.Count > 0 ? Collection(artistLinks, artistName) : null;
            if (collection == null || !Shows(collection, new YearRange()).TryGetValue(showDate, out var recordings))
            {
                return new List<ReleaseInfo>();
            }

            return recordings.Where(r => identifiers.Contains(r.Identifier))
                .AsParallel().AsOrdered().WithDegreeOfParallelism(4)
                .SelectMany(r => torrents
                    ? ReleaseOffers.TorrentFromItem(_client.GetItemMetadata(r.Identifier, r.UpdatedAt), artistName, albumTitle)
                    : ReleaseOffers.FromItem(_client.GetItemMetadata(r.Identifier, r.UpdatedAt), artistName, albumTitle))
                .ToList();
        }

        /// <summary>The archive.org collection named by one of an artist's links (MusicBrainz often has one).</summary>
        /// <param name="linkTargets">The artist's link URLs.</param>
        /// <returns>The collection name, or null.</returns>
        internal static string FindCollection(IEnumerable<string> linkTargets) =>
            linkTargets.Select(t => CollectionLink.Match(t ?? ""))
                .Where(m => m.Success)
                .Select(m => m.Groups[1].Value)
                .FirstOrDefault();

        // MusicBrainz's archive.org link when it has one; otherwise search archive.org by artist name.
        private string Collection(IEnumerable<string> linkTargets, string artistName) =>
            FindCollection(linkTargets) ?? (string.IsNullOrWhiteSpace(artistName) ? null : NullIfEmpty(_client.FindCollectionByCreator(artistName)));

        private static string NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

        private static IEnumerable<string> LinkTargets(JObject artist) =>
            (artist["links"] as JArray ?? new JArray()).Select(l => l.Value<string>("target"));

        private static List<RecordingSummary> MostDownloaded(IEnumerable<RecordingSummary> recordings, int max) =>
            recordings.OrderByDescending(r => r.Downloads).Take(Math.Max(1, max)).ToList();

        private Dictionary<string, List<RecordingSummary>> Shows(string collection, YearRange years) =>
            _showCache.Get($"{collection}|{years.From}-{years.To}",
                () => ShowCatalog.GroupByShow(_client.GetRecordings(collection), years),
                TimeSpan.FromHours(1));
    }
}
