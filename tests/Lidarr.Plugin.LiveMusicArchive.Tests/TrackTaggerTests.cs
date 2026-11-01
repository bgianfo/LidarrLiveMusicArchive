using Lidarr.Plugin.LiveMusicArchive.Core.Downloads;
using Xunit;

namespace Lidarr.Plugin.LiveMusicArchive.Tests
{
    public sealed class TrackTaggerTests : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "lma-tag-" + Guid.NewGuid().ToString("N") + ".mp3");
        private readonly TrackTagger _tagger = new(TestData.Logger);

        public TrackTaggerTests()
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "archive-derivative-head.mp3"), _path);
        }

        public void Dispose() => File.Delete(_path);

        [Fact]
        public void writes_the_release_id_album_and_artist_lidarr_matches_on()
        {
            _tagger.Tag(_path, new DownloadManifest
            {
                ArtistName = "Grateful Dead",
                AlbumTitle = "1977-05-08: Barton Hall - Cornell University, Ithaca, NY",
                AlbumId = "lma-6faa7ca7-0d99-4a5e-bfa6-1fd5037520c6-19770508",
                ReleaseId = "lma-gd77-05-08.matrix.dan.26857.sbeok.flacf"
            });

            using var file = TagLib.File.Create(_path);
            Assert.Equal("lma-gd77-05-08.matrix.dan.26857.sbeok.flacf", file.Tag.MusicBrainzReleaseId);
            Assert.Equal("lma-6faa7ca7-0d99-4a5e-bfa6-1fd5037520c6-19770508", file.Tag.MusicBrainzReleaseGroupId);
            Assert.Equal("1977-05-08: Barton Hall - Cornell University, Ithaca, NY", file.Tag.Album);
            Assert.Equal(new[] { "Grateful Dead" }, file.Tag.AlbumArtists);
        }

        [Fact]
        public void keeps_the_song_title_and_performer_from_archive_org()
        {
            _tagger.Tag(_path, new DownloadManifest { ArtistName = "Someone Else", AlbumTitle = "x" });

            using var file = TagLib.File.Create(_path);
            Assert.Equal("Take A Step Back", file.Tag.Title);
            Assert.Equal(new[] { "Grateful Dead" }, file.Tag.Performers);
        }

        [Fact]
        public void leaves_release_id_alone_for_musicbrainz_shows()
        {
            _tagger.Tag(_path, new DownloadManifest { AlbumTitle = "1969-11-07: Fillmore West" });

            using var file = TagLib.File.Create(_path);
            Assert.True(string.IsNullOrEmpty(file.Tag.MusicBrainzReleaseId));
        }

        [Fact]
        public void unreadable_files_are_skipped_not_fatal()
        {
            File.WriteAllText(_path, "not audio");

            var ex = Record.Exception(() => _tagger.Tag(_path, new DownloadManifest { AlbumTitle = "x" }));

            Assert.Null(ex);
        }
    }
}
