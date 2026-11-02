using System.Text.Json.Nodes;
using Xunit;

namespace Lidarr.Plugin.LiveMusicArchive.EndToEnd
{
    /// <summary>
    /// Starts the fakes and Lidarr once for all scenarios, configures Lidarr the way the README tells users to,
    /// and adds the fake band. Does nothing when the end-to-end tests are switched off.
    /// </summary>
    public sealed class EndToEndEnvironment : IAsyncLifetime
    {
        public FakeServices Fakes { get; private set; }

        public LidarrHost Lidarr { get; private set; }

        public int ArtistId { get; private set; }

        public async Task InitializeAsync()
        {
            if (Environment.GetEnvironmentVariable("LMA_E2E") != "1")
            {
                return;
            }

            Fakes = await FakeServices.StartAsync(new FakeCatalog(Path.Combine(AppContext.BaseDirectory, "Audio")));
            Lidarr = await LidarrHost.StartAsync(Fakes.BaseUrl);

            // Lidarr's metadata comes from the fake metadata server.
            var provider = await Lidarr.Get("config/metadataProvider");
            provider["metadataSource"] = Fakes.BaseUrl + "/api/v0.4";
            await Lidarr.Send(HttpMethod.Put, "config/metadataProvider", provider);

            // README step 2: a metadata profile that allows the plugin's shows.
            var profile = await Lidarr.Get("metadataprofile/schema");
            profile["name"] = "Live Music Archive";
            Allow(profile["primaryAlbumTypes"], "albumType", "Broadcast");
            Allow(profile["secondaryAlbumTypes"], "albumType", "Live");
            Allow(profile["releaseStatuses"], "releaseStatus", "Bootleg");
            var profileId = (await Lidarr.Send(HttpMethod.Post, "metadataprofile", profile))["id"].GetValue<int>();

            await Lidarr.Send(HttpMethod.Post, "rootfolder", new JsonObject
            {
                ["name"] = "music",
                ["path"] = Lidarr.MusicFolder,
                ["defaultMetadataProfileId"] = profileId,
                ["defaultQualityProfileId"] = 1
            });

            // README step 1: enable the show listings.
            var metadata = (await Lidarr.Get("metadata")).AsArray().First(m => m["implementation"]?.GetValue<string>() == "LiveMusicArchiveMetadata");
            metadata["enable"] = true;
            await Lidarr.Send(HttpMethod.Put, $"metadata/{metadata["id"]}", metadata);

            // README step 3: the indexer and the download client. Saving runs their connection tests.
            var indexer = Schema(await Lidarr.Get("indexer/schema"), "ArchiveOrgIndexer");
            indexer["name"] = "Live Music Archive";
            indexer["enableAutomaticSearch"] = true;
            indexer["enableInteractiveSearch"] = true;
            await Lidarr.Send(HttpMethod.Post, "indexer", indexer);

            var client = Schema(await Lidarr.Get("downloadclient/schema"), "ArchiveOrgDownloadClient");
            client["name"] = "Live Music Archive";
            client["enable"] = true;
            SetField(client, "downloadPath", Lidarr.DownloadFolder);
            await Lidarr.Send(HttpMethod.Post, "downloadclient", client);

            var band = FakeCatalog.Band;
            ArtistId = (await Lidarr.Send(HttpMethod.Post, "artist", new JsonObject
            {
                ["foreignArtistId"] = band.Mbid,
                ["artistName"] = band.Name,
                ["qualityProfileId"] = 1,
                ["metadataProfileId"] = profileId,
                ["rootFolderPath"] = Lidarr.MusicFolder,
                ["monitored"] = false,
                ["addOptions"] = new JsonObject { ["monitor"] = "none", ["searchForMissingAlbums"] = false }
            }))["id"].GetValue<int>();

            await Lidarr.WaitUntilAsync(async () => (await Albums()).Count > 0, TimeSpan.FromMinutes(3), "the band's shows to appear");
            await Lidarr.WaitForIdleAsync(TimeSpan.FromMinutes(3));
        }

        public async Task DisposeAsync()
        {
            if (Lidarr != null)
            {
                await Lidarr.DisposeAsync();
            }

            if (Fakes != null)
            {
                await Fakes.DisposeAsync();
            }
        }

        public async Task<List<JsonNode>> Albums() =>
            (await Lidarr.Get($"album?artistId={ArtistId}")).AsArray().Where(a => a["foreignAlbumId"].GetValue<string>().StartsWith("lma-", StringComparison.Ordinal)).ToList();

        public async Task<JsonNode> Album(string date) =>
            (await Albums()).FirstOrDefault(a => a["foreignAlbumId"].GetValue<string>() == FakeCatalog.AlbumId(FakeCatalog.Band, date));

        private static void Allow(JsonNode items, string key, string name)
        {
            foreach (var item in items.AsArray())
            {
                item["allowed"] = item[key]["name"].GetValue<string>() == name;
            }
        }

        private static JsonNode Schema(JsonNode schemas, string implementation) =>
            schemas.AsArray().First(s => s["implementation"]?.GetValue<string>() == implementation);

        private static void SetField(JsonNode provider, string name, string value) =>
            provider["fields"].AsArray().First(f => f["name"].GetValue<string>() == name)["value"] = value;
    }

    [CollectionDefinition(Name)]
    public sealed class EndToEndCollection : ICollectionFixture<EndToEndEnvironment>
    {
        public const string Name = "Lidarr end to end";
    }
}
