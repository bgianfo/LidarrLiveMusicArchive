using Lidarr.Plugin.LiveMusicArchive.Core;
using Lidarr.Plugin.LiveMusicArchive.Core.Http;
using NSubstitute;
using NzbDrone.Common.EnvironmentInfo;
using Xunit;

namespace Lidarr.Plugin.LiveMusicArchive.Tests
{
    public sealed class ArchiveOrgClientTests : IDisposable
    {
        private readonly string _appData = Path.Combine(Path.GetTempPath(), "lma-client-" + Guid.NewGuid().ToString("N"));
        private readonly IArchiveOrgHttp _http = Substitute.For<IArchiveOrgHttp>();
        private readonly FakeClock _clock = new();
        private readonly List<string> _requests = new();
        private readonly ArchiveOrgClient _client;

        public ArchiveOrgClientTests()
        {
            var folders = Substitute.For<IAppFolderInfo>();
            folders.AppDataFolder.Returns(_appData);
            _client = new ArchiveOrgClient(_http, folders, _clock, TestData.Logger);
        }

        public void Dispose()
        {
            if (Directory.Exists(_appData))
            {
                Directory.Delete(_appData, true);
            }
        }

        [Fact]
        public void unchanged_items_are_served_from_cache()
        {
            Serve(_ => Item("gd77"));
            var updated = _clock.Fake.GetUtcNow().AddDays(-400);

            _client.GetItemMetadata("gd77", updated);
            _clock.Fake.Advance(TimeSpan.FromDays(100));
            _client.GetItemMetadata("gd77", updated);

            Assert.Single(_requests);
        }

        [Fact]
        public void items_changed_since_they_were_cached_are_refetched()
        {
            Serve(_ => Item("gd77"));

            _client.GetItemMetadata("gd77", _clock.Fake.GetUtcNow().AddDays(-400));
            _clock.Fake.Advance(TimeSpan.FromDays(3));
            _client.GetItemMetadata("gd77", _clock.Fake.GetUtcNow().AddDays(-1));

            Assert.Equal(2, _requests.Count);
        }

        [Fact]
        public void items_are_refetched_after_the_safety_ttl_even_if_unchanged()
        {
            Serve(_ => Item("gd77"));
            var updated = _clock.Fake.GetUtcNow().AddDays(-400);

            _client.GetItemMetadata("gd77", updated);
            _clock.Fake.Advance(ArchiveOrgClient.ItemTtl * 1.2);
            _client.GetItemMetadata("gd77", updated);

            Assert.Equal(2, _requests.Count);
        }

        [Fact]
        public void an_outage_serves_the_cached_copy_however_old()
        {
            Serve(_ => Item("gd77"));
            _client.GetItemMetadata("gd77");
            _clock.Fake.Advance(ArchiveOrgClient.ItemTtl * 2);
            _http.GetStringAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<Task<string>>(_ => throw new HttpRequestException("archive.org down"));

            var item = _client.GetItemMetadata("gd77");

            Assert.Equal("gd77", item["metadata"].Value<string>("identifier"));
        }

        [Fact]
        public void an_outage_with_nothing_cached_fails()
        {
            _http.GetStringAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<Task<string>>(_ => throw new HttpRequestException("archive.org down"));

            Assert.Throws<HttpRequestException>(() => _client.GetItemMetadata("gd77"));
        }

        [Fact]
        public void dates_in_cached_json_survive()
        {
            Serve(_ => @"{""metadata"":{""identifier"":""gd77"",""date"":""1977-05-08T00:00:00Z""},""files"":[]}");

            _client.GetItemMetadata("gd77");
            var cached = _client.GetItemMetadata("gd77");

            Assert.Equal("1977-05-08T00:00:00Z", cached["metadata"].Value<string>("date"));
        }

        [Fact]
        public void follows_scrape_cursors_and_reads_change_dates()
        {
            Serve(url => url.Contains("cursor=")
                ? @"{""items"":[{""identifier"":""b"",""date"":""1977-05-08T00:00:00Z"",""oai_updatedate"":[""2005-09-16T22:36:25Z"",""2024-04-22T05:42:25Z"",""2011-10-04T00:38:04Z""]}]}"
                : @"{""items"":[{""identifier"":""a"",""date"":""1977-05-07T00:00:00Z""}],""cursor"":""next page""}");

            var recordings = _client.GetRecordings("GratefulDead");

            Assert.Equal(new[] { "a", "b" }, recordings.Select(r => r.Identifier));
            Assert.Null(recordings[0].UpdatedAt);
            Assert.Equal(new DateTimeOffset(2024, 4, 22, 5, 42, 25, TimeSpan.Zero), recordings[1].UpdatedAt);
            Assert.Contains("cursor=next%20page", _requests[1]);
            Assert.Contains("oai_updatedate", _requests[0]);
        }

        [Fact]
        public void collection_listings_are_reused_for_about_a_day()
        {
            Serve(_ => @"{""items"":[]}");

            _client.GetRecordings("GratefulDead");
            _clock.Fake.Advance(TimeSpan.FromHours(20));
            _client.GetRecordings("GratefulDead");
            Assert.Single(_requests);

            _clock.Fake.Advance(TimeSpan.FromHours(8));
            _client.GetRecordings("GratefulDead");
            Assert.Equal(2, _requests.Count);
        }

        [Fact]
        public void cache_lifetimes_are_jittered_per_url_but_stable()
        {
            var lifetimes = Enumerable.Range(0, 50).Select(i => ArchiveOrgClient.Jittered($"https://archive.org/metadata/item{i}", TimeSpan.FromDays(100), 0.15)).ToList();

            Assert.All(lifetimes, t => Assert.InRange(t.TotalDays, 85, 115));
            Assert.True(lifetimes.Distinct().Count() > 40);
            Assert.Equal(lifetimes[0], ArchiveOrgClient.Jittered("https://archive.org/metadata/item0", TimeSpan.FromDays(100), 0.15));
        }

        [Fact]
        public void creator_search_quotes_the_name_and_strips_quotes_from_it()
        {
            Serve(_ => @"{""items"":[{""collection"":[""Dispatch"",""etree""]}]}");

            Assert.Equal("Dispatch", _client.FindCollectionByCreator("Dis\"patch"));
            Assert.Contains("creator%3A%22Dispatch%22", Assert.Single(_requests));
        }

        [Fact]
        public async Task downloads_escape_each_path_segment()
        {
            await _client.DownloadFileAsync("dispatch1996-04-16.shnf", "d1.shnf/t 01.mp3", "/tmp/x", CancellationToken.None);

            await _http.Received().DownloadFileAsync("https://archive.org/download/dispatch1996-04-16.shnf/d1.shnf/t%2001.mp3", "/tmp/x", Arg.Any<CancellationToken>());
        }

        private static string Item(string identifier) => $@"{{""metadata"":{{""identifier"":""{identifier}""}},""files"":[]}}";

        private void Serve(Func<string, string> body) =>
            _http.GetStringAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci =>
            {
                var url = ci.Arg<string>();
                _requests.Add(url);
                return Task.FromResult(body(url));
            });
    }
}
