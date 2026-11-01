using System.Reflection;
using Xunit;

namespace Lidarr.Plugin.LiveMusicArchive.Tests
{
    public class LiveMusicArchivePluginTests
    {
        [Fact]
        public void installed_version_is_the_plain_project_version()
        {
            // Lidarr compares InstalledVersion with the release tag (vX.Y.Z) to offer updates. A "+<sha>" suffix in the
            // informational version makes it fall back to the assembly version, which MinVer pins to Major.0.0.0.
            var informational = typeof(LiveMusicArchivePlugin).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;

            Assert.DoesNotContain("+", informational);
            Assert.Equal(Version.Parse(informational.Split('-')[0]), new LiveMusicArchivePlugin().InstalledVersion.BaseVersion);
        }

        [Fact]
        public void github_url_points_at_the_owners_repository()
        {
            // Lidarr checks for updates by fetching releases from this URL.
            var plugin = new LiveMusicArchivePlugin();

            Assert.StartsWith($"https://github.com/{plugin.Owner}/", plugin.GithubUrl);
        }

        [Fact]
        public void name_is_exactly_the_repository_name()
        {
            // Lidarr matches installed plugins to their repository with ==; a mismatch installs a second copy on update.
            var plugin = new LiveMusicArchivePlugin();

            Assert.Equal($"https://github.com/{plugin.Owner}/{plugin.Name}", plugin.GithubUrl);
            Assert.Equal("LidarrLiveMusicArchive", plugin.Name);
        }
    }
}
