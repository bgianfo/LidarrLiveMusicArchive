using Lidarr.Plugin.LiveMusicArchive.Core;
using NSubstitute;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.MetadataSource.SkyHook.Resource;
using Xunit;

namespace Lidarr.Plugin.LiveMusicArchive.Tests
{
    public sealed class ShowArchiveTests : IDisposable
    {
        private readonly string _appData = Path.Combine(Path.GetTempPath(), "lma-archive-" + Guid.NewGuid().ToString("N"));
        private readonly ShowArchive _archive;

        public ShowArchiveTests()
        {
            var folders = Substitute.For<IAppFolderInfo>();
            folders.AppDataFolder.Returns(_appData);
            _archive = new ShowArchive(folders);
        }

        public void Dispose()
        {
            if (Directory.Exists(_appData))
            {
                Directory.Delete(_appData, true);
            }
        }

        [Fact]
        public void saves_and_loads_shows_with_their_releases()
        {
            _archive.Save(Show("1977-05-08", "tape-a"));

            var loaded = _archive.Load(SyntheticIds.AlbumId(TestData.GratefulDead, "1977-05-08"));

            Assert.Equal("1977-05-08: Barton Hall", loaded.Title);
            Assert.Equal("lma-tape-a", Assert.Single(loaded.Releases).Id);
        }

        [Fact]
        public void lists_an_artists_saved_shows()
        {
            _archive.Save(Show("1977-05-08", "a"));
            _archive.Save(Show("1977-05-09", "b"));

            Assert.Equal(2, _archive.ForArtist(TestData.GratefulDead).Count);
            Assert.Empty(_archive.ForArtist("aa86ac85-a76a-4411-9605-c269215f52b5"));
        }

        [Fact]
        public void unknown_and_non_synthetic_ids_load_nothing()
        {
            Assert.Null(_archive.Load(SyntheticIds.AlbumId(TestData.GratefulDead, "1977-05-08")));
            Assert.Null(_archive.Load("../../etc/passwd"));
        }

        [Fact]
        public void saving_again_replaces_the_copy()
        {
            _archive.Save(Show("1977-05-08", "a"));
            _archive.Save(Show("1977-05-08", "b"));

            Assert.Equal("lma-b", Assert.Single(_archive.Load(SyntheticIds.AlbumId(TestData.GratefulDead, "1977-05-08")).Releases).Id);
        }

        private static AlbumResource Show(string date, string identifier) => new()
        {
            Id = SyntheticIds.AlbumId(TestData.GratefulDead, date),
            Title = "1977-05-08: Barton Hall",
            Releases = new List<ReleaseResource> { new() { Id = SyntheticIds.ReleaseId(identifier), Disambiguation = "SBD" } }
        };
    }
}
