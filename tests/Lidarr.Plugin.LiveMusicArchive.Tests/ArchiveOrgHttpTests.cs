using System.Net;
using System.Net.Http.Headers;
using Lidarr.Plugin.LiveMusicArchive.Core.Http;
using NSubstitute;
using NzbDrone.Common.Http.Proxy;
using Xunit;

namespace Lidarr.Plugin.LiveMusicArchive.Tests
{
    public sealed class ArchiveOrgHttpTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "lma-http-" + Guid.NewGuid().ToString("N"));
        private readonly FakeClock _clock = new();
        private readonly ArchiveOrgThrottle _throttle;

        public ArchiveOrgHttpTests()
        {
            _throttle = new ArchiveOrgThrottle(_clock);
            Directory.CreateDirectory(_folder);
        }

        public void Dispose() => Directory.Delete(_folder, true);

        [Fact]
        public void user_agent_names_the_plugin_its_version_and_the_lidarr_host()
        {
            Assert.Matches(@"^Lidarr\.Plugin\.LiveMusicArchive/\d+\.\d+\.\d+(-[0-9A-Za-z.]+)? \(\+https://github\.com/bgianfo/LidarrLiveMusicArchive\) Lidarr/[\d.]+$", ArchiveOrgHttp.UserAgent);
            Assert.True(ProductInfoHeaderValue.TryParse(ArchiveOrgHttp.UserAgent.Split(' ')[0], out _));
        }

        [Fact]
        public async Task every_request_carries_the_user_agent()
        {
            var handler = new FakeHandler(_ => FakeHandler.Text("{}"));

            await _clock.Run(Http(handler).GetStringAsync("https://archive.org/metadata/x", CancellationToken.None));

            Assert.Equal(ArchiveOrgHttp.UserAgent, Assert.Single(handler.Requests).Headers.UserAgent.ToString());
        }

        [Fact]
        public async Task honours_429_retry_after_and_pauses_all_requests()
        {
            var calls = 0;
            var handler = new FakeHandler(_ =>
            {
                if (++calls > 1)
                {
                    return FakeHandler.Text("{\"ok\":true}");
                }

                var limited = FakeHandler.Text("slow down", HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
                return limited;
            });
            var start = _clock.Fake.GetUtcNow();

            var body = await _clock.Run(Http(handler).GetStringAsync("https://archive.org/metadata/x", CancellationToken.None));

            Assert.Equal("{\"ok\":true}", body);
            Assert.True(_clock.Fake.GetUtcNow() - start >= TimeSpan.FromSeconds(30));
            Assert.True(_throttle.PausedUntil >= start + TimeSpan.FromSeconds(30));
        }

        [Fact]
        public async Task retries_server_errors_with_backoff_then_gives_up()
        {
            var handler = new FakeHandler(_ => FakeHandler.Text("oops", HttpStatusCode.BadGateway));

            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => _clock.Run(Http(handler).GetStringAsync("https://archive.org/metadata/x", CancellationToken.None)));

            Assert.Equal(HttpStatusCode.BadGateway, ex.StatusCode);
            Assert.Equal(RetryPolicy.MaxAttempts, handler.Requests.Count);
        }

        [Fact]
        public async Task does_not_retry_client_errors()
        {
            var handler = new FakeHandler(_ => FakeHandler.Text("missing", HttpStatusCode.NotFound));

            await Assert.ThrowsAsync<HttpRequestException>(() => _clock.Run(Http(handler).GetStringAsync("https://archive.org/metadata/x", CancellationToken.None)));

            Assert.Single(handler.Requests);
        }

        [Fact]
        public async Task retries_network_failures()
        {
            var calls = 0;
            var handler = new FakeHandler(_ => ++calls == 1 ? throw new HttpRequestException("connection reset") : FakeHandler.Text("{}"));

            await _clock.Run(Http(handler).GetStringAsync("https://archive.org/metadata/x", CancellationToken.None));

            Assert.Equal(2, calls);
        }

        [Fact]
        public async Task downloads_a_whole_file_and_removes_the_partial()
        {
            var dest = Path.Combine(_folder, "d1", "t01.flac");
            var handler = new FakeHandler(_ => Bytes("abcdef", HttpStatusCode.OK, "\"v1\""));

            await _clock.Run(Http(handler).DownloadFileAsync("https://archive.org/download/x/t01.flac", dest, CancellationToken.None));

            Assert.Equal("abcdef", File.ReadAllText(dest));
            Assert.False(File.Exists(dest + ".part"));
            Assert.False(File.Exists(dest + ".part.etag"));
        }

        [Fact]
        public async Task resumes_a_partial_download_when_the_file_is_unchanged()
        {
            var dest = Path.Combine(_folder, "t01.flac");
            File.WriteAllText(dest + ".part", "abc");
            File.WriteAllText(dest + ".part.etag", "\"v1\"");
            var handler = new FakeHandler(r => r.Headers.Range?.Ranges.Single().From == 3 && r.Headers.IfRange?.EntityTag?.Tag == "\"v1\""
                ? Bytes("def", HttpStatusCode.PartialContent, "\"v1\"")
                : Bytes("WRONG!", HttpStatusCode.OK, "\"v1\""));

            await _clock.Run(Http(handler).DownloadFileAsync("https://archive.org/download/x/t01.flac", dest, CancellationToken.None));

            Assert.Equal("abcdef", File.ReadAllText(dest));
        }

        [Fact]
        public async Task restarts_when_the_server_copy_changed()
        {
            var dest = Path.Combine(_folder, "t01.flac");
            File.WriteAllText(dest + ".part", "old");
            File.WriteAllText(dest + ".part.etag", "\"v1\"");
            // If-Range didn't match, so the server sends the whole new file with 200.
            var handler = new FakeHandler(_ => Bytes("brand new", HttpStatusCode.OK, "\"v2\""));

            await _clock.Run(Http(handler).DownloadFileAsync("https://archive.org/download/x/t01.flac", dest, CancellationToken.None));

            Assert.Equal("brand new", File.ReadAllText(dest));
        }

        [Fact]
        public async Task a_part_without_an_etag_is_downloaded_again_in_full()
        {
            var dest = Path.Combine(_folder, "t01.flac");
            File.WriteAllText(dest + ".part", "abc");
            var handler = new FakeHandler(_ => Bytes("abcdef", HttpStatusCode.OK, "\"v1\""));

            await _clock.Run(Http(handler).DownloadFileAsync("https://archive.org/download/x/t01.flac", dest, CancellationToken.None));

            Assert.Null(Assert.Single(handler.Requests).Headers.Range);
            Assert.Equal("abcdef", File.ReadAllText(dest));
        }

        [Fact]
        public async Task range_not_satisfiable_means_the_partial_was_already_complete()
        {
            var dest = Path.Combine(_folder, "t01.flac");
            File.WriteAllText(dest + ".part", "abcdef");
            File.WriteAllText(dest + ".part.etag", "\"v1\"");
            var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));

            await _clock.Run(Http(handler).DownloadFileAsync("https://archive.org/download/x/t01.flac", dest, CancellationToken.None));

            Assert.Equal("abcdef", File.ReadAllText(dest));
        }

        [Fact]
        public async Task restricted_files_fail_without_retrying()
        {
            var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

            await Assert.ThrowsAsync<HttpRequestException>(() => _clock.Run(Http(handler).DownloadFileAsync("https://archive.org/download/x/t01.flac", Path.Combine(_folder, "t"), CancellationToken.None)));

            Assert.Single(handler.Requests);
        }

        private ArchiveOrgHttp Http(FakeHandler handler) =>
            new(_throttle, Substitute.For<IHttpProxySettingsProvider>(), _clock, TestData.Logger) { HandlerFactory = () => handler, Random = () => 0.5 };

        private static HttpResponseMessage Bytes(string body, HttpStatusCode status, string etag)
        {
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(body)) };
            response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
            return response;
        }
    }
}
