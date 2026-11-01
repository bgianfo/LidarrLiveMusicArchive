using Lidarr.Plugin.LiveMusicArchive.Core;
using Xunit;

namespace Lidarr.Plugin.LiveMusicArchive.Tests
{
    public class AudioFilesTests
    {
        [Fact]
        public void private_files_are_not_downloadable()
        {
            var formats = AudioFiles.DownloadableFormats(TestData.Item(TestData.StreamOnlyFlacItem));

            Assert.DoesNotContain("Flac", formats);
            Assert.Equal("VBR MP3", formats[0]);
        }

        [Fact]
        public void formats_are_returned_best_first()
        {
            var item = TestData.SyntheticItem("x",
                new { name = "a.mp3", format = "VBR MP3" },
                new { name = "a.ogg", format = "Ogg Vorbis" },
                new { name = "a.flac", format = "Flac" },
                new { name = "a.png", format = "PNG" });

            Assert.Equal(new[] { "Flac", "VBR MP3", "Ogg Vorbis" }, AudioFiles.DownloadableFormats(item));
        }

        [Fact]
        public void files_are_ordered_by_disc_then_track()
        {
            var item = TestData.SyntheticItem("x",
                new { name = "gd77d2t01.flac", format = "Flac" },
                new { name = "gd77d1t10.flac", format = "Flac" },
                new { name = "gd77d1t02.flac", format = "Flac" });

            var files = AudioFiles.ForFormat(item, "Flac");

            Assert.Equal(new[] { "gd77d1t02.flac", "gd77d1t10.flac", "gd77d2t01.flac" }, files.Select(f => f.Name));
            Assert.Equal(new[] { 1, 1, 2 }, files.Select(f => f.Medium));
            Assert.Equal(new[] { 2, 10, 1 }, files.Select(f => f.Track));
        }

        [Fact]
        public void falls_back_to_the_track_field_when_names_carry_no_position()
        {
            var item = TestData.SyntheticItem("x",
                new { name = "Encore.flac", format = "Flac", track = "12/12" },
                new { name = "Opener.flac", format = "Flac", track = "01" });

            var files = AudioFiles.ForFormat(item, "Flac");

            Assert.Equal(new[] { "Opener.flac", "Encore.flac" }, files.Select(f => f.Name));
            Assert.All(files, f => Assert.Equal(1, f.Medium));
        }

        [Fact]
        public void reads_size_checksum_title_and_length()
        {
            var item = TestData.SyntheticItem("x",
                new { name = "s1t01.flac", format = "Flac", size = "1234", md5 = "abc", title = "Tuning", length = "40" });

            var file = Assert.Single(AudioFiles.ForFormat(item, "Flac"));

            Assert.Equal(1234, file.Size);
            Assert.Equal("abc", file.Md5);
            Assert.Equal("Tuning", file.Title);
            Assert.Equal(40000, file.DurationMs);
        }

        [Fact]
        public void shorten_originals_are_never_chosen_because_lidarr_cannot_import_them()
        {
            var formats = AudioFiles.DownloadableFormats(TestData.Item(TestData.ShortenItem));

            Assert.DoesNotContain("Shorten", formats);
            Assert.Equal("VBR MP3", formats[0]);
        }

        [Fact]
        public void lengths_come_from_the_mp3_derivative_not_the_wrong_shorten_value()
        {
            var ogg = AudioFiles.ForFormat(TestData.Item(TestData.ShortenItem), "Ogg Vorbis")[0];

            // The .shn claims 44.58s; the MP3 made from it says 01:51.
            Assert.Equal(111000, ogg.DurationMs);
        }

        [Fact]
        public void titles_missing_on_derivatives_come_from_the_original()
        {
            var ogg = AudioFiles.ForFormat(TestData.Item(TestData.ShortenItem), "Ogg Vorbis");

            // Disc 1's Ogg files have no titles of their own; their Shorten originals do. (Some disc 2
            // songs have no title on any copy; the mapper falls back to the file name for those.)
            Assert.Equal("Intro -> Chanting", ogg[0].Title);
            Assert.All(ogg.Where(f => f.Medium == 1), f => Assert.NotNull(f.Title));
        }

        [Fact]
        public void files_in_sub_folders_keep_their_path_and_disc_number()
        {
            var mp3 = AudioFiles.ForFormat(TestData.Item(TestData.ShortenItem), "VBR MP3");

            Assert.Equal("dispatch1996-04-16d1.shnf/dispatch1996-04-16d1t01.mp3", mp3[0].Name);
            Assert.Equal(new[] { 1, 2 }, mp3.Select(f => f.Medium).Distinct());
        }

        [Fact]
        public void m4a_only_items_are_downloadable_with_track_numbers_from_n_of_m()
        {
            var item = TestData.Item(TestData.M4aOnlyItem);

            Assert.Equal(new[] { AudioFiles.MPEG4Audio }, AudioFiles.DownloadableFormats(item));
            var files = AudioFiles.ForFormat(item, AudioFiles.MPEG4Audio);
            Assert.Equal(11, files.Count);
            Assert.Equal(Enumerable.Range(1, 11), files.Select(f => f.Track));
            Assert.Equal("Deadweight On Velveteen", files[0].Title);
            Assert.Equal(193650, files[0].DurationMs);
        }

        [Theory]
        [InlineData("06:21", 381000)]
        [InlineData("365.55", 365550)]
        [InlineData("1:02:03", 3723000)]
        [InlineData("", 0)]
        [InlineData(null, 0)]
        [InlineData("n/a", 0)]
        public void parses_archive_lengths(string length, int expectedMs) =>
            Assert.Equal(expectedMs, AudioFiles.ParseLengthMs(length));
    }
}
