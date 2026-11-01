using Lidarr.Plugin.LiveMusicArchive.Core;
using Newtonsoft.Json.Linq;
using NSubstitute;
using NzbDrone.Common.Cache;
using Xunit;

namespace Lidarr.Plugin.LiveMusicArchive.Tests
{
    public sealed class LiveMusicArchiveServiceTests : IDisposable
    {
        private readonly string _appData = Path.Combine(Path.GetTempPath(), "lma-service-" + Guid.NewGuid().ToString("N"));
        private readonly IArchiveOrgClient _client = Substitute.For<IArchiveOrgClient>();
        private readonly ShowArchive _archive;
        private LiveMusicArchiveService _service;

        public LiveMusicArchiveServiceTests()
        {
            var folders = Substitute.For<NzbDrone.Common.EnvironmentInfo.IAppFolderInfo>();
            folders.AppDataFolder.Returns(_appData);
            _archive = new ShowArchive(folders);
            _service = NewService();
            _client.GetRecordings("GratefulDead").Returns(new List<RecordingSummary>
            {
                TestData.Recording(TestData.SoundboardItem, "1977-05-08", downloads: 500),
                TestData.Recording(TestData.StreamOnlyFlacItem, "1977-05-08", downloads: 900),
                TestData.Recording("gd77-05-07.x", "1977-05-07"),
                TestData.Recording("gd69-11-07.x", "1969-11-07"),
                TestData.Recording("gd78-01-01.x", "1978-01-01")
            });
            _client.GetItemMetadata(TestData.SoundboardItem, Arg.Any<DateTimeOffset?>()).Returns(TestData.Item(TestData.SoundboardItem));
            _client.GetItemMetadata(TestData.StreamOnlyFlacItem, Arg.Any<DateTimeOffset?>()).Returns(TestData.Item(TestData.StreamOnlyFlacItem));
        }

        [Fact]
        public void adds_every_show_in_the_year_range()
        {
            var artist = TestData.Artist();

            Assert.True(_service.AddShows(artist, Options("1969-1977")));

            var titles = ((JArray)artist["Albums"]).Select(a => a.Value<string>("Title")).ToList();
            Assert.Contains("1977-05-08: Barton Hall, Ithaca, NY", titles);
            Assert.Contains("1977-05-07: Barton Hall, Ithaca, NY", titles);
            Assert.DoesNotContain(titles, t => t.StartsWith("1978", StringComparison.Ordinal));
        }

        [Fact]
        public void adds_shows_even_on_dates_musicbrainz_has_a_bootleg_for()
        {
            var artist = TestData.Artist();

            _service.AddShows(artist, Options(""));

            // MusicBrainz's 1969-11-07 bootleg stays, and the importable archive.org show is added beside it.
            var albums = ((JArray)artist["Albums"]).Where(a => a.Value<string>("Title").StartsWith("1969-11-07", StringComparison.Ordinal)).ToList();
            Assert.Equal(2, albums.Count);
            Assert.Single(albums, a => SyntheticIds.IsSynthetic(a.Value<string>("Id")));
        }

        [Fact]
        public void leaves_artists_without_a_collection_unchanged()
        {
            var artist = JObject.Parse(@"{""id"":""x"",""artistname"":""Nobody"",""links"":[],""Albums"":[]}");

            Assert.False(_service.AddShows(artist, Options("")));
            Assert.Empty((JArray)artist["Albums"]);
        }

        [Fact]
        public void an_empty_creator_search_result_means_no_collection()
        {
            _client.FindCollectionByCreator("Nobody").Returns("");
            var artist = JObject.Parse(@"{""id"":""x"",""artistname"":""Nobody"",""links"":[],""Albums"":[]}");

            Assert.False(_service.AddShows(artist, Options("")));
            _client.DidNotReceiveWithAnyArgs().GetRecordings(default);
        }

        [Fact]
        public void falls_back_to_a_creator_search_when_musicbrainz_has_no_archive_link()
        {
            var artist = JObject.Parse(@"{""id"":""x"",""artistname"":""Dispatch"",""links"":[],""Albums"":[]}");
            _client.FindCollectionByCreator("Dispatch").Returns("GratefulDead");

            Assert.True(_service.AddShows(artist, Options("1977")));
            Assert.NotEmpty((JArray)artist["Albums"]);
        }

        [Fact]
        public void passes_each_recordings_change_date_to_the_cache()
        {
            var changed = new DateTimeOffset(2024, 4, 22, 5, 42, 25, TimeSpan.Zero);
            _client.GetRecordings("GratefulDead").Returns(new List<RecordingSummary>
            {
                new() { Identifier = TestData.SoundboardItem, Date = "1977-05-08", UpdatedAt = changed }
            });

            _service.BuildShow(TestData.Artist(), "1977-05-08", Options(""));

            _client.Received().GetItemMetadata(TestData.SoundboardItem, changed);
        }

        [Fact]
        public void finds_torrents_when_asked()
        {
            var item = TestData.SyntheticItem(TestData.SoundboardItem,
                new { name = "t01.flac", format = "Flac", size = "10" },
                new { name = TestData.SoundboardItem + "_archive.torrent", format = "Archive BitTorrent", size = "1" });
            _client.GetItemMetadata(TestData.SoundboardItem, Arg.Any<DateTimeOffset?>()).Returns(item);

            var releases = _service.FindReleases(new[] { "https://archive.org/details/GratefulDead" }, "Grateful Dead", "Cornell", "1977-05-08", new[] { TestData.SoundboardItem }, torrents: true);

            Assert.IsType<NzbDrone.Core.Parser.Model.TorrentInfo>(Assert.Single(releases));
        }

        [Fact]
        public void builds_a_show_with_its_most_downloaded_recordings_best_first()
        {
            var album = _service.BuildShow(TestData.Artist(), "1977-05-08", Options("", maxRecordings: 2));

            Assert.Equal("1977-05-08: Barton Hall, Ithaca, NY", album.Title);
            Assert.Equal(2, album.Releases.Count);
            // Both are MP3 soundboards (the stream-only one is a SBD/AUD matrix), so downloads decide.
            Assert.Equal(new[] { "lma-" + TestData.StreamOnlyFlacItem, "lma-" + TestData.SoundboardItem }, album.Releases.Select(r => r.Id));
        }

        [Fact]
        public void unknown_show_date_builds_nothing() =>
            Assert.Null(_service.BuildShow(TestData.Artist(), "1977-05-09", Options("")));

        [Fact]
        public void finds_releases_for_the_album_recordings_only()
        {
            var links = new[] { "https://archive.org/details/GratefulDead" };

            var releases = _service.FindReleases(links, "Grateful Dead", "Cornell", "1977-05-08", new[] { TestData.SoundboardItem });

            Assert.All(releases, r => Assert.Contains(TestData.SoundboardItem, r.Title));
            Assert.NotEmpty(releases);
            _client.DidNotReceive().GetItemMetadata(TestData.StreamOnlyFlacItem, Arg.Any<DateTimeOffset?>());
        }

        [Fact]
        public void finds_nothing_for_an_album_without_releases()
        {
            var links = new[] { "https://archive.org/details/GratefulDead" };

            Assert.Empty(_service.FindReleases(links, "Grateful Dead", "Cornell", "1977-05-08", Array.Empty<string>()));
            _client.DidNotReceiveWithAnyArgs().GetRecordings(default);
        }

        [Theory]
        [InlineData("https://archive.org/details/GratefulDead", "GratefulDead")]
        [InlineData("http://www.archive.org/details/Phish?tab=about", "Phish")]
        [InlineData("https://www.jazzmusicarchives.com/artist/grateful-dead", null)]
        public void finds_collection_from_links(string link, string expected) =>
            Assert.Equal(expected, LiveMusicArchiveService.FindCollection(new[] { "https://www.wikidata.org/wiki/Q212533", link }));

        [Fact]
        public void most_common_collection_ignores_shared_collections()
        {
            var items = new[]
            {
                JObject.Parse(@"{""collection"":[""Dispatch"",""etree"",""stream_only""]}"),
                JObject.Parse(@"{""collection"":[""Dispatch"",""etree""]}"),
                JObject.Parse(@"{""collection"":[""DispatchTribute"",""etree""]}")
            };

            Assert.Equal("Dispatch", ArchiveOrgClient.MostCommonCollection(items));
            Assert.Null(ArchiveOrgClient.MostCommonCollection(Array.Empty<JObject>()));
        }

        public void Dispose()
        {
            if (Directory.Exists(_appData))
            {
                Directory.Delete(_appData, true);
            }
        }

        [Fact]
        public void a_show_removed_from_archive_org_is_kept_from_the_saved_copy()
        {
            _service.BuildShow(TestData.Artist(), "1977-05-08", Options(""));
            ListingWithout("1977-05-08");

            var kept = _service.BuildShow(TestData.Artist(), "1977-05-08", Options(""));

            Assert.NotNull(kept);
            Assert.Equal(ShowMapper.RemovedDisambiguation, kept.Disambiguation);
            Assert.Equal(2, kept.Releases.Count);
        }

        [Fact]
        public void a_removed_show_stays_in_the_artists_album_list()
        {
            _service.BuildShow(TestData.Artist(), "1977-05-08", Options(""));
            ListingWithout("1977-05-08");
            var artist = TestData.Artist();

            _service.AddShows(artist, Options("1977"));

            Assert.Contains(((JArray)artist["Albums"]).Select(a => a.Value<string>("Title")), t => t.StartsWith("1977-05-08", StringComparison.Ordinal));
        }

        [Fact]
        public void the_year_filter_still_removes_saved_shows()
        {
            _service.BuildShow(TestData.Artist(), "1977-05-08", Options(""));
            var artist = TestData.Artist();

            Assert.Null(_service.BuildShow(TestData.Artist(), "1977-05-08", Options("1978")));
            _service.AddShows(artist, Options("1978"));
            Assert.DoesNotContain(((JArray)artist["Albums"]).Select(a => a.Value<string>("Title")), t => t.StartsWith("1977-05-08", StringComparison.Ordinal));
        }

        [Fact]
        public void a_tape_removed_from_archive_org_stays_as_a_marked_release()
        {
            _service.BuildShow(TestData.Artist(), "1977-05-08", Options(""));
            ListingWithout("1977-05-07", TestData.SoundboardItem);

            var show = _service.BuildShow(TestData.Artist(), "1977-05-08", Options(""));

            var kept = Assert.Single(show.Releases, r => r.Id == SyntheticIds.ReleaseId(TestData.SoundboardItem));
            Assert.EndsWith(ShowMapper.RemovedSuffix, kept.Disambiguation);
        }

        [Fact]
        public void a_tape_that_drops_out_of_the_top_n_is_kept_unmarked()
        {
            _service.BuildShow(TestData.Artist(), "1977-05-08", Options("", maxRecordings: 2));

            var show = _service.BuildShow(TestData.Artist(), "1977-05-08", Options("", maxRecordings: 1));

            Assert.Equal(2, show.Releases.Count);
            Assert.DoesNotContain(show.Releases, r => r.Disambiguation.EndsWith(ShowMapper.RemovedSuffix, StringComparison.Ordinal));
        }

        [Fact]
        public void a_show_never_saved_and_not_listed_is_gone() =>
            Assert.Null(_service.BuildShow(TestData.Artist(), "1977-05-06", Options("")));

        // A new service stands in for the in-memory listing having expired; the saved shows are on disk.
        private void ListingWithout(string date, string identifier = null)
        {
            _client.GetRecordings("GratefulDead").Returns(new List<RecordingSummary>
            {
                TestData.Recording(TestData.SoundboardItem, "1977-05-08", downloads: 500),
                TestData.Recording(TestData.StreamOnlyFlacItem, "1977-05-08", downloads: 900),
                TestData.Recording("gd77-05-07.x", "1977-05-07")
            }.Where(r => r.Date != date && r.Identifier != identifier).ToList());
            _service = NewService();
        }

        private LiveMusicArchiveService NewService() => new(_client, _archive, new CacheManager(), TestData.Logger);

        private static LiveMusicArchiveOptions Options(string years, int maxRecordings = 10)
        {
            YearRange.TryParse(years, out var range);
            return new LiveMusicArchiveOptions { Years = range, MaxRecordingsPerShow = maxRecordings };
        }
    }
}
