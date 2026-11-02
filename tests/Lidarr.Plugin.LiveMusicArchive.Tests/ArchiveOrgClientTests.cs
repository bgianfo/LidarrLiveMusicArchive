using Lidarr.Plugin.LiveMusicArchive.Core;
using Lidarr.Plugin.LiveMusicArchive.Core.Http;
using NSubstitute;
using NzbDrone.Common.EnvironmentInfo;
using Xunit;

namespace Lidarr.Plugin.LiveMusicArchive.Tests
{
    public sealed class ArchiveOrgClientTests : IDisposable
    {
        private readonly string _appData = Path.Combine(Path.GetTempPath(), "lma-client-" + Guid.NewGuid().ToString("N"));
        private readonly IArchiveOrgHttp _http = Substitute.For<IArchiveOrgHttp>();
        private readonly FakeClock _clock = new();
        private readonly List<string> _requests = new();
        private readonly ArchiveOrgClient _client;

        public ArchiveOrgClientTests()
        {
            var folders = Substitute.For<IAppFolderInfo>();
            folders.AppDataFolder.Returns(_appData);
            _client = new ArchiveOrgClient(_http, folders, _clock, TestData.Logger);
        }

        public void Dispose()
        {
            if (Directory.Exists(_appData))
            {
                Directory.Delete(_appData, true);
            }
        }

        [Fact]
        public void unchanged_items_are_served_from_cache()
        {
            Serve(_ => Item("gd77"));
            var updated = _clock.Fake.GetUtcNow().AddDays(-400);

            _client.GetItemMetadata("gd77", updated);
            _clock.Fake.Advance(TimeSpan.FromDays(100));
            _client.GetItemMetadata("gd77", updated);

            Assert.Single(_requests);
        }

        [Fact]
        public void items_changed_since_they_were_cached_are_refetched()
        {
            Serve(_ => Item("gd77"));

            _client.GetItemMetadata("gd77", _clock.Fake.GetUtcNow().AddDays(-400));
            _clock.Fake.Advance(TimeSpan.FromDays(3));
            _client.GetItemMetadata("gd77", _clock.Fake.GetUtcNow().AddDays(-1));

            Assert.Equal(2, _requests.Count);
        }

        [Fact]
        public void items_are_refetched_after_the_safety_ttl_even_if_unchanged()
        {
            Serve(_ => Item("gd77"));
            var updated = _clock.Fake.GetUtcNow().AddDays(-400);

            _client.GetItemMetadata("gd77", updated);
            _clock.Fake.Advance(ArchiveOrgClient.ItemTtl * 1.2);
            _client.GetItemMetadata("gd77", updated);

            Assert.Equal(2, _requests.Count);
        }

        [Fact]
        public void an_outage_serves_the_cached_copy_however_old()
        {
            Serve(_ => Item("gd77"));
            _client.GetItemMetadata("gd77");
            _clock.Fake.Advance(ArchiveOrgClient.ItemTtl * 2);
            _http.GetStringAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<Task<string>>(_ => throw new HttpRequestException("archive.org down"));

            var item = _client.GetItemMetadata("gd77");

            Assert.Equal("gd77", item["metadata"].Value<string>("identifier"));
        }

        [Fact]
        public void an_outage_with_nothing_cached_fails()
        {
            _http.GetStringAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<Task<string>>(_ => throw new HttpRequestException("archive.org down"));

            Assert.Throws<HttpRequestException>(() => _client.GetItemMetadata("gd77"));
        }

        [Fact]
        public void a_listing_for_another_collection_is_rejected_and_the_good_copy_kept()
        {
            Serve(_ => @"{""items"":[{""identifier"":""gd77"",""collection"":[""GratefulDead"",""etree""]}]}");
            _client.GetRecordings("GratefulDead");
            _clock.Fake.Advance(TimeSpan.FromDays(2));
            // Seen 2026-11-01: the scrape API answered every query with the same unrelated results.
            Serve(_ => @"{""items"":[{""identifier"":""05TheCurtainWith"",""collection"":[""ChumATributetoPhish"",""etree""]}]}");

            var recordings = _client.GetRecordings("GratefulDead");

            Assert.Equal("gd77", Assert.Single(recordings).Identifier);
        }

        [Fact]
        public void a_repeating_unrelated_page_with_a_cursor_does_not_paginate_forever()
        {
            Serve(_ => @"{""items"":[{""identifier"":""x"",""collection"":[""SomeoneElse"",""etree""]}],""cursor"":""again""}");

            var recordings = _client.GetRecordings("GratefulDead");

            Assert.Empty(recordings);
            Assert.Single(_requests);
        }

        [Fact]
        public void an_empty_listing_never_replaces_a_good_one()
        {
            Serve(_ => @"{""items"":[{""identifier"":""gd77"",""collection"":""GratefulDead""}]}");
            _client.GetRecordings("GratefulDead");
            _clock.Fake.Advance(TimeSpan.FromDays(2));
            Serve(_ => @"{""items"":[],""count"":0,""total"":0}");

            Assert.Single(_client.GetRecordings("GratefulDead"));
            _clock.Fake.Advance(TimeSpan.FromDays(2));
            Assert.Single(_client.GetRecordings("GratefulDead"));
        }

        [Fact]
        public void an_artist_with_no_collection_simply_has_none_and_that_is_cached()
        {
            Serve(_ => @"{""items"":[],""total"":0}");

            Assert.Null(_client.FindCollectionByCreator("Taylor Swift"));
            Assert.Null(_client.FindCollectionByCreator("Taylor Swift"));
            Assert.Empty(_client.GetRecordings("NoSuchCollection"));
            Assert.Empty(_client.GetRecordings("NoSuchCollection"));

            Assert.Equal(2, _requests.Count);
        }

        [Fact]
        public void a_creator_search_finding_only_other_artists_is_cached_not_retried()
        {
            Serve(_ => @"{""items"":[{""collection"":[""ChumATributetoPhish"",""etree""],""creator"":""Chum, A Tribute to Phish""}]}");

            Assert.Null(_client.FindCollectionByCreator("Phish"));
            Assert.Null(_client.FindCollectionByCreator("Phish"));

            Assert.Single(_requests);
        }

        [Theory]
        [InlineData(@"{""items"":[{""collection"":[""ChumATributetoPhish"",""etree""],""creator"":""Chum, A Tribute to Phish""}]}")]
        [InlineData(@"{""items"":[{""collection"":[""Dispatch"",""etree""],""creator"":""Dispatch""}]}")]
        [InlineData(@"{""items"":[]}")]
        public void a_good_creator_match_survives_a_later_answer_without_the_artist(string later)
        {
            Serve(_ => @"{""items"":[{""collection"":[""DiscoBiscuits"",""etree""],""creator"":""Disco Biscuits""}]}");
            Assert.Equal("DiscoBiscuits", _client.FindCollectionByCreator("The Disco Biscuits"));
            _clock.Fake.Advance(TimeSpan.FromDays(2));
            Serve(_ => later);

            Assert.Equal("DiscoBiscuits", _client.FindCollectionByCreator("The Disco Biscuits"));
            // And the good answer is still what's cached.
            _clock.Fake.Advance(TimeSpan.FromHours(1));
            Assert.Equal("DiscoBiscuits", _client.FindCollectionByCreator("The Disco Biscuits"));
        }

        [Fact]
        public void creator_search_ignores_tribute_acts_and_other_artists()
        {
            Serve(_ => @"{""items"":[{""collection"":[""ChumATributetoPhish"",""etree""],""creator"":""Chum, A Tribute to Phish""},{""collection"":[""Dispatch"",""etree""],""creator"":""Dispatch""}]}");

            Assert.Null(_client.FindCollectionByCreator("Phish"));
            Assert.Null(_client.FindCollectionByCreator("Taylor Swift"));
        }

        [Theory]
        [InlineData("The String Cheese Incident", "String Cheese Incident", true)]
        [InlineData("moe.", "Moe", true)]
        [InlineData("Umphrey’s McGee", "Umphrey's McGee", true)]
        [InlineData("Béla Fleck", "Bela Fleck", true)]
        [InlineData("Umphreys McGee", "Umphrey’s McGee", true)]
        [InlineData("Phil Lesh and Friends", "Phil Lesh & Friends", true)]
        [InlineData("Phil Lesh", "Phil Lesh & Friends", false)]
        [InlineData("Chum, A Tribute to Phish", "Phish", false)]
        [InlineData("Theory of a Deadman", "ory of a Deadman", false)]
        public void matches_artist_names_loosely_but_not_partially(string creator, string artist, bool same) =>
            Assert.Equal(same, ArchiveOrgClient.SameArtist(creator, artist));

        [Fact]
        public void metadata_for_another_item_is_rejected()
        {
            Serve(_ => Item("someone-else"));

            Assert.Throws<HttpRequestException>(() => _client.GetItemMetadata("gd77"));
        }

        [Fact]
        public void metadata_for_another_item_falls_back_to_the_cached_one()
        {
            Serve(_ => Item("gd77"));
            _client.GetItemMetadata("gd77");
            _clock.Fake.Advance(ArchiveOrgClient.ItemTtl * 2);
            Serve(_ => "{}");

            Assert.Equal("gd77", _client.GetItemMetadata("gd77")["metadata"].Value<string>("identifier"));
        }

        [Fact]
        public void dates_in_cached_json_survive()
        {
            Serve(_ => @"{""metadata"":{""identifier"":""gd77"",""date"":""1977-05-08T00:00:00Z""},""files"":[]}");

            _client.GetItemMetadata("gd77");
            var cached = _client.GetItemMetadata("gd77");

            Assert.Equal("1977-05-08T00:00:00Z", cached["metadata"].Value<string>("date"));
        }

        [Fact]
        public void follows_scrape_cursors_and_reads_change_dates()
        {
            Serve(url => url.Contains("cursor=")
                ? @"{""items"":[{""identifier"":""b"",""date"":""1977-05-08T00:00:00Z"",""collection"":[""GratefulDead"",""etree""],""oai_updatedate"":[""2005-09-16T22:36:25Z"",""2024-04-22T05:42:25Z"",""2011-10-04T00:38:04Z""]}]}"
                : @"{""items"":[{""identifier"":""a"",""date"":""1977-05-07T00:00:00Z"",""collection"":""GratefulDead""}],""cursor"":""next page""}");

            var recordings = _client.GetRecordings("GratefulDead");

            Assert.Equal(new[] { "a", "b" }, recordings.Select(r => r.Identifier));
            Assert.Null(recordings[0].UpdatedAt);
            Assert.Equal(new DateTimeOffset(2024, 4, 22, 5, 42, 25, TimeSpan.Zero), recordings[1].UpdatedAt);
            Assert.Contains("cursor=next%20page", _requests[1]);
            Assert.Contains("oai_updatedate", _requests[0]);
        }

        [Fact]
        public void collection_listings_are_reused_for_about_a_day()
        {
            Serve(_ => @"{""items"":[{""identifier"":""a"",""collection"":""GratefulDead""}]}");

            _client.GetRecordings("GratefulDead");
            _clock.Fake.Advance(TimeSpan.FromHours(20));
            _client.GetRecordings("GratefulDead");
            Assert.Single(_requests);

            _clock.Fake.Advance(TimeSpan.FromHours(8));
            _client.GetRecordings("GratefulDead");
            Assert.Equal(2, _requests.Count);
        }

        [Fact]
        public void cache_lifetimes_are_jittered_per_url_but_stable()
        {
            var lifetimes = Enumerable.Range(0, 50).Select(i => ArchiveOrgClient.Jittered($"https://archive.org/metadata/item{i}", TimeSpan.FromDays(100), 0.15)).ToList();

            Assert.All(lifetimes, t => Assert.InRange(t.TotalDays, 85, 115));
            Assert.True(lifetimes.Distinct().Count() > 40);
            Assert.Equal(lifetimes[0], ArchiveOrgClient.Jittered("https://archive.org/metadata/item0", TimeSpan.FromDays(100), 0.15));
        }

        [Fact]
        public void creator_search_quotes_the_name_and_strips_quotes_from_it()
        {
            Serve(_ => @"{""items"":[{""collection"":[""Dispatch"",""etree""],""creator"":""Dispatch""}]}");

            Assert.Equal("Dispatch", _client.FindCollectionByCreator("Dis\"patch"));
            Assert.Contains("creator%3A%22Dispatch%22", Assert.Single(_requests));
        }

        [Theory]
        [InlineData("The Disco Biscuits", "Disco Biscuits")]
        [InlineData("Umphrey’s McGee", "Umphrey's McGee")]
        [InlineData("Theory of a Deadman", "Theory of a Deadman")]
        [InlineData("The \"Band\"", "Band")]
        [InlineData("moe.", "moe.")]
        public void creator_phrase_matches_how_archive_org_writes_names(string artist, string phrase) =>
            Assert.Equal(phrase, ArchiveOrgClient.CreatorPhrase(artist));

        [Theory]
        [InlineData("Dispatch", "\"Dispatch\"")]
        [InlineData("Umphrey’s McGee", "(\"Umphrey's McGee\" OR \"Umphreys McGee\")")]
        [InlineData("Phil Lesh & Friends", "(\"Phil Lesh & Friends\" OR \"Phil Lesh and Friends\")")]
        [InlineData("Hot Tuna and Friends", "(\"Hot Tuna and Friends\" OR \"Hot Tuna & Friends\")")]
        public void creator_query_also_tries_the_spellings_tapers_use(string artist, string query) =>
            Assert.Equal(query, ArchiveOrgClient.CreatorQuery(artist));

        [Fact]
        public void umphreys_mcgee_is_found_without_the_apostrophe()
        {
            Serve(_ => @"{""items"":[{""collection"":[""UmphreysMcGee"",""etree""],""creator"":""Umphreys McGee""},{""collection"":[""UmphreysMcGee"",""etree""],""creator"":""Music By Umphrey's McGee, Capture by marc geanoules""}]}");

            Assert.Equal("UmphreysMcGee", _client.FindCollectionByCreator("Umphrey’s McGee"));
            Assert.Contains(Uri.EscapeDataString("\"Umphreys McGee\""), Assert.Single(_requests));
        }

        [Fact]
        public void the_disco_biscuits_are_found_under_disco_biscuits()
        {
            Serve(_ => @"{""items"":[{""collection"":[""DiscoBiscuits"",""etree""],""creator"":""Disco Biscuits""}]}");

            Assert.Equal("DiscoBiscuits", _client.FindCollectionByCreator("The Disco Biscuits"));
            Assert.Contains("creator%3A%22Disco%20Biscuits%22", Assert.Single(_requests));
        }

        [Fact]
        public void each_query_gets_its_own_stable_field_order()
        {
            var fields = new[] { "identifier", "date", "venue", "coverage", "downloads", "oai_updatedate", "collection" };
            var orders = Enumerable.Range(0, 50).Select(i => ArchiveOrgClient.FieldsFor($"collection:Artist{i} AND mediatype:etree", fields)).ToList();

            Assert.Equal(orders[0], ArchiveOrgClient.FieldsFor("collection:Artist0 AND mediatype:etree", fields));
            Assert.True(orders.Distinct().Count() > 45);
            Assert.All(orders, o => Assert.Equal(fields.OrderBy(f => f), o.Split(',').OrderBy(f => f)));
        }

        [Fact]
        public async Task downloads_escape_each_path_segment()
        {
            await _client.DownloadFileAsync("dispatch1996-04-16.shnf", "d1.shnf/t 01.mp3", "/tmp/x", CancellationToken.None);

            await _http.Received().DownloadFileAsync("https://archive.org/download/dispatch1996-04-16.shnf/d1.shnf/t%2001.mp3", "/tmp/x", Arg.Any<CancellationToken>());
        }

        private static string Item(string identifier) => $@"{{""metadata"":{{""identifier"":""{identifier}""}},""files"":[]}}";

        private void Serve(Func<string, string> body) =>
            _http.GetStringAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci =>
            {
                var url = ci.Arg<string>();
                _requests.Add(url);
                return Task.FromResult(body(url));
            });
    }
}
