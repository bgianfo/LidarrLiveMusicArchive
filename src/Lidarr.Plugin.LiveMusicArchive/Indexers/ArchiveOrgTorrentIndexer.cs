using Lidarr.Plugin.LiveMusicArchive.Core;
using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;
using NzbDrone.Core.ThingiProvider;

namespace Lidarr.Plugin.LiveMusicArchive.Indexers
{
    /// <summary>
    /// Offers archive.org's own torrent for each recording, so your existing torrent client does the download
    /// from archive.org's web seeds and any other peers. Optional: add it instead of, or as well as, the
    /// HTTP indexer.
    /// </summary>
    public class ArchiveOrgTorrentIndexer : LiveMusicArchiveIndexerBase
    {
        /// <summary>Initializes a new instance of the <see cref="ArchiveOrgTorrentIndexer"/> class.</summary>
        /// <param name="service">The Live Music Archive service.</param>
        /// <param name="client">The archive.org client, used by the connection test.</param>
        /// <param name="releaseService">Lidarr's releases, to restrict results to an album's recordings.</param>
        /// <param name="indexerStatusService">Lidarr's indexer status tracking.</param>
        /// <param name="configService">Lidarr's configuration.</param>
        /// <param name="parsingService">Lidarr's release parser.</param>
        /// <param name="logger">The logger.</param>
        public ArchiveOrgTorrentIndexer(ILiveMusicArchiveService service,
                                        IArchiveOrgClient client,
                                        IReleaseService releaseService,
                                        IIndexerStatusService indexerStatusService,
                                        IConfigService configService,
                                        IParsingService parsingService,
                                        Logger logger)
            : base(service, client, releaseService, indexerStatusService, configService, parsingService, logger)
        {
        }

        /// <inheritdoc />
        public override string Name => "Live Music Archive (Torrent)";

        /// <inheritdoc />
        public override string Protocol => nameof(TorrentDownloadProtocol);

        /// <inheritdoc />
        public override ProviderMessage Message => new(
            "Returns archive.org's torrent for each recording, for your torrent client. Each torrent holds every format " +
            "of the recording (e.g. FLAC, MP3 and Ogg), so it is larger than an HTTP download of one format. Stream-only " +
            "recordings have no torrent.",
            ProviderMessageType.Warning);

        /// <inheritdoc />
        protected override bool Torrents => true;
    }
}
