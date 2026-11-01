using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Lidarr.Plugin.LiveMusicArchive.Core
{
    /// <summary>
    /// Ids for entities that exist only on archive.org. All of them start with "lma-", so neither Lidarr nor
    /// any tool reading tags can mistake one for a MusicBrainz id. Album ids describe themselves
    /// ("lma-{artistMbid}-{yyyyMMdd}"), so a show can be rebuilt from its id alone.
    /// </summary>
    public static class SyntheticIds
    {
        /// <summary>Prefix of every album and release id this plugin creates.</summary>
        public const string Prefix = "lma-";

        private static readonly Regex AlbumIdPattern = new(@"^lma-([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})-(\d{8})$", RegexOptions.Compiled);
        private static readonly Guid Namespace = new("5d1c3a5e-2b7a-4e1f-9c0a-1a2c0e7ee000");

        /// <summary>The album id for an artist's show on a date.</summary>
        /// <param name="artistMbid">The artist's MusicBrainz id.</param>
        /// <param name="showDate">The show date (yyyy-MM-dd).</param>
        /// <returns>For example "lma-6faa7ca7-0d99-4a5e-bfa6-1fd5037520c6-19770508".</returns>
        public static string AlbumId(string artistMbid, string showDate) =>
            $"{Prefix}{artistMbid}-{showDate.Replace("-", "")}";

        /// <summary>Reads the artist and date back out of an album id.</summary>
        /// <param name="id">A foreign album id.</param>
        /// <param name="artistMbid">The artist's MusicBrainz id.</param>
        /// <param name="showDate">The show date (yyyy-MM-dd).</param>
        /// <returns>False for MusicBrainz ids and malformed or impossible dates.</returns>
        public static bool TryParseAlbumId(string id, out string artistMbid, out string showDate)
        {
            artistMbid = showDate = null;
            var match = AlbumIdPattern.Match(id ?? "");
            if (!match.Success ||
                !DateTime.TryParseExact(match.Groups[2].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return false;
            }

            artistMbid = match.Groups[1].Value;
            showDate = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }

        /// <summary>The release id for an archive.org recording.</summary>
        /// <param name="identifier">The archive.org identifier.</param>
        /// <returns>"lma-" followed by the identifier.</returns>
        public static string ReleaseId(string identifier) => $"{Prefix}{identifier}";

        /// <summary>Reads the archive.org identifier back out of a release id.</summary>
        /// <param name="id">A foreign release id.</param>
        /// <param name="identifier">The archive.org identifier.</param>
        /// <returns>False for ids this plugin didn't create.</returns>
        public static bool TryParseReleaseId(string id, out string identifier)
        {
            identifier = id != null && id.StartsWith(Prefix, StringComparison.Ordinal) && id.Length > Prefix.Length && !AlbumIdPattern.IsMatch(id)
                ? id[Prefix.Length..]
                : null;
            return identifier != null;
        }

        /// <summary>Whether an id was created by this plugin.</summary>
        /// <param name="id">Any foreign id or tag value.</param>
        /// <returns>True for "lma-…" values.</returns>
        public static bool IsSynthetic(string id) => id != null && id.StartsWith(Prefix, StringComparison.Ordinal);

        /// <summary>A stable track id for one song of a recording.</summary>
        /// <param name="identifier">The archive.org identifier.</param>
        /// <param name="songKey">The song's original file name (<see cref="AudioFile.SongKey"/>).</param>
        /// <returns>"lma-" followed by a name-based UUID.</returns>
        public static string TrackId(string identifier, string songKey) => Prefix + Uuid5("track", identifier, songKey);

        /// <summary>A stable recording id for one song of a recording.</summary>
        /// <param name="identifier">The archive.org identifier.</param>
        /// <param name="songKey">The song's original file name.</param>
        /// <returns>"lma-" followed by a name-based UUID.</returns>
        public static string RecordingId(string identifier, string songKey) => Prefix + Uuid5("recording", identifier, songKey);

        /// <summary>
        /// Track ids earlier versions issued for a song, listed as old ids so Lidarr migrates existing tracks:
        /// a bare UUID from the original file (0.3.1) and a bare UUID from the format's file name (0.3.0 and earlier).
        /// </summary>
        /// <param name="identifier">The archive.org identifier.</param>
        /// <param name="songKey">The song's original file name.</param>
        /// <param name="fileName">The file name in the format the track list was built from.</param>
        /// <returns>The distinct earlier ids.</returns>
        public static List<string> LegacyTrackIds(string identifier, string songKey, string fileName) =>
            new[] { Uuid5("track", identifier, songKey), Uuid5("track", identifier, fileName) }.Distinct().ToList();

        /// <summary>Recording ids earlier versions issued for a song; see <see cref="LegacyTrackIds"/>.</summary>
        /// <param name="identifier">The archive.org identifier.</param>
        /// <param name="songKey">The song's original file name.</param>
        /// <param name="fileName">The file name in the format the track list was built from.</param>
        /// <returns>The distinct earlier ids.</returns>
        public static List<string> LegacyRecordingIds(string identifier, string songKey, string fileName) =>
            new[] { Uuid5("recording", identifier, songKey), Uuid5("recording", identifier, fileName) }.Distinct().ToList();

        // RFC 4122 name-based (SHA-1) UUID: stable across refreshes and restarts.
        private static string Uuid5(params string[] parts)
        {
            var ns = Namespace.ToByteArray();
            SwapByteOrder(ns);
            var name = Encoding.UTF8.GetBytes(string.Join("/", parts));
            var hash = SHA1.HashData(ns.Concat(name).ToArray());

            var bytes = hash[..16];
            bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
            bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
            SwapByteOrder(bytes);
            return new Guid(bytes).ToString();
        }

        // Guid's byte layout is little-endian for the first three fields; RFC 4122 is big-endian.
        private static void SwapByteOrder(byte[] guid)
        {
            (guid[0], guid[3]) = (guid[3], guid[0]);
            (guid[1], guid[2]) = (guid[2], guid[1]);
            (guid[4], guid[5]) = (guid[5], guid[4]);
            (guid[6], guid[7]) = (guid[7], guid[6]);
        }
    }
}
