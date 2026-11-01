using NLog;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Profiles.Delay;

namespace Lidarr.Plugin.LiveMusicArchive.Indexers
{
    /// <summary>
    /// The download protocol linking the Live Music Archive indexer to its download client. Lidarr hands a
    /// release only to download clients of the same protocol.
    /// </summary>
    public class ArchiveOrgDownloadProtocol : IDownloadProtocol
    {
    }

    /// <summary>
    /// Adds the protocol to every delay profile at startup. Lidarr rejects releases whose protocol a delay
    /// profile doesn't list, so without this nothing from archive.org is ever grabbed.
    /// </summary>
    public class DelayProfileRegistration : IHandle<ApplicationStartedEvent>
    {
        private readonly IDelayProfileRepository _repository;
        private readonly Logger _logger;

        /// <summary>Initializes a new instance of the <see cref="DelayProfileRegistration"/> class.</summary>
        /// <param name="repository">Lidarr's delay profiles.</param>
        /// <param name="logger">The logger.</param>
        public DelayProfileRegistration(IDelayProfileRepository repository, Logger logger)
        {
            _repository = repository;
            _logger = logger;
        }

        /// <summary>Gets the protocol name as Lidarr stores it.</summary>
        public static string Protocol => nameof(ArchiveOrgDownloadProtocol);

        /// <inheritdoc />
        public void Handle(ApplicationStartedEvent message)
        {
            foreach (var profile in _repository.All().Where(AddProtocol))
            {
                _repository.Update(profile);
                _logger.Info("Allowed Live Music Archive downloads in delay profile {0}", profile.Id);
            }
        }

        /// <summary>Adds the protocol (allowed, no delay) to a delay profile that lacks it.</summary>
        /// <param name="profile">The delay profile.</param>
        /// <returns>True when the profile was changed and needs saving.</returns>
        internal static bool AddProtocol(DelayProfile profile)
        {
            if (profile.Items.Any(i => i.Protocol == Protocol))
            {
                return false;
            }

            profile.Items.Add(new DelayProfileProtocolItem { Name = "Live Music Archive", Protocol = Protocol, Allowed = true });
            return true;
        }
    }
}
