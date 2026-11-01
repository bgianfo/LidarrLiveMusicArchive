using System.Net;
using Lidarr.Plugin.LiveMusicArchive.Core;
using Lidarr.Plugin.LiveMusicArchive.Core.Http;
using Newtonsoft.Json.Linq;
using NSubstitute;
using NzbDrone.Common.Cache;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http.Proxy;
using Xunit;

namespace Lidarr.Plugin.LiveMusicArchive.Tests
{
    /// <summary>
    /// Counts and times the archive.org requests behind refreshing one show, through the real client, cache,
    /// throttle and HTTP stack, with only the network faked.
    /// </summary>
    public sealed class RefreshCostTests : IDisposable
    {
        private const int Recordings = 8;

        private readonly string _appData = Path.Combine(Path.GetTempPath(), "lma-cost-" + Guid.NewGuid().ToString("N"));
        private readonly FakeClock _clock = new();
        private readonly Dictionary<string, DateTimeOffset> _changed = new();
        private readonly FakeHandler _network;
        private readonly ArchiveOrgClient _client;
        private LiveMusicArchiveService _service;

        public RefreshCostTests()
        {
            for (var i = 0; i < Recordings; i++)
            {
                _changed[$"tape{i}"] = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
            }

            _network = new FakeHandler(Respond);
            var http = new ArchiveOrgHttp(new ArchiveOrgThrottle(_clock), Substitute.For<IHttpProxySettingsProvider>(), _clock, TestData.Logger) { HandlerFactory = () => _network };
            var folders = Substitute.For<IAppFolderInfo>();
            folders.AppDataFolder.Returns(_appData);
            _client = new ArchiveOrgClient(http, folders, _clock, TestData.Logger);
            _service = NewService();
        }

        public void Dispose()
        {
            if (Directory.Exists(_appData))
            {
                Directory.Delete(_appData, true);
            }
        }

        [Fact]
        public async Task first_refresh_costs_one_listing_plus_the_capped_recordings_spaced_a_second_apart()
        {
            var start = _clock.Fake.GetUtcNow();

            var album = await Refresh();

            Assert.Equal(5, album.Releases.Count);
            Assert.Equal(1 + 5, _network.Requests.Count);
            Assert.True(_clock.Fake.GetUtcNow() - start >= ArchiveOrgThrottle.ApiInterval * 5);
        }

        [Fact]
        public async Task a_later_refresh_with_nothing_changed_costs_only_the_listing()
        {
            await Refresh();
            Later(TimeSpan.FromDays(2));

            await Refresh();

            Assert.Single(_network.Requests);
            Assert.Contains("/services/search/v1/scrape", _network.Requests[0].RequestUri.ToString());
        }

        [Fact]
        public async Task a_later_refresh_refetches_only_the_items_that_changed()
        {
            await Refresh();
            Later(TimeSpan.FromDays(2));
            _changed["tape3"] = _clock.Fake.GetUtcNow().AddHours(-1);

            await Refresh();

            Assert.Equal(new[] { "scrape", "tape3" }, _network.Requests.Select(r => r.RequestUri.AbsolutePath.Contains("scrape") ? "scrape" : r.RequestUri.Segments.Last()));
        }

        [Fact]
        public async Task a_refresh_within_a_day_makes_no_requests_at_all()
        {
            await Refresh();
            Later(TimeSpan.FromHours(6));

            await Refresh();

            Assert.Empty(_network.Requests);
        }

        // The service's one-hour in-memory show list runs on Lidarr's real clock; a fresh service stands in for it
        // having expired, leaving the disk cache (on the fake clock) as the thing under test.
        private void Later(TimeSpan elapsed)
        {
            _clock.Fake.Advance(elapsed);
            _service = NewService();
            _network.Requests.Clear();
        }

        private LiveMusicArchiveService NewService() => new(_client, Substitute.For<IShowArchive>(), new CacheManager(), TestData.Logger);

        private Task<NzbDrone.Core.MetadataSource.SkyHook.Resource.AlbumResource> Refresh() =>
            _clock.Run(Task.Run(() => _service.BuildShow(Artist(), "1977-05-08", new LiveMusicArchiveOptions())));

        private static JObject Artist() =>
            JObject.Parse($@"{{""id"":""{TestData.GratefulDead}"",""artistname"":""Grateful Dead"",""links"":[{{""target"":""https://archive.org/details/GratefulDead""}}],""Albums"":[]}}");

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var uri = request.RequestUri;
            if (uri.AbsolutePath.Contains("/scrape"))
            {
                var items = _changed.Select((kv, i) => new JObject
                {
                    ["identifier"] = kv.Key,
                    ["date"] = "1977-05-08T00:00:00Z",
                    ["downloads"] = 1000 - i,
                    ["oai_updatedate"] = new JArray(kv.Value.ToString("yyyy-MM-ddTHH:mm:ssZ"))
                });
                return FakeHandler.Text(new JObject { ["items"] = new JArray(items) }.ToString());
            }

            var identifier = uri.Segments.Last();
            return FakeHandler.Text(TestData.SyntheticItem(identifier, new { name = "t01.mp3", format = "VBR MP3", size = "10" }).ToString(), HttpStatusCode.OK);
        }
    }
}
