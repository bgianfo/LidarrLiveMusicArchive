using System.Text.Json.Nodes;
using Xunit;

namespace Lidarr.Plugin.LiveMusicArchive.EndToEnd
{
    /// <summary>
    /// The plugin inside a real Lidarr: shows appear, settings screens load, and every kind of recording that once
    /// failed to import (FLAC without titles in disc sub-folders, .m4a only) is searched, downloaded and imported
    /// in full.
    /// </summary>
    [Collection(EndToEndCollection.Name)]
    public sealed class ImportTests
    {
        private static readonly TimeSpan ImportTimeout = TimeSpan.FromMinutes(5);
        private readonly EndToEndEnvironment _env;

        public ImportTests(EndToEndEnvironment env)
        {
            _env = env;
        }

        [EndToEndFact]
        public async Task every_settings_screen_has_fields()
        {
            // Lidarr 3.2's settings dialogs crash on a provider without a fields list.
            foreach (var (schema, implementation) in new[]
            {
                ("indexer/schema", "ArchiveOrgIndexer"),
                ("indexer/schema", "ArchiveOrgTorrentIndexer"),
                ("downloadclient/schema", "ArchiveOrgDownloadClient"),
                ("metadata/schema", "LiveMusicArchiveMetadata")
            })
            {
                var provider = (await _env.Lidarr.Get(schema)).AsArray().FirstOrDefault(p => p["implementation"]?.GetValue<string>() == implementation);
                Assert.True(provider != null, $"{implementation} is missing from {schema}");
                Assert.True(provider["fields"] is JsonArray, $"{implementation} has no fields list");
            }
        }

        [EndToEndFact]
        public async Task shows_with_importable_audio_appear_with_their_releases()
        {
            var flac = await _env.Album("2001-05-01");
            var m4a = await _env.Album("2001-05-02");

            Assert.NotNull(flac);
            Assert.NotNull(m4a);
            Assert.Equal("2001-05-01: The Test Hall, Springfield, XX", flac["title"].GetValue<string>());
            Assert.Single(flac["releases"].AsArray());
            Assert.Single(m4a["releases"].AsArray());
            // Shows are built from artist requests; asking the metadata server for an lma- album only wastes a 404.
            Assert.DoesNotContain(_env.Fakes.Requests, r => r.Contains("/album/lma-", StringComparison.Ordinal));
        }

        [EndToEndFact]
        public async Task a_show_with_nothing_importable_is_left_out() =>
            Assert.Null(await _env.Album("2001-05-03"));

        [EndToEndFact]
        public Task flac_originals_in_disc_folders_import_every_track() =>
            GrabAndImport("2001-05-01", "FLAC", expectedTracks: 3);

        [EndToEndFact]
        public Task an_m4a_only_upload_imports_every_track() =>
            GrabAndImport("2001-05-02", "AAC", expectedTracks: 2);

        [EndToEndFact]
        public async Task an_archive_org_outage_deletes_nothing()
        {
            var before = (await _env.Albums()).Count;
            _env.Fakes.ArchiveOrgDown = true;
            try
            {
                await _env.Lidarr.Send(HttpMethod.Post, "command", new JsonObject { ["name"] = "RefreshArtist", ["artistIds"] = new JsonArray(_env.ArtistId) });
                await Task.Delay(TimeSpan.FromSeconds(3));
                await _env.Lidarr.WaitForIdleAsync(TimeSpan.FromMinutes(3));

                var after = await _env.Albums();
                Assert.Equal(before, after.Count);
                Assert.All(after, a => Assert.NotEmpty(a["releases"].AsArray()));
            }
            finally
            {
                _env.Fakes.ArchiveOrgDown = false;
            }
        }

        private async Task GrabAndImport(string date, string qualityStartsWith, int expectedTracks)
        {
            var album = await _env.Album(date);
            Assert.NotNull(album);
            var albumId = album["id"].GetValue<int>();

            var results = (await _env.Lidarr.Get($"release?albumId={albumId}")).AsArray()
                .Where(r => r["protocol"].GetValue<string>() == "ArchiveOrgDownloadProtocol")
                .ToList();
            var pick = results.FirstOrDefault(r => r["quality"]["quality"]["name"].GetValue<string>().StartsWith(qualityStartsWith, StringComparison.Ordinal));
            Assert.True(pick != null, $"no {qualityStartsWith} result among: {string.Join(", ", results.Select(r => r["title"]))}");
            Assert.True(pick["approved"].GetValue<bool>(), $"{pick["title"]} rejected: {pick["rejections"]}");

            await _env.Lidarr.Send(HttpMethod.Post, "release", new JsonObject { ["guid"] = pick["guid"].DeepClone(), ["indexerId"] = pick["indexerId"].DeepClone() });

            string failure = null;
            await _env.Lidarr.WaitUntilAsync(async () =>
            {
                var stats = (await _env.Lidarr.Get($"album/{albumId}"))["statistics"];
                if (stats?["trackFileCount"]?.GetValue<int>() == expectedTracks)
                {
                    return true;
                }

                var queue = (await _env.Lidarr.Get("queue?pageSize=100"))["records"].AsArray()
                    .FirstOrDefault(q => q["albumId"]?.GetValue<int>() == albumId && q["trackedDownloadState"]?.GetValue<string>() == "importFailed");
                failure = queue?["statusMessages"]?.ToJsonString();
                return failure != null;
            }, ImportTimeout, $"{date} to import");

            Assert.True(failure == null, $"import of {date} failed: {failure}");
            var tracks = (await _env.Lidarr.Get($"album/{albumId}"))["statistics"];
            Assert.Equal(expectedTracks, tracks["trackCount"].GetValue<int>());
            Assert.Equal(expectedTracks, tracks["trackFileCount"].GetValue<int>());
        }
    }
}
