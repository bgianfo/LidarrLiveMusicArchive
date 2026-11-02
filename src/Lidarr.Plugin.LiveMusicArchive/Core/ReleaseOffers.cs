using System.Globalization;
using Lidarr.Plugin.LiveMusicArchive.Core.Http;
using Newtonsoft.Json.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Parser.Model;

namespace Lidarr.Plugin.LiveMusicArchive.Core
{
    /// <summary>
    /// Turns archive.org recordings into indexer results. Each downloadable format of a recording is a
    /// separate result, so Lidarr's quality profile (not this plugin) decides between FLAC and MP3.
    /// </summary>
    public static class ReleaseOffers
    {
        /// <summary>The query parameter that carries the chosen format in a download URL.</summary>
        public const string FormatParameter = "lma-format";

        // Formats worth offering; tiny 64k MP3s and Ogg derivatives only clutter results.
        private static readonly string[] OfferedFormats = { "Flac", "24bit Flac", "VBR MP3", AudioFiles.MPEG4Audio };

        // ALAC is lossless (typically 700–1,100 kbps for CD audio); AAC rarely exceeds 320.
        private const int AlacBitrateThreshold = 600;

        /// <summary>
        /// The indexer results for one recording.
        /// Titles have the shape "Artist - Album Title [SBD] [FLAC] [identifier]". Lidarr's parser
        /// takes the quality from the bracketed tag and, during a search, finds the artist and album
        /// names in the title verbatim.
        /// </summary>
        /// <param name="item">The item's <c>/metadata/{identifier}</c> response.</param>
        /// <param name="artistName">The artist name as Lidarr knows it.</param>
        /// <param name="albumTitle">The album title as Lidarr knows it.</param>
        /// <returns>One result per offered format; empty when nothing is downloadable.</returns>
        public static List<ReleaseInfo> FromItem(JObject item, string artistName, string albumTitle)
        {
            var metadata = item["metadata"] as JObject ?? new JObject();
            var identifier = IaJson.First(metadata["identifier"]);
            if (identifier == null)
            {
                return new List<ReleaseInfo>();
            }

            var lineage = ShowMapper.Lineage(identifier, string.Join(" ", IaJson.All(metadata["source"])));
            var published = ParsePublishDate(IaJson.First(metadata["publicdate"]) ?? IaJson.First(metadata["addeddate"]));

            return AudioFiles.DownloadableFormats(item)
                .Where(OfferedFormats.Contains)
                .Select(format => (Format: format, Files: AudioFiles.ForFormat(item, format)))
                .Select(x => new ReleaseInfo
                {
                    Guid = $"archiveorg-{identifier}-{x.Format.Replace(' ', '-').ToLowerInvariant()}",
                    Title = Title(artistName, albumTitle, lineage, QualityTag(x.Format, x.Files), identifier),
                    Artist = artistName,
                    Album = albumTitle,
                    Size = x.Files.Sum(f => f.Size),
                    DownloadUrl = DownloadUrl(identifier, x.Format),
                    InfoUrl = $"{ArchiveOrgEndpoints.BaseUrl}/details/{Uri.EscapeDataString(identifier)}",
                    PublishDate = published,
                    Codec = QualityTag(x.Format, x.Files).Split(' ')[0],
                    Container = x.Format == AudioFiles.MPEG4Audio ? "M4A" : x.Format.Contains("Flac", StringComparison.Ordinal) ? "FLAC" : "MP3"
                })
                .ToList();
        }

        /// <summary>
        /// The torrent result for one recording: archive.org's own "_archive.torrent", which a torrent client
        /// fetches from archive.org's web seeds (and any peers). It carries the whole item: every format, so a
        /// FLAC recording also brings its MP3 and Ogg copies. None is offered for restricted items, whose
        /// torrents return 403.
        /// </summary>
        /// <param name="item">The item's <c>/metadata/{identifier}</c> response.</param>
        /// <param name="artistName">The artist name as Lidarr knows it.</param>
        /// <param name="albumTitle">The album title as Lidarr knows it.</param>
        /// <returns>Zero or one result.</returns>
        public static List<ReleaseInfo> TorrentFromItem(JObject item, string artistName, string albumTitle)
        {
            var metadata = item["metadata"] as JObject ?? new JObject();
            var identifier = IaJson.First(metadata["identifier"]);
            var files = (item["files"] as JArray ?? new JArray()).OfType<JObject>().ToList();
            var torrent = files.FirstOrDefault(f => IaJson.First(f["format"]) == "Archive BitTorrent");
            var restricted = IaJson.All(metadata["collection"]).Contains("stream_only") || files.Any(f => IaJson.First(f["private"]) == "true");
            var best = AudioFiles.DownloadableFormats(item).FirstOrDefault(OfferedFormats.Contains);

            if (identifier == null || torrent == null || restricted || best == null)
            {
                return new List<ReleaseInfo>();
            }

            var lineage = ShowMapper.Lineage(identifier, string.Join(" ", IaJson.All(metadata["source"])));
            var tags = new[] { lineage, QualityTag(best, AudioFiles.ForFormat(item, best)), identifier, "torrent" }.Where(t => t != null).Select(t => $"[{t}]");

            return new List<ReleaseInfo>
            {
                new TorrentInfo
                {
                    Guid = $"archiveorg-{identifier}-torrent",
                    Title = $"{TitleName(artistName)} - {TitleName(albumTitle)} {string.Join(" ", tags)}",
                    Artist = artistName,
                    Album = albumTitle,
                    Size = files.Sum(f => long.TryParse(IaJson.First(f["size"]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0),
                    DownloadUrl = $"{ArchiveOrgEndpoints.BaseUrl}/download/{Uri.EscapeDataString(identifier)}/{Uri.EscapeDataString(IaJson.First(torrent["name"]))}",
                    InfoUrl = $"{ArchiveOrgEndpoints.BaseUrl}/details/{Uri.EscapeDataString(identifier)}",
                    PublishDate = ParsePublishDate(IaJson.First(metadata["publicdate"]) ?? IaJson.First(metadata["addeddate"])),
                    // archive.org always web-seeds its torrents, so there is at least one source.
                    Seeders = 1,
                    Peers = 1
                }
            };
        }

        /// <summary>The tag Lidarr's quality parser understands for an archive.org format.</summary>
        /// <param name="format">An archive.org format label.</param>
        /// <param name="files">The files in that format; needed only for MPEG-4 Audio, which is AAC or ALAC.</param>
        /// <returns>For example "FLAC", "FLAC 24bit", "MP3 VBR V0", "AAC" or "ALAC".</returns>
        public static string QualityTag(string format, IReadOnlyCollection<AudioFile> files) =>
            format == AudioFiles.MPEG4Audio
                ? (AverageKbps(files) >= AlacBitrateThreshold ? "ALAC" : "AAC")
                : QualityTag(format);

        /// <summary>The tag Lidarr's quality parser understands for an archive.org format.</summary>
        /// <param name="format">An archive.org format label.</param>
        /// <returns>For example "FLAC", "FLAC 24bit" or "MP3 VBR V0".</returns>
        public static string QualityTag(string format) => format switch
        {
            "Flac" => "FLAC",
            "24bit Flac" => "FLAC 24bit",
            // Lidarr needs "MP3 ... VBR" to see VBR at all. The level matches what Lidarr itself decides when it
            // reads imported archive.org MP3s (MP3-VBR-V0), so search results and files on disk compare equal
            // and Lidarr doesn't refuse grabs or chase "upgrades" between them.
            "VBR MP3" => "MP3 VBR V0",
            // Lidarr reads the exact AAC or ALAC quality from the files on import; plain AAC parses as AAC-VBR.
            AudioFiles.MPEG4Audio => "AAC",
            _ => format
        };

        /// <summary>Average bitrate of a set of files from their sizes and lengths.</summary>
        /// <param name="files">The files.</param>
        /// <returns>Kilobits per second, or 0 when lengths are unknown.</returns>
        internal static int AverageKbps(IReadOnlyCollection<AudioFile> files)
        {
            var milliseconds = files?.Sum(f => (long)f.DurationMs) ?? 0;
            return milliseconds > 0 ? (int)(files.Sum(f => f.Size) * 8 / milliseconds) : 0;
        }

        /// <summary>Builds the URL handed from the indexer to the download client.</summary>
        /// <param name="identifier">The archive.org identifier.</param>
        /// <param name="format">The archive.org format to download.</param>
        /// <returns>An archive.org details URL carrying the format.</returns>
        public static string DownloadUrl(string identifier, string format) =>
            $"{ArchiveOrgEndpoints.BaseUrl}/details/{Uri.EscapeDataString(identifier)}?{FormatParameter}={Uri.EscapeDataString(format)}";

        /// <summary>Reads the identifier and format back out of a <see cref="DownloadUrl"/>.</summary>
        /// <param name="url">The download URL.</param>
        /// <param name="identifier">The archive.org identifier.</param>
        /// <param name="format">The archive.org format.</param>
        /// <returns>False when the URL was not produced by this plugin.</returns>
        public static bool TryParseDownloadUrl(string url, out string identifier, out string format)
        {
            identifier = format = null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Host != "archive.org" && uri.Host != new Uri(ArchiveOrgEndpoints.BaseUrl).Host) ||
                !uri.AbsolutePath.StartsWith("/details/", StringComparison.Ordinal))
            {
                return false;
            }

            identifier = Uri.UnescapeDataString(uri.AbsolutePath["/details/".Length..]);
            format = uri.Query.TrimStart('?').Split('&')
                .Select(p => p.Split('=', 2))
                .Where(p => p.Length == 2 && p[0] == FormatParameter)
                .Select(p => Uri.UnescapeDataString(p[1]))
                .FirstOrDefault();

            return identifier.Length > 0 && !identifier.Contains('/') && format != null;
        }

        private static string Title(string artistName, string albumTitle, string lineage, string qualityTag, string identifier)
        {
            var tags = new[] { lineage, qualityTag, identifier }.Where(t => t != null).Select(t => $"[{t}]");
            return $"{TitleName(artistName)} - {TitleName(albumTitle)} {string.Join(" ", tags)}";
        }

        // During a search Lidarr looks for the artist and album names in the title with accents removed
        // (Parser.ParseAlbumTitleWithSearchCriteria applies RemoveAccent to the names, not to the title), so
        // "José González" in a title never matches. Using Lidarr's own RemoveAccent keeps both sides identical.
        private static string TitleName(string name) => name.RemoveAccent();

        private static DateTime ParsePublishDate(string value) =>
            DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
                ? date
                : DateTime.UtcNow;
    }
}
