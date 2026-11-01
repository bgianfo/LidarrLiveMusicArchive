using Lidarr.Plugin.LiveMusicArchive.Core;
using Lidarr.Plugin.LiveMusicArchive.Metadata;
using NSubstitute;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Music;
using Xunit;

namespace Lidarr.Plugin.LiveMusicArchive.Tests
{
    public class LargeCatalogueCheckTests
    {
        private readonly IMetadataFactory _metadata = Substitute.For<IMetadataFactory>();
        private readonly IAlbumService _albums = Substitute.For<IAlbumService>();
        private readonly IArtistService _artists = Substitute.For<IArtistService>();
        private readonly LargeCatalogueCheck _check;

        public LargeCatalogueCheckTests()
        {
            _check = new LargeCatalogueCheck(_metadata, _albums, _artists, Substitute.For<ILocalizationService>());
            _artists.GetAllArtists().Returns(new List<Artist> { new() { ArtistMetadataId = 1, Metadata = new ArtistMetadata { Name = "Grateful Dead" } } });
        }

        [Fact]
        public void warns_about_large_catalogues_without_a_year_filter()
        {
            Settings("");
            Shows(LiveMusicArchiveService.LargeCatalogue + 1);

            var result = _check.Check();

            Assert.Equal(HealthCheckResult.Warning, result.Type);
            Assert.Contains("Grateful Dead (301 shows)", result.Message);
        }

        [Fact]
        public void a_year_filter_silences_the_warning()
        {
            Settings("1977");
            Shows(2000);

            Assert.Equal(HealthCheckResult.Ok, _check.Check().Type);
        }

        [Fact]
        public void small_catalogues_are_fine()
        {
            Settings("");
            Shows(LiveMusicArchiveService.LargeCatalogue);

            Assert.Equal(HealthCheckResult.Ok, _check.Check().Type);
        }

        [Fact]
        public void disabled_plugin_is_fine()
        {
            _metadata.All().Returns(new List<MetadataDefinition>());
            Shows(2000);

            Assert.Equal(HealthCheckResult.Ok, _check.Check().Type);
        }

        private void Settings(string years) =>
            _metadata.All().Returns(new List<MetadataDefinition>
            {
                new() { Id = 1, Enable = true, Implementation = nameof(LiveMusicArchiveMetadata), Settings = new LiveMusicArchiveSettings { Years = years } }
            });

        private void Shows(int count) =>
            _albums.GetAllAlbums().Returns(Enumerable.Range(0, count)
                .Select(i => new Album { ArtistMetadataId = 1, ForeignAlbumId = SyntheticIds.AlbumId(TestData.GratefulDead, DateTime.Parse("1970-01-01").AddDays(i).ToString("yyyy-MM-dd")) })
                .Append(new Album { ArtistMetadataId = 1, ForeignAlbumId = "aa86ac85-a76a-4411-9605-c269215f52b5" })
                .ToList());
    }
}
