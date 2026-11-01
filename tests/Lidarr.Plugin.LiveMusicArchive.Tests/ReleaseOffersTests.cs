using Lidarr.Plugin.LiveMusicArchive.Core;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Qualities;
using Xunit;

namespace Lidarr.Plugin.LiveMusicArchive.Tests
{
    public class ReleaseOffersTests
    {
        private const string Cornell = "1977-05-08: Barton Hall - Cornell University, Ithaca, NY";

        [Fact]
        public void offers_each_importable_format_once()
        {
            var item = TestData.SyntheticItem("gd77-05-08.sbd.x",
                new { name = "t01.flac", format = "Flac", size = "100" },
                new { name = "t02.flac", format = "Flac", size = "50" },
                new { name = "t01.mp3", format = "VBR MP3", size = "10" },
                new { name = "t01.ogg", format = "Ogg Vorbis", size = "5" },
                new { name = "t01.shn", format = "Shorten", size = "90" });

            var offers = ReleaseOffers.FromItem(item, "Grateful Dead", Cornell);

            Assert.Equal(new[] { "FLAC", "MP3" }, offers.Select(o => o.Codec));
            Assert.Equal(150, offers[0].Size);
            Assert.Equal("Grateful Dead - " + Cornell + " [SBD] [FLAC] [gd77-05-08.sbd.x]", offers[0].Title);
            Assert.Equal("https://archive.org/details/gd77-05-08.sbd.x", offers[0].InfoUrl);
            Assert.NotEqual(offers[0].Guid, offers[1].Guid);
        }

        [Fact]
        public void stream_only_items_offer_only_mp3()
        {
            var offers = ReleaseOffers.FromItem(TestData.Item(TestData.StreamOnlyFlacItem), "Grateful Dead", Cornell);

            Assert.Equal("MP3", Assert.Single(offers).Codec);
        }

        [Fact]
        public void item_without_identifier_offers_nothing() =>
            Assert.Empty(ReleaseOffers.FromItem(new Newtonsoft.Json.Linq.JObject(), "Grateful Dead", Cornell));

        [Theory]
        [InlineData("Flac")]
        [InlineData("24bit Flac")]
        [InlineData("VBR MP3")]
        public void download_url_round_trips(string format)
        {
            var url = ReleaseOffers.DownloadUrl("gd77-05-08.sbd.hicks", format);

            Assert.True(ReleaseOffers.TryParseDownloadUrl(url, out var identifier, out var parsedFormat));
            Assert.Equal("gd77-05-08.sbd.hicks", identifier);
            Assert.Equal(format, parsedFormat);
        }

        [Theory]
        [InlineData("https://example.com/details/x?lma-format=Flac")]
        [InlineData("https://archive.org/download/x/t01.flac")]
        [InlineData("https://archive.org/details/x")]
        [InlineData("https://archive.org/details/a%2F..%2Fb?lma-format=Flac")]
        [InlineData("not a url")]
        public void rejects_urls_it_did_not_create(string url) =>
            Assert.False(ReleaseOffers.TryParseDownloadUrl(url, out _, out _));

        [Theory]
        [InlineData("Flac", "FLAC")]
        [InlineData("24bit Flac", "FLAC 24bit")]
        [InlineData("VBR MP3", "MP3-VBR-V0")]
        public void lidarr_parses_the_quality_from_the_title(string format, string expectedQuality)
        {
            var title = ReleaseOffers.FromItem(Item(format), "Grateful Dead", Cornell)[0].Title;

            Assert.Equal(expectedQuality, QualityParser.ParseQuality(title, null, 0).Quality.Name);
        }

        [Theory]
        [InlineData(Cornell)]
        [InlineData("1969-11-07: Fillmore West, San Francisco, CA, USA")]
        [InlineData("1966\u201003\u201025: Troupers Hall, Los Angeles, CA, USA")]
        [InlineData("1977-05-08")]
        [InlineData("2007-11-26: Belly Up Tavern, Solana Beach (San Diego), CA")]
        public void lidarr_matches_the_title_to_the_searched_album(string albumTitle)
        {
            var artist = new Artist { Metadata = new ArtistMetadata { Name = "Grateful Dead" } };
            var album = new Album { Title = albumTitle };
            var title = ReleaseOffers.FromItem(Item("Flac"), "Grateful Dead", albumTitle)[0].Title;

            var parsed = Parser.ParseAlbumTitleWithSearchCriteria(title, artist, new List<Album> { album });

            Assert.NotNull(parsed);
            Assert.Equal("Grateful Dead", parsed.ArtistName);
            Assert.Equal(albumTitle, parsed.AlbumTitle);
            Assert.Equal(Quality.FLAC, parsed.Quality.Quality);
        }

        [Fact]
        public void offers_archive_orgs_torrent_for_the_whole_item()
        {
            var item = TestData.SyntheticItem("gd77-05-08.sbd.x",
                new { name = "t01.flac", format = "Flac", size = "100" },
                new { name = "t01.mp3", format = "VBR MP3", size = "10" },
                new { name = "gd77-05-08.sbd.x_archive.torrent", format = "Archive BitTorrent", size = "5" });

            var torrent = Assert.IsType<NzbDrone.Core.Parser.Model.TorrentInfo>(Assert.Single(ReleaseOffers.TorrentFromItem(item, "Grateful Dead", Cornell)));

            Assert.Equal("https://archive.org/download/gd77-05-08.sbd.x/gd77-05-08.sbd.x_archive.torrent", torrent.DownloadUrl);
            Assert.Equal(115, torrent.Size);
            Assert.EndsWith("[SBD] [FLAC] [gd77-05-08.sbd.x] [torrent]", torrent.Title);
            Assert.Equal(1, torrent.Seeders);
            Assert.Equal(Quality.FLAC, QualityParser.ParseQuality(torrent.Title, null, 0).Quality);
        }

        [Fact]
        public void restricted_items_have_no_torrent()
        {
            Assert.Empty(ReleaseOffers.TorrentFromItem(TestData.Item(TestData.StreamOnlyFlacItem), "Grateful Dead", Cornell));
            Assert.Empty(ReleaseOffers.TorrentFromItem(TestData.SyntheticItem("x", new { name = "t01.mp3", format = "VBR MP3" }), "Grateful Dead", Cornell));
        }

        [Fact]
        public void aac_m4a_recordings_are_offered_as_aac()
        {
            var offer = Assert.Single(ReleaseOffers.FromItem(TestData.Item(TestData.M4aOnlyItem), "José González", "2006-09-08: Fine Line, Minneapolis MN"));

            Assert.Contains("[AAC]", offer.Title);
            Assert.Equal("AAC", offer.Codec);
            Assert.Equal("M4A", offer.Container);
            Assert.Equal(Quality.AAC_VBR, QualityParser.ParseQuality(offer.Title, null, 0).Quality);
        }

        [Fact]
        public void lossless_m4a_is_tagged_alac()
        {
            // ~1,000 kbps: 3 minutes of CD-quality ALAC.
            var item = TestData.SyntheticItem("x", new { name = "t01.m4a", format = "MPEG-4 Audio", size = "22500000", length = "180" });

            var offer = Assert.Single(ReleaseOffers.FromItem(item, "Artist", "2006-09-08: Venue"));

            Assert.Contains("[ALAC]", offer.Title);
            Assert.Equal(Quality.ALAC, QualityParser.ParseQuality(offer.Title, null, 0).Quality);
        }

        [Theory]
        [InlineData("José González", "2007-11-26: Belly Up Tavern, Solana Beach (San Diego), CA")]
        [InlineData("Béla Fleck and the Flecktones", "1992-08-01: Café Wha?, New York, NY")]
        [InlineData("Sigur Rós", "2005-10-01: Ráðhús, Reykjavík")]
        public void lidarr_matches_titles_of_artists_and_venues_with_accents(string artistName, string albumTitle)
        {
            var artist = new Artist { Metadata = new ArtistMetadata { Name = artistName } };
            var album = new Album { Title = albumTitle };
            var title = ReleaseOffers.FromItem(Item("Flac"), artistName, albumTitle)[0].Title;

            var parsed = Parser.ParseAlbumTitleWithSearchCriteria(title, artist, new List<Album> { album });

            Assert.NotNull(parsed);
            Assert.Equal(Quality.FLAC, parsed.Quality.Quality);
        }

        private static Newtonsoft.Json.Linq.JObject Item(string format) =>
            TestData.SyntheticItem("gd77-05-08.sbd.hicks", new { name = "t01", format, size = "1" });
    }
}
