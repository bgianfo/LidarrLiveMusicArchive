using FluentValidation.Results;
using Lidarr.Plugin.LiveMusicArchive.Core;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace Lidarr.Plugin.LiveMusicArchive.Indexers
{
    /// <summary>
    /// Finds Live Music Archive recordings for album searches of shows this plugin added, identified by their
    /// synthetic album id, never by a text query. Each result is one of the album's releases, so it can be
    /// imported. MusicBrainz's own show albums are not searched: an archive.org tape rarely matches a
    /// MusicBrainz bootleg closely enough to import. Subclasses choose how recordings are delivered.
    /// </summary>
    public abstract class LiveMusicArchiveIndexerBase : IndexerBase<ArchiveOrgIndexerSettings>
    {
        private readonly ILiveMusicArchiveService _service;
        private readonly IArchiveOrgClient _client;
        private readonly IReleaseService _releaseService;

        /// <summary>Initializes a new instance of the <see cref="LiveMusicArchiveIndexerBase"/> class.</summary>
        /// <param name="service">The Live Music Archive service.</param>
        /// <param name="client">The archive.org client, used by the connection test.</param>
        /// <param name="releaseService">Lidarr's releases, to restrict results to an album's recordings.</param>
        /// <param name="indexerStatusService">Lidarr's indexer status tracking.</param>
        /// <param name="configService">Lidarr's configuration.</param>
        /// <param name="parsingService">Lidarr's release parser.</param>
        /// <param name="logger">The logger.</param>
        protected LiveMusicArchiveIndexerBase(ILiveMusicArchiveService service,
                                              IArchiveOrgClient client,
                                              IReleaseService releaseService,
                                              IIndexerStatusService indexerStatusService,
                                              IConfigService configService,
                                              IParsingService parsingService,
                                              Logger logger)
            : base(indexerStatusService, configService, parsingService, logger)
        {
            _service = service;
            _client = client;
            _releaseService = releaseService;
        }

        /// <inheritdoc />
        public override bool SupportsRss => false;

        /// <inheritdoc />
        public override bool SupportsSearch => true;

        /// <summary>Gets a value indicating whether results are whole-item torrents rather than per-format downloads.</summary>
        protected abstract bool Torrents { get; }

        /// <inheritdoc />
        public override Task<IList<ReleaseInfo>> FetchRecent() => Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo>());

        /// <inheritdoc />
        public override Task<IList<ReleaseInfo>> Fetch(ArtistSearchCriteria searchCriteria) =>
            Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo>());

        /// <inheritdoc />
        public override Task<IList<ReleaseInfo>> Fetch(AlbumSearchCriteria searchCriteria)
        {
            var links = searchCriteria.Artist?.Metadata?.Value?.Links?.Select(l => l.Url).ToList() ?? new List<string>();
            var releases = new List<ReleaseInfo>();

            foreach (var album in searchCriteria.Albums ?? new List<Album>())
            {
                if (!SyntheticIds.TryParseAlbumId(album.ForeignAlbumId, out _, out var showDate))
                {
                    continue;
                }

                var found = _service.FindReleases(links, searchCriteria.Artist.Name, album.Title, showDate, RecordingsOf(album), Torrents);
                _logger.Debug("Found {0} Live Music Archive results for {1}", found.Count, album.Title);
                releases.AddRange(found);
            }

            return Task.FromResult(CleanupReleases(releases));
        }

        /// <inheritdoc />
        public override HttpRequest GetDownloadRequest(string link) => new(link);

        /// <inheritdoc />
        protected override Task Test(List<ValidationFailure> failures)
        {
            try
            {
                _client.Ping();
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "archive.org connection test failed");
                failures.Add(new ValidationFailure(string.Empty, $"Unable to reach archive.org: {ex.Message}"));
            }

            return Task.CompletedTask;
        }

        // Offer exactly the recordings Lidarr has as releases, so every result imports.
        private List<string> RecordingsOf(Album album)
        {
            if (album.Id == 0)
            {
                return new List<string>();
            }

            return _releaseService.GetReleasesByAlbum(album.Id)
                .Select(r => SyntheticIds.TryParseReleaseId(r.ForeignReleaseId, out var identifier) ? identifier : null)
                .Where(i => i != null)
                .ToList();
        }
    }
}
