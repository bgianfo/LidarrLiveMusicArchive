using Lidarr.Plugin.LiveMusicArchive.Core;
using Lidarr.Plugin.LiveMusicArchive.Indexers;
using NSubstitute;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using Xunit;

namespace Lidarr.Plugin.LiveMusicArchive.Tests
{
    public class ArchiveOrgIndexerTests
    {
        private readonly ILiveMusicArchiveService _service = Substitute.For<ILiveMusicArchiveService>();
        private readonly IArchiveOrgClient _client = Substitute.For<IArchiveOrgClient>();
        private readonly IReleaseService _releases = Substitute.For<IReleaseService>();
        private readonly ArchiveOrgIndexer _indexer;

        public ArchiveOrgIndexerTests()
        {
            _indexer = new ArchiveOrgIndexer(_service, _client, _releases, Substitute.For<IIndexerStatusService>(), Substitute.For<IConfigService>(), Substitute.For<IParsingService>(), TestData.Logger)
            {
                Definition = new IndexerDefinition { Id = 7, Name = "Live Music Archive", Settings = new ArchiveOrgIndexerSettings() }
            };
            _service.FindReleases(default, default, default, default, default, default)
                .ReturnsForAnyArgs(ci => new List<ReleaseInfo> { new() { Guid = "g-" + ci.ArgAt<string>(3), Title = "t" } });
        }

        [Fact]
        public async Task searches_plugin_shows_for_exactly_their_releases()
        {
            var album = new Album { Id = 5, ForeignAlbumId = SyntheticIds.AlbumId(TestData.GratefulDead, "1977-05-08"), Title = "1977-05-08: Barton Hall" };
            _releases.GetReleasesByAlbum(5).Returns(new List<AlbumRelease>
            {
                new() { ForeignReleaseId = SyntheticIds.ReleaseId("tape-a") },
                new() { ForeignReleaseId = "c1620f35-f4b9-400f-bf9e-65207770a52c" }
            });

            var results = await _indexer.Fetch(Criteria(album));

            _service.Received().FindReleases(
                Arg.Is<IEnumerable<string>>(l => l.Contains("https://archive.org/details/GratefulDead")),
                "Grateful Dead", album.Title, "1977-05-08",
                Arg.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(new[] { "tape-a" })), false);
            var release = Assert.Single(results);
            Assert.Equal("7_g-1977-05-08", release.Guid);
            Assert.Equal(nameof(ArchiveOrgDownloadProtocol), release.DownloadProtocol);
        }

        [Fact]
        public async Task does_not_search_musicbrainz_shows_because_their_downloads_would_not_import()
        {
            var album = new Album { Id = 6, ForeignAlbumId = "ac3b135e-9c03-4639-a44e-bba6eb717694", Title = "1969-11-07: Fillmore West, San Francisco, CA, USA" };

            Assert.Empty(await _indexer.Fetch(Criteria(album)));
            _service.DidNotReceiveWithAnyArgs().FindReleases(default, default, default, default, default, default);
        }

        [Fact]
        public async Task ignores_albums_that_are_not_shows()
        {
            var results = await _indexer.Fetch(Criteria(new Album { ForeignAlbumId = "aa86ac85-a76a-4411-9605-c269215f52b5", Title = "Touch of Grey" }));

            Assert.Empty(results);
            _service.DidNotReceiveWithAnyArgs().FindReleases(default, default, default, default, default, default);
        }

        [Fact]
        public async Task the_torrent_indexer_asks_for_torrents_under_the_torrent_protocol()
        {
            var torrents = new ArchiveOrgTorrentIndexer(_service, _client, _releases, Substitute.For<IIndexerStatusService>(), Substitute.For<IConfigService>(), Substitute.For<IParsingService>(), TestData.Logger)
            {
                Definition = new IndexerDefinition { Id = 8, Name = "LMA torrents", Settings = new ArchiveOrgIndexerSettings() }
            };
            var album = new Album { Id = 6, ForeignAlbumId = SyntheticIds.AlbumId(TestData.GratefulDead, "1969-11-07"), Title = "1969-11-07: Fillmore West" };
            _releases.GetReleasesByAlbum(6).Returns(new List<AlbumRelease> { new() { ForeignReleaseId = SyntheticIds.ReleaseId("tape-b") } });

            var results = await torrents.Fetch(Criteria(album));

            _service.Received().FindReleases(Arg.Any<IEnumerable<string>>(), "Grateful Dead", album.Title, "1969-11-07", Arg.Any<IReadOnlyCollection<string>>(), true);
            Assert.Equal(nameof(NzbDrone.Core.Indexers.TorrentDownloadProtocol), Assert.Single(results).DownloadProtocol);
        }

        [Fact]
        public async Task artist_and_rss_searches_return_nothing()
        {
            Assert.Empty(await _indexer.FetchRecent());
            Assert.Empty(await _indexer.Fetch(new ArtistSearchCriteria()));
        }

        [Fact]
        public void connection_test_reports_archive_org_failures()
        {
            _client.When(c => c.Ping()).Throw(new HttpRequestException("offline"));

            var result = _indexer.Test();

            Assert.False(result.IsValid);
            Assert.Contains("offline", result.Errors[0].ErrorMessage);
        }

        [Fact]
        public void connection_test_passes_when_archive_org_answers() =>
            Assert.True(_indexer.Test().IsValid);

        private static AlbumSearchCriteria Criteria(Album album) => new()
        {
            Artist = new Artist
            {
                Metadata = new ArtistMetadata
                {
                    Name = "Grateful Dead",
                    Links = new List<Links> { new() { Name = "archive", Url = "https://archive.org/details/GratefulDead" } }
                }
            },
            Albums = new List<Album> { album }
        };
    }
}
