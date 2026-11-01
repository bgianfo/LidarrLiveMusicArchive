using Lidarr.Plugin.LiveMusicArchive.Core;
using Xunit;

namespace Lidarr.Plugin.LiveMusicArchive.Tests
{
    public class SyntheticIdsTests
    {
        [Fact]
        public void album_id_round_trips()
        {
            var id = SyntheticIds.AlbumId(TestData.GratefulDead, "1977-05-08");

            Assert.Equal($"lma-{TestData.GratefulDead}-19770508", id);
            Assert.True(SyntheticIds.TryParseAlbumId(id, out var mbid, out var date));
            Assert.Equal(TestData.GratefulDead, mbid);
            Assert.Equal("1977-05-08", date);
        }

        [Theory]
        [InlineData("aa86ac85-a76a-4411-9605-c269215f52b5")]
        [InlineData("lma-6faa7ca7-0d99-4a5e-bfa6-1fd5037520c6-19770532")]
        [InlineData("lma-6faa7ca7-0d99-4a5e-bfa6-1fd5037520c6-1977050")]
        [InlineData("lma-gd77-05-08.sbd.hicks.4982.sbeok.shnf")]
        [InlineData("")]
        [InlineData(null)]
        public void rejects_ids_that_are_not_synthetic_albums(string id) =>
            Assert.False(SyntheticIds.TryParseAlbumId(id, out _, out _));

        [Fact]
        public void release_id_round_trips()
        {
            var id = SyntheticIds.ReleaseId(TestData.SoundboardItem);

            Assert.True(SyntheticIds.TryParseReleaseId(id, out var identifier));
            Assert.Equal(TestData.SoundboardItem, identifier);
        }

        [Theory]
        [InlineData("c1620f35-f4b9-400f-bf9e-65207770a52c")]
        [InlineData("lma-")]
        [InlineData("lma-6faa7ca7-0d99-4a5e-bfa6-1fd5037520c6-19770508")]
        [InlineData(null)]
        public void release_id_parsing_rejects_foreign_and_album_ids(string id) =>
            Assert.False(SyntheticIds.TryParseReleaseId(id, out _));

        [Fact]
        public void track_and_recording_ids_are_stable_distinct_and_prefixed()
        {
            var track = SyntheticIds.TrackId("item", "d1t01.flac");

            Assert.Equal(track, SyntheticIds.TrackId("item", "d1t01.flac"));
            Assert.NotEqual(track, SyntheticIds.TrackId("item", "d1t02.flac"));
            Assert.NotEqual(track, SyntheticIds.RecordingId("item", "d1t01.flac"));
            // Prefixed so nothing mistakes it for a MusicBrainz id; the rest is still a version-5 UUID.
            Assert.StartsWith("lma-", track);
            Assert.True(Guid.TryParse(track["lma-".Length..], out _));
            Assert.Equal('5', track["lma-".Length + 14]);
        }

        [Fact]
        public void legacy_ids_are_the_bare_uuids_earlier_versions_issued()
        {
            var legacy = SyntheticIds.LegacyTrackIds("item", "t01.flac", "t01.mp3");

            Assert.Equal(2, legacy.Count);
            Assert.All(legacy, id => Assert.True(Guid.TryParse(id, out _)));
            Assert.Equal(SyntheticIds.TrackId("item", "t01.flac"), "lma-" + legacy[0]);
            Assert.Single(SyntheticIds.LegacyTrackIds("item", "t01.flac", "t01.flac"));
        }

        [Theory]
        [InlineData("lma-gd77", true)]
        [InlineData("6faa7ca7-0d99-4a5e-bfa6-1fd5037520c6", false)]
        [InlineData(null, false)]
        public void recognises_synthetic_ids(string id, bool synthetic) =>
            Assert.Equal(synthetic, SyntheticIds.IsSynthetic(id));
    }
}
