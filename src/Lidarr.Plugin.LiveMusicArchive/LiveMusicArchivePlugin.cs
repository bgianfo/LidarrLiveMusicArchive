namespace Lidarr.Plugin.LiveMusicArchive
{
    /// <summary>
    /// Registers the plugin with Lidarr's plugin system (name, owner and update source). Lidarr installs a plugin
    /// into plugins/{owner}/{repository} and matches installed plugins to updates with ==, so Owner and Name must
    /// equal the GitHub owner and repository name exactly, case included.
    /// </summary>
    public class LiveMusicArchivePlugin : NzbDrone.Core.Plugins.Plugin
    {
        /// <inheritdoc />
        public override string Name => "LidarrLiveMusicArchive";
        /// <inheritdoc />
        public override string Owner => "bgianfo";
        /// <inheritdoc />
        public override string GithubUrl => "https://github.com/bgianfo/LidarrLiveMusicArchive";
    }
}
