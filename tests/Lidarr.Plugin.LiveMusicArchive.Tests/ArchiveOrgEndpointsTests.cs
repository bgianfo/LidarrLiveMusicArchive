using Lidarr.Plugin.LiveMusicArchive.Core.Http;
using Xunit;

namespace Lidarr.Plugin.LiveMusicArchive.Tests
{
    public class ArchiveOrgEndpointsTests
    {
        [Theory]
        [InlineData(null, "https://archive.org")]
        [InlineData("", "https://archive.org")]
        [InlineData("not a url", "https://archive.org")]
        [InlineData("ftp://example.com", "https://archive.org")]
        [InlineData("http://127.0.0.1:5000/", "http://127.0.0.1:5000")]
        [InlineData("https://mirror.example.org", "https://mirror.example.org")]
        public void the_override_must_be_an_http_url(string value, string expected) =>
            Assert.Equal(expected, ArchiveOrgEndpoints.Resolve(value));

        [Fact]
        public void without_the_override_it_is_archive_org() =>
            Assert.Equal(ArchiveOrgEndpoints.DefaultBaseUrl, ArchiveOrgEndpoints.BaseUrl);
    }
}
