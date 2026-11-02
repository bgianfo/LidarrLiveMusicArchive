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
            }, null);

            using var file = TagLib.File.Create(_path);
            Assert.Equal("lma-gd77-05-08.matrix.dan.26857.sbeok.flacf", file.Tag.MusicBrainzReleaseId);
            Assert.Equal("lma-6faa7ca7-0d99-4a5e-bfa6-1fd5037520c6-19770508", file.Tag.MusicBrainzReleaseGroupId);
            Assert.Equal("1977-05-08: Barton Hall - Cornell University, Ithaca, NY", file.Tag.Album);
            Assert.Equal(new[] { "Grateful Dead" }, file.Tag.AlbumArtists);
        }

        [Fact]
        public void keeps_the_song_title_and_performer_from_archive_org()
        {
            _tagger.Tag(_path, new DownloadManifest { ArtistName = "Someone Else", AlbumTitle = "x" }, null);

            using var file = TagLib.File.Create(_path);
            Assert.Equal("Take A Step Back", file.Tag.Title);
            Assert.Equal(new[] { "Grateful Dead" }, file.Tag.Performers);
        }

        [Fact]
        public void leaves_release_id_alone_for_musicbrainz_shows()
        {
            _tagger.Tag(_path, new DownloadManifest { AlbumTitle = "1969-11-07: Fillmore West" }, null);

            using var file = TagLib.File.Create(_path);
            Assert.True(string.IsNullOrEmpty(file.Tag.MusicBrainzReleaseId));
        }

        [Fact]
        public void unreadable_files_are_skipped_not_fatal()
        {
            File.WriteAllText(_path, "not audio");

            var ex = Record.Exception(() => _tagger.Tag(_path, new DownloadManifest { AlbumTitle = "x" }, null));

            Assert.Null(ex);
        }

        [Theory]
        [InlineData("archive-derivative-head.mp3")]
        [InlineData("tone.flac")]
        [InlineData("tone.m4a")]
        public void lidarr_reads_back_the_track_identity_in_every_format(string fixture)
        {
            var path = Path.Combine(Path.GetTempPath(), "lma-tag-" + Guid.NewGuid().ToString("N") + Path.GetExtension(fixture));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture), path);
            try
            {
                _tagger.Tag(path, new DownloadManifest { ArtistName = "José González", AlbumTitle = "2006-09-08: Fine Line", ReleaseId = "lma-josegonzalez2006-09-08" },
                    new ManifestFile { Title = "Deadweight On Velveteen", TrackNumber = 3, Disc = 2, RecordingId = "lma-11111111-2222-5333-8444-555555555555" });

                // Read the file the way Lidarr's importer does.
                var info = new NzbDrone.Core.MediaFiles.AudioTag(path);
                Assert.Equal("lma-11111111-2222-5333-8444-555555555555", info.MusicBrainzTrackId);
                Assert.Equal("lma-josegonzalez2006-09-08", info.MusicBrainzReleaseId);
                Assert.Equal("Deadweight On Velveteen", info.Title);
                Assert.Equal(3u, info.Track);
                Assert.Equal(2u, info.Disc);
                Assert.Equal("2006-09-08: Fine Line", info.Album);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void a_stale_recording_id_from_an_earlier_tagging_is_replaced()
        {
            using (var before = TagLib.File.Create(_path))
            {
                before.Tag.MusicBrainzTrackId = "740e2cc8-6e29-56ca-96d4-957c95da7ef2";
                before.Save();
            }

            _tagger.Tag(_path, new DownloadManifest { ReleaseId = "lma-x" }, new ManifestFile { RecordingId = "lma-740e2cc8-6e29-56ca-96d4-957c95da7ef2" });

            Assert.Equal("lma-740e2cc8-6e29-56ca-96d4-957c95da7ef2", new NzbDrone.Core.MediaFiles.AudioTag(_path).MusicBrainzTrackId);
        }
    }
}
