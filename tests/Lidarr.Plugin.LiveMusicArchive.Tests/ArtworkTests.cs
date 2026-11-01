using Lidarr.Plugin.LiveMusicArchive.Core;
using Xunit;

namespace Lidarr.Plugin.LiveMusicArchive.Tests
{
    public class ArtworkTests
    {
        [Fact]
        public void prefers_a_front_cover_scan()
        {
            var url = Artwork.Cover(new[] { TestData.Item(TestData.StreamOnlyFlacItem) });

            Assert.Equal($"https://archive.org/download/{TestData.StreamOnlyFlacItem}/GD770508_Front150.jpg", url);
        }

        [Fact]
        public void spectrograms_and_thumbnails_are_never_covers() =>
            Assert.Null(Artwork.Cover(new[] { TestData.Item(TestData.SoundboardItem), TestData.Item(TestData.ShortenItem) }));

        [Fact]
        public void falls_back_to_other_artwork_but_not_backs()
        {
            var item = TestData.SyntheticItem("x",
                new { name = "gd_back.jpg", format = "JPEG", source = "original" },
                new { name = "ticket.jpg", format = "JPEG", source = "original" });

            Assert.Equal("https://archive.org/download/x/ticket.jpg", Artwork.Cover(new[] { item }));
        }

        [Fact]
        public void earlier_recordings_win()
        {
            var first = TestData.SyntheticItem("first", new { name = "front.jpg", format = "JPEG", source = "original" });
            var second = TestData.SyntheticItem("second", new { name = "cover.jpg", format = "JPEG", source = "original" });

            Assert.StartsWith("https://archive.org/download/first/", Artwork.Cover(new[] { first, second }));
        }

        [Fact]
        public void escapes_names() =>
            Assert.Equal("https://archive.org/download/x/art/front%20cover.jpg",
                Artwork.Cover(new[] { TestData.SyntheticItem("x", new { name = "art/front cover.jpg", format = "JPEG", source = "original" }) }));
    }
}
