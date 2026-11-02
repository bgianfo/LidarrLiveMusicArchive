using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Lidarr.Plugin.LiveMusicArchive.EndToEnd
{
    /// <summary>One file of a fake archive.org item.</summary>
    public sealed record FakeFile(string Name, string Format, string Source, byte[] Content, string Title = null, string Track = null,
        string Length = "1", string Original = null, bool Private = false);

    /// <summary>One fake archive.org recording.</summary>
    public sealed record FakeItem(string Identifier, string Collection, string Creator, string Date, string Venue, string Coverage, IReadOnlyList<FakeFile> Files);

    /// <summary>One fake MusicBrainz artist, as Lidarr's metadata server would describe it.</summary>
    public sealed record FakeArtist(string Mbid, string Name, string Collection);

    /// <summary>
    /// The recordings the fakes serve. Each item reproduces a layout that broke imports on a real instance: FLAC
    /// originals without titles in archive.org's disc sub-folders, an upload that has only .m4a files, and a show
    /// with nothing Lidarr can import.
    /// </summary>
    public sealed class FakeCatalog
    {
        public static readonly FakeArtist Band = new("0e2e0e2e-0000-4000-8000-000000000001", "Endtoend Band", "EndtoendBand");

        public FakeCatalog(string audioDirectory)
        {
            var flac = File.ReadAllBytes(Path.Combine(audioDirectory, "tone.flac"));
            var m4a = File.ReadAllBytes(Path.Combine(audioDirectory, "tone.m4a"));
            var mp3 = File.ReadAllBytes(Path.Combine(audioDirectory, "archive-derivative-head.mp3"));

            Artists = new[] { Band };
            Items = new[]
            {
                // FLAC originals with no titles, in disc sub-folders; MP3 derivatives carry the titles.
                new FakeItem("eeb2001-05-01.flac16", Band.Collection, Band.Name, "2001-05-01", "The Test Hall", "Springfield, XX", new[]
                {
                    new FakeFile("eeb2001-05-01d1.flac16/eeb2001-05-01d1t01.flac", "Flac", "original", flac, Track: "1"),
                    new FakeFile("eeb2001-05-01d1.flac16/eeb2001-05-01d1t02.flac", "Flac", "original", flac, Track: "2"),
                    new FakeFile("eeb2001-05-01d2.flac16/eeb2001-05-01d2t01.flac", "Flac", "original", flac, Track: "1"),
                    new FakeFile("eeb2001-05-01d1.flac16/eeb2001-05-01d1t01.mp3", "VBR MP3", "derivative", mp3, "First Song", "1", "00:01", "eeb2001-05-01d1.flac16/eeb2001-05-01d1t01.flac"),
                    new FakeFile("eeb2001-05-01d1.flac16/eeb2001-05-01d1t02.mp3", "VBR MP3", "derivative", mp3, "Second Song", "2", "00:01", "eeb2001-05-01d1.flac16/eeb2001-05-01d1t02.flac"),
                    new FakeFile("eeb2001-05-01d2.flac16/eeb2001-05-01d2t01.mp3", "VBR MP3", "derivative", mp3, "Encore", "1", "00:01", "eeb2001-05-01d2.flac16/eeb2001-05-01d2t01.flac"),
                }),

                // Only .m4a files (AAC), "n of m" track numbers, no derivatives at all.
                new FakeItem("eeb2001-05-02", Band.Collection, Band.Name, "2001-05-02", "Second Venue", "Shelbyville, XX", new[]
                {
                    new FakeFile("01 Opener.m4a", "MPEG-4 Audio", "original", m4a, "Opener", "1 of 2"),
                    new FakeFile("02 Closer.m4a", "MPEG-4 Audio", "original", m4a, "Closer", "2 of 2"),
                }),

                // Only Shorten, which Lidarr can't import: the show must not appear at all.
                new FakeItem("eeb2001-05-03.shnf", Band.Collection, Band.Name, "2001-05-03", "Third Venue", "Ogdenville, XX", new[]
                {
                    new FakeFile("eeb2001-05-03d1t01.shn", "Shorten", "original", new byte[] { 1, 2, 3 }, "Unplayable", "1"),
                }),
            };
        }

        public IReadOnlyList<FakeArtist> Artists { get; }

        public IReadOnlyList<FakeItem> Items { get; }

        public static string AlbumId(FakeArtist artist, string date) => $"lma-{artist.Mbid}-{date.Replace("-", "")}";

        public static string Md5(byte[] content) => Convert.ToHexString(MD5.HashData(content)).ToLowerInvariant();

        public static string ETag(byte[] content) => $"\"{Md5(content)[..16]}\"";

        public JsonObject ArtistJson(FakeArtist artist) => new()
        {
            ["id"] = artist.Mbid,
            ["artistname"] = artist.Name,
            ["sortname"] = artist.Name,
            ["artistaliases"] = new JsonArray(),
            ["disambiguation"] = "",
            ["overview"] = "A band that exists only in the end-to-end tests.",
            ["type"] = "Group",
            ["status"] = "active",
            ["genres"] = new JsonArray("Rock"),
            ["images"] = new JsonArray(),
            ["links"] = new JsonArray(new JsonObject { ["target"] = $"https://archive.org/details/{artist.Collection}", ["type"] = "download for free" }),
            ["rating"] = new JsonObject { ["Count"] = 0, ["Value"] = null },
            ["oldids"] = new JsonArray(),
            ["Albums"] = new JsonArray()
        };

        public JsonObject MetadataJson(FakeItem item) => new()
        {
            ["metadata"] = new JsonObject
            {
                ["identifier"] = item.Identifier,
                ["title"] = $"{item.Creator} Live at {item.Venue} on {item.Date}",
                ["creator"] = item.Creator,
                ["date"] = item.Date,
                ["venue"] = item.Venue,
                ["coverage"] = item.Coverage,
                ["source"] = "SBD > DAT",
                ["collection"] = new JsonArray(item.Collection, "etree"),
                ["publicdate"] = "2001-06-01 00:00:00"
            },
            ["files"] = new JsonArray(item.Files.Select(f =>
            {
                var file = new JsonObject
                {
                    ["name"] = f.Name,
                    ["format"] = f.Format,
                    ["source"] = f.Source,
                    ["size"] = f.Content.Length.ToString(),
                    ["md5"] = Md5(f.Content),
                    ["length"] = f.Length
                };
                if (f.Title != null) { file["title"] = f.Title; }
                if (f.Track != null) { file["track"] = f.Track; }
                if (f.Original != null) { file["original"] = f.Original; }
                if (f.Private) { file["private"] = "true"; }
                return (JsonNode)file;
            }).ToArray())
        };

        public JsonObject ScrapeRow(FakeItem item, IEnumerable<string> fields)
        {
            var all = new Dictionary<string, JsonNode>
            {
                ["identifier"] = item.Identifier,
                ["date"] = item.Date + "T00:00:00Z",
                ["venue"] = item.Venue,
                ["coverage"] = item.Coverage,
                ["downloads"] = 100,
                ["oai_updatedate"] = new JsonArray("2001-06-01T00:00:00Z"),
                ["collection"] = new JsonArray(item.Collection, "etree"),
                ["creator"] = item.Creator,
                ["title"] = item.Identifier
            };
            var row = new JsonObject();
            foreach (var f in fields.Where(all.ContainsKey))
            {
                row[f] = all[f].DeepClone();
            }

            return row;
        }
    }
}
