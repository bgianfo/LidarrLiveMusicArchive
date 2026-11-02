using Lidarr.Plugin.LiveMusicArchive.Core;
using NzbDrone.Core.MetadataSource.SkyHook.Resource;
using Xunit;

namespace Lidarr.Plugin.LiveMusicArchive.Tests
{
    public class ShowMapperTests
    {
        [Fact]
        public void stream_only_item_uses_downloadable_mp3_not_private_flac()
        {
            var release = ShowMapper.Release(TestData.Item(TestData.StreamOnlyFlacItem), TestData.GratefulDead, "1977-05-08");

            Assert.Equal("VBR MP3", release.Format);
            Assert.Equal("lma-" + TestData.StreamOnlyFlacItem, release.Resource.Id);
            Assert.Equal("Bootleg", release.Resource.Status);
            Assert.Equal(release.Resource.TrackCount, release.Resource.Tracks.Count);
            Assert.Equal("Tuning", release.Resource.Tracks[0].TrackName);
            Assert.Equal(40000, release.Resource.Tracks[0].DurationMs);
        }

        [Fact]
        public void tracks_are_numbered_in_order_with_one_medium_per_disc()
        {
            var release = ShowMapper.Release(TestData.Item(TestData.SoundboardItem), TestData.GratefulDead, "1977-05-08").Resource;

            Assert.Equal("Minglewood Blues", release.Tracks[0].TrackName);
            Assert.Equal(Enumerable.Range(1, release.TrackCount), release.Tracks.Select(t => t.TrackPosition));
            Assert.Equal(release.Tracks.Select(t => t.MediumNumber).Distinct().Count(), release.Media.Count);
            Assert.All(release.Tracks, t => Assert.Equal(TestData.GratefulDead, t.ArtistId));
            Assert.StartsWith("SBD · VBR MP3", release.Disambiguation);
        }

        [Fact]
        public void track_ids_are_the_same_whichever_format_the_list_is_built_from()
        {
            var flac = TestData.SyntheticItem("x",
                new { name = "t01.flac", format = "Flac", source = "original" },
                new { name = "t01.mp3", format = "VBR MP3", source = "derivative", original = "t01.flac" });
            var mp3Only = TestData.SyntheticItem("x",
                new { name = "t01.flac", format = "Flac", source = "original", @private = "true" },
                new { name = "t01.mp3", format = "VBR MP3", source = "derivative", original = "t01.flac" });

            var fromFlac = ShowMapper.Release(flac, TestData.GratefulDead, "1977-05-08").Resource.Tracks.Single();
            var fromMp3 = ShowMapper.Release(mp3Only, TestData.GratefulDead, "1977-05-08").Resource.Tracks.Single();

            Assert.Equal(fromFlac.Id, fromMp3.Id);
            Assert.Equal(fromFlac.RecordingId, fromMp3.RecordingId);
        }

        [Fact]
        public void tracks_list_their_pre_v031_ids_so_lidarr_migrates_them()
        {
            var item = TestData.SyntheticItem("x",
                new { name = "t01.flac", format = "Flac", source = "original", @private = "true" },
                new { name = "t01.mp3", format = "VBR MP3", source = "derivative", original = "t01.flac" });

            var track = ShowMapper.Release(item, TestData.GratefulDead, "1977-05-08").Resource.Tracks.Single();

            Assert.Equal(SyntheticIds.LegacyTrackIds("x", "t01.flac", "t01.mp3"), track.OldIds);
            Assert.Equal(SyntheticIds.LegacyRecordingIds("x", "t01.flac", "t01.mp3"), track.OldRecordingIds);
            Assert.Equal(2, track.OldIds.Count);
        }

        [Fact]
        public void originals_list_only_the_one_earlier_id()
        {
            var track = ShowMapper.Release(TestData.SyntheticItem("x", new { name = "t01.flac", format = "Flac", source = "original" }), TestData.GratefulDead, "1977-05-08").Resource.Tracks.Single();

            Assert.Single(track.OldIds);
            Assert.DoesNotContain(track.Id, track.OldIds);
        }

        [Fact]
        public void item_without_audio_is_not_a_release() =>
            Assert.Null(ShowMapper.Release(TestData.SyntheticItem("x", new { name = "a.png", format = "PNG" }), TestData.GratefulDead, "1977-05-08"));

        [Fact]
        public void album_summary_is_a_live_bootleg_broadcast()
        {
            var summary = ShowMapper.AlbumSummary(TestData.GratefulDead, "1977-05-08", "1977-05-08: Barton Hall");

            Assert.Equal(SyntheticIds.AlbumId(TestData.GratefulDead, "1977-05-08"), summary.Id);
            Assert.Equal("Broadcast", summary.Type);
            Assert.Equal(new[] { "Live" }, summary.SecondaryTypes);
            Assert.Equal(new[] { "Bootleg" }, summary.ReleaseStatuses);
            Assert.Equal(new DateTime(1977, 5, 8, 0, 0, 0, DateTimeKind.Utc), summary.ReleaseDate);
        }

        [Fact]
        public void album_embeds_the_artist_without_its_album_list()
        {
            var artist = TestData.Artist().ToObject<ArtistResource>();
            var release = ShowMapper.Release(TestData.Item(TestData.SoundboardItem), TestData.GratefulDead, "1977-05-08").Resource;

            var album = ShowMapper.Album(artist, "GratefulDead", "1977-05-08", "1977-05-08: Barton Hall", new[] { release });

            Assert.Equal(TestData.GratefulDead, album.ArtistId);
            Assert.Null(Assert.Single(album.Artists).Albums);
            Assert.Same(release, Assert.Single(album.Releases));
            Assert.Contains("collection%3AGratefulDead", album.Links[0].Target);
            Assert.NotNull(album.Images);
        }

        [Fact]
        public void lossless_soundboard_tapes_are_ordered_first()
        {
            static MappedRelease R(string id, string format, string lineage) => new() { Format = format, Lineage = lineage, Resource = new() { Id = id } };

            var ordered = ShowMapper.OrderReleases(new[]
            {
                (R("mp3-sbd", "VBR MP3", "SBD"), 9000L),
                (R("flac-aud", "Flac", "AUD"), 5000L),
                (R("flac-sbd-less", "Flac", "SBD"), 10L),
                (R("flac-sbd-more", "Flac", "SBD"), 100L),
                ((MappedRelease)null, 0L)
            });

            Assert.Equal(new[] { "flac-sbd-more", "flac-sbd-less", "flac-aud", "mp3-sbd" }, ordered.Select(r => r.Id));
        }

        [Theory]
        [InlineData("gd77-05-08.sbd.hicks.4982.sbeok.shnf", "", "SBD")]
        [InlineData("gd1977-05-08.aud.moore.berger.28354.flac16", "", "AUD")]
        [InlineData("gd1977-05-08.mtx.dan.29511.flac16", "Soundboard + audience matrix", "SBD")]
        [InlineData("gd1977-05-08.ecm33p.moore", "2 Sony ECM-33P mics > Nakamichi 550", "AUD")]
        [InlineData("gd1977-05-08.shure57.stevenson", "", null)]
        public void guesses_lineage(string identifier, string source, string expected) =>
            Assert.Equal(expected, ShowMapper.Lineage(identifier, source));


        [Fact]
        public void band_cover_prefers_the_poster_then_fanart()
        {
            var artist = new ArtistResource
            {
                Images = new List<ImageResource>
                {
                    new() { CoverType = "Logo", Url = "https://x/logo.png" },
                    new() { CoverType = "Fanart", Url = "https://x/fanart.jpg" },
                    new() { CoverType = "Poster", Url = "https://x/poster.jpg" }
                }
            };

            Assert.Equal("https://x/poster.jpg", ShowMapper.BandCover(artist).Url);
            artist.Images.RemoveAll(i => i.CoverType == "Poster");
            Assert.Equal("https://x/fanart.jpg", ShowMapper.BandCover(artist).Url);
            Assert.Equal("Cover", ShowMapper.BandCover(artist).CoverType);
        }

        [Fact]
        public void band_cover_is_null_when_the_artist_has_no_picture()
        {
            Assert.Null(ShowMapper.BandCover(new ArtistResource { Images = new List<ImageResource> { new() { CoverType = "Logo", Url = "https://x/logo.png" } } }));
            Assert.Null(ShowMapper.BandCover(new ArtistResource()));
        }
    }
}
