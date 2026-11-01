using Lidarr.Plugin.LiveMusicArchive.Core;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Music;
using NzbDrone.Core.Music.Events;
using NzbDrone.Core.ThingiProvider.Events;

namespace Lidarr.Plugin.LiveMusicArchive.Metadata
{
    /// <summary>
    /// Warns in System → Status when an artist has hundreds of Live Music Archive shows and no year filter
    /// is set. Every show is refetched on each artist refresh (from cache when unchanged), and the first
    /// refresh costs one archive.org request per recording.
    /// </summary>
    [CheckOn(typeof(ArtistRefreshCompleteEvent))]
    [CheckOn(typeof(ProviderUpdatedEvent<IMetadata>))]
    public class LargeCatalogueCheck : HealthCheckBase
    {
        private readonly IMetadataFactory _metadataFactory;
        private readonly IAlbumService _albumService;
        private readonly IArtistService _artistService;

        /// <summary>Initializes a new instance of the <see cref="LargeCatalogueCheck"/> class.</summary>
        /// <param name="metadataFactory">Where the plugin's settings live.</param>
        /// <param name="albumService">Lidarr's albums.</param>
        /// <param name="artistService">Lidarr's artists, for names in the message.</param>
        /// <param name="localizationService">Lidarr's localization (required by the base class).</param>
        public LargeCatalogueCheck(IMetadataFactory metadataFactory, IAlbumService albumService, IArtistService artistService, ILocalizationService localizationService)
            : base(localizationService)
        {
            _metadataFactory = metadataFactory;
            _albumService = albumService;
            _artistService = artistService;
        }

        /// <inheritdoc />
        public override HealthCheck Check()
        {
            var settings = _metadataFactory.All()
                .Where(d => d.Enable && d.Implementation == nameof(LiveMusicArchiveMetadata))
                .OrderByDescending(d => d.Id)
                .FirstOrDefault()?.Settings as LiveMusicArchiveSettings;

            if (settings == null || !string.IsNullOrWhiteSpace(settings.Years))
            {
                return new HealthCheck(GetType());
            }

            var large = _albumService.GetAllAlbums()
                .Where(a => SyntheticIds.TryParseAlbumId(a.ForeignAlbumId, out _, out _))
                .GroupBy(a => a.ArtistMetadataId)
                .Where(g => g.Count() > LiveMusicArchiveService.LargeCatalogue)
                .ToDictionary(g => g.Key, g => g.Count());

            if (large.Count == 0)
            {
                return new HealthCheck(GetType());
            }

            var names = _artistService.GetAllArtists()
                .Where(a => large.ContainsKey(a.ArtistMetadataId))
                .Select(a => $"{a.Name} ({large[a.ArtistMetadataId]} shows)");

            // Lidarr has no plugin-specific reason; ServerNotification is its generic one.
            return new HealthCheck(GetType(), HealthCheckResult.Warning, HealthCheckReason.ServerNotification,
                $"Live Music Archive has no year filter and these artists have very large catalogues: {string.Join(", ", names)}. " +
                "Set Years in Settings → Metadata → Live Music Archive to reduce load on archive.org.");
        }
    }
}
