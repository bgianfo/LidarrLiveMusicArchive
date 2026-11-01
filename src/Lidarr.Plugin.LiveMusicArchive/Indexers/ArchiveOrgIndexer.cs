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
    /// Offers each downloadable format of a recording as its own result, for the Live Music Archive download
    /// client. Lidarr's quality profile then picks FLAC or MP3, and only that format is downloaded.
    /// </summary>
    public class ArchiveOrgIndexer : LiveMusicArchiveIndexerBase
    {
        /// <summary>Initializes a new instance of the <see cref="ArchiveOrgIndexer"/> class.</summary>
        /// <param name="service">The Live Music Archive service.</param>
        /// <param name="client">The archive.org client, used by the connection test.</param>
        /// <param name="releaseService">Lidarr's releases, to restrict results to an album's recordings.</param>
        /// <param name="indexerStatusService">Lidarr's indexer status tracking.</param>
        /// <param name="configService">Lidarr's configuration.</param>
        /// <param name="parsingService">Lidarr's release parser.</param>
        /// <param name="logger">The logger.</param>
        public ArchiveOrgIndexer(ILiveMusicArchiveService service,
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
        public override string Name => "Live Music Archive";

        /// <inheritdoc />
        public override string Protocol => nameof(ArchiveOrgDownloadProtocol);

        /// <inheritdoc />
        public override ProviderMessage Message => new(
            "Searches the Internet Archive Live Music Archive by show date. Pair it with the Live Music Archive download client.",
            ProviderMessageType.Info);

        /// <inheritdoc />
        protected override bool Torrents => false;
    }
}
