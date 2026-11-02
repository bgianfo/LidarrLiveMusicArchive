using System.Net;
using Lidarr.Plugin.LiveMusicArchive.Core;
using Lidarr.Plugin.LiveMusicArchive.Metadata;
using Newtonsoft.Json.Linq;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.MetadataSource.SkyHook.Resource;
using Xunit;

namespace Lidarr.Plugin.LiveMusicArchive.Tests
{
    public class MetadataInterceptorTests
    {
        private const string Upstream = "https://api.lidarr.audio/api/v0.4";

        private readonly ILiveMusicArchiveService _service = Substitute.For<ILiveMusicArchiveService>();
        private readonly IMetadataFactory _metadataFactory = Substitute.For<IMetadataFactory>();
        private readonly IConfigService _config = Substitute.For<IConfigService>();
        private readonly MetadataInterceptor _interceptor;

        public MetadataInterceptorTests()
        {
            _interceptor = new MetadataInterceptor(new(() => _service), new(() => _metadataFactory), new(() => _config), TestData.Logger);
            Enable(true);
        }

        [Fact]
        public void adds_shows_to_artist_responses()
        {
            _service.AddShows(Arg.Any<JObject>(), Arg.Any<LiveMusicArchiveOptions>()).Returns(ci =>
            {
                ((JArray)ci.Arg<JObject>()["Albums"]).Add(new JObject { ["Title"] = "1977-05-08: Barton Hall" });
                return true;
            });

            var response = _interceptor.PostResponse(Response($"{Upstream}/artist/{TestData.GratefulDead}", TestData.Artist().ToString()));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("1977-05-08: Barton Hall", response.Content);
            Assert.Contains("1969-11-07: Fillmore West", response.Content);
        }

        [Fact]
        public void passes_through_artists_without_shows()
        {
            var original = Response($"{Upstream}/artist/{TestData.GratefulDead}", TestData.Artist().ToString());

            Assert.Same(original, _interceptor.PostResponse(original));
        }

        [Fact]
        public void a_show_request_is_sent_as_a_request_for_its_artist()
        {
            var request = new HttpRequest($"{Upstream}/album/{SyntheticIds.AlbumId(TestData.GratefulDead, "1977-05-08")}");

            var sent = _interceptor.PreRequest(request);

            Assert.Equal($"{Upstream}/artist/{TestData.GratefulDead}", sent.Url.FullUri);
        }

        [Fact]
        public void answers_synthetic_album_requests_with_the_show()
        {
            var albumId = SyntheticIds.AlbumId(TestData.GratefulDead, "1977-05-08");
            _service.BuildShow(Arg.Is<JObject>(a => a.Value<string>("id") == TestData.GratefulDead), "1977-05-08", Arg.Any<LiveMusicArchiveOptions>())
                .Returns(new AlbumResource { Id = albumId, Title = "1977-05-08: Barton Hall", Releases = new List<ReleaseResource>() });

            var response = _interceptor.PostResponse(ShowResponse(albumId, TestData.Artist().ToString()));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("1977-05-08: Barton Hall", JObject.Parse(response.Content).Value<string>("Title"));
            // The artist response belongs to the show; it isn't also augmented as an artist.
            _service.DidNotReceiveWithAnyArgs().AddShows(default, default);
        }

        [Theory]
        [InlineData(HttpStatusCode.ServiceUnavailable)]
        [InlineData(HttpStatusCode.NotFound)]
        public void a_show_whose_artist_cannot_be_fetched_is_reported_unavailable(HttpStatusCode upstream)
        {
            var response = _interceptor.PostResponse(ShowResponse(SyntheticIds.AlbumId(TestData.GratefulDead, "1977-05-08"), "", upstream));

            // A 404 would make Lidarr delete the album.
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        [Fact]
        public void show_requests_are_left_alone_when_disabled()
        {
            Enable(false);
            var request = new HttpRequest($"{Upstream}/album/{SyntheticIds.AlbumId(TestData.GratefulDead, "1977-05-08")}");

            Assert.EndsWith("/album/" + SyntheticIds.AlbumId(TestData.GratefulDead, "1977-05-08"), _interceptor.PreRequest(request).Url.FullUri);
        }

        [Fact]
        public void a_show_request_answered_after_the_plugin_was_disabled_is_not_found()
        {
            var albumId = SyntheticIds.AlbumId(TestData.GratefulDead, "1977-05-08");
            var sent = _interceptor.PreRequest(new HttpRequest($"{Upstream}/album/{albumId}"));
            Enable(false);

            var response = _interceptor.PostResponse(new HttpResponse(sent, new HttpHeader(), TestData.Artist().ToString(), HttpStatusCode.OK));

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public void ignores_musicbrainz_album_ids()
        {
            var original = Response($"{Upstream}/album/aa86ac85-a76a-4411-9605-c269215f52b5", "{}");

            Assert.Same(original, _interceptor.PostResponse(original));
            _service.DidNotReceiveWithAnyArgs().BuildShow(default, default, default);
        }

        [Fact]
        public void does_nothing_when_disabled()
        {
            Enable(false);
            var original = Response($"{Upstream}/artist/{TestData.GratefulDead}", TestData.Artist().ToString());

            Assert.Same(original, _interceptor.PostResponse(original));
            _service.DidNotReceiveWithAnyArgs().AddShows(default, default);
        }

        [Fact]
        public void the_newest_enabled_settings_entry_wins_over_duplicates()
        {
            _metadataFactory.All().Returns(new List<MetadataDefinition>
            {
                new() { Id = 14, Enable = true, Implementation = nameof(LiveMusicArchiveMetadata), Settings = new LiveMusicArchiveSettings { Years = "" } },
                new() { Id = 15, Enable = true, Implementation = nameof(LiveMusicArchiveMetadata), Settings = new LiveMusicArchiveSettings { Years = "1977" } },
                new() { Id = 16, Enable = false, Implementation = nameof(LiveMusicArchiveMetadata), Settings = new LiveMusicArchiveSettings { Years = "1990" } }
            });

            _interceptor.PostResponse(Response($"{Upstream}/artist/{TestData.GratefulDead}", TestData.Artist().ToString()));

            _service.Received().AddShows(Arg.Any<JObject>(), Arg.Is<LiveMusicArchiveOptions>(o => o.Years.From == 1977 && o.Years.To == 1977));
        }

        [Fact]
        public void ignores_requests_to_other_hosts()
        {
            var original = Response($"https://example.com/artist/{TestData.GratefulDead}", "{}");

            Assert.Same(original, _interceptor.PostResponse(original));
            _metadataFactory.DidNotReceive().All();
        }

        [Fact]
        public void honours_a_custom_metadata_server()
        {
            _config.MetadataSource.Returns("http://localhost:5055/api/v0.4/");
            _service.AddShows(Arg.Any<JObject>(), Arg.Any<LiveMusicArchiveOptions>()).Returns(true);

            var response = _interceptor.PostResponse(Response($"http://localhost:5055/api/v0.4/artist/{TestData.GratefulDead}", TestData.Artist().ToString()));

            _service.Received().AddShows(Arg.Any<JObject>(), Arg.Any<LiveMusicArchiveOptions>());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public void a_failed_artist_is_reported_unavailable_so_lidarr_keeps_its_shows()
        {
            _service.AddShows(default, default).ThrowsForAnyArgs(new HttpRequestException("archive.org down"));

            var response = _interceptor.PostResponse(Response($"{Upstream}/artist/{TestData.GratefulDead}", TestData.Artist().ToString()));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        [Fact]
        public void a_failed_show_is_reported_unavailable_not_missing()
        {
            var albumId = SyntheticIds.AlbumId(TestData.GratefulDead, "1977-05-08");
            _service.BuildShow(default, default, default).ThrowsForAnyArgs(new HttpRequestException("archive.org down"));

            var response = _interceptor.PostResponse(ShowResponse(albumId, TestData.Artist().ToString()));

            // A 404 would make Lidarr delete the album.
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        [Fact]
        public void a_show_that_no_longer_exists_is_not_found()
        {
            var response = _interceptor.PostResponse(ShowResponse(SyntheticIds.AlbumId(TestData.GratefulDead, "1977-05-08"), TestData.Artist().ToString()));

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public void other_requests_pass_through_unchanged()
        {
            var request = new HttpRequest($"{Upstream}/artist/{TestData.GratefulDead}");

            Assert.Same(request, _interceptor.PreRequest(request));
        }

        private void Enable(bool enabled) =>
            _metadataFactory.All().Returns(new List<MetadataDefinition>
            {
                new() { Enable = enabled, Implementation = nameof(LiveMusicArchiveMetadata), Settings = new LiveMusicArchiveSettings { Years = "1977" } }
            });

        private static HttpResponse Response(string url, string content, HttpStatusCode status = HttpStatusCode.OK) =>
            new(new HttpRequest(url), new HttpHeader(), content, status);

        // The metadata server's answer to a show request, after PreRequest turned it into an artist request.
        private HttpResponse ShowResponse(string albumId, string content, HttpStatusCode status = HttpStatusCode.OK) =>
            new(_interceptor.PreRequest(new HttpRequest($"{Upstream}/album/{albumId}")), new HttpHeader(), content, status);
    }
}
