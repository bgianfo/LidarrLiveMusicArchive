using Lidarr.Plugin.LiveMusicArchive.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Lidarr.Plugin.LiveMusicArchive.Tests
{
    public class ShowCatalogTests
    {
        [Fact]
        public void groups_shows_by_exact_date_within_the_year_filter()
        {
            var recordings = new[] { "1977-05-08", "1977-05-08", "1966-00-00", "1978-01-01", "", "1977-02-26" }
                .Select((d, i) => TestData.Recording($"r{i}", d));
            YearRange.TryParse("1977", out var years);

            var shows = ShowCatalog.GroupByShow(recordings, years);

            Assert.Equal(new[] { "1977-02-26", "1977-05-08" }, shows.Keys.OrderBy(k => k));
            Assert.Equal(2, shows["1977-05-08"].Count);
        }

        [Theory]
        [InlineData("", true, null, null)]
        [InlineData("1977", true, 1977, 1977)]
        [InlineData(" 1972 - 1974 ", true, 1972, 1974)]
        [InlineData("1974-1972", false, null, null)]
        [InlineData("seventies", false, null, null)]
        public void parses_year_ranges(string value, bool valid, int? from, int? to)
        {
            Assert.Equal(valid, YearRange.TryParse(value, out var range));
            Assert.Equal(from, range.From);
            Assert.Equal(to, range.To);
        }

        [Fact]
        public void unbounded_range_contains_every_year() =>
            Assert.True(new YearRange().Contains(1066));

        [Fact]
        public void show_title_uses_the_venue_most_tapers_agree_on()
        {
            var recordings = new[]
            {
                TestData.Recording("a", "1977-05-08", venue: "Barton Hall"),
                TestData.Recording("b", "1977-05-08", venue: "Barton Hall "),
                TestData.Recording("c", "1977-05-08", venue: "Cornell University", coverage: null)
            };

            Assert.Equal("1977-05-08: Barton Hall, Ithaca, NY", ShowCatalog.ShowTitle("1977-05-08", recordings));
        }

        [Fact]
        public void show_title_without_venue_is_just_the_date() =>
            Assert.Equal("1977-05-08", ShowCatalog.ShowTitle("1977-05-08", new[] { TestData.Recording("a", "1977-05-08", venue: null, coverage: " ") }));

        [Fact]
        public void scrape_dates_survive_json_parsing()
        {
            var page = IaJson.Parse(@"{""items"":[{""identifier"":""gd77-05-08"",""date"":""1977-05-08T00:00:00Z"",""venue"":[""Barton Hall""],""downloads"":42}]}");

            var recording = RecordingSummary.FromScrape((JObject)page["items"][0]);

            Assert.Equal("1977-05-08", recording.Date);
            Assert.Equal("Barton Hall", recording.Venue);
            Assert.Equal(42, recording.Downloads);
        }

        [Fact]
        public void json_helpers_handle_strings_arrays_and_nulls()
        {
            var json = IaJson.Parse(@"{""s"":""one"",""a"":[""x"",""y""],""e"":[],""n"":null}");

            Assert.Equal("one", IaJson.First(json["s"]));
            Assert.Equal("x", IaJson.First(json["a"]));
            Assert.Null(IaJson.First(json["e"]));
            Assert.Null(IaJson.First(json["n"]));
            Assert.Null(IaJson.First(json["missing"]));
            Assert.Equal(new[] { "x", "y" }, IaJson.All(json["a"]));
            Assert.Empty(IaJson.All(json["n"]));
        }
    }
}
