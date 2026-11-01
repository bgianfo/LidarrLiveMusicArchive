using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Lidarr.Plugin.LiveMusicArchive.Core
{
    /// <summary>One downloadable audio file of an archive.org item.</summary>
    public class AudioFile
    {
        /// <summary>Gets or sets the file name, relative to the item (may contain '/').</summary>
        public string Name { get; set; }

        /// <summary>
        /// Gets or sets the name of the original file this song was made from (its own name for originals).
        /// The same in every format, so ids built from it survive a change of format.
        /// </summary>
        public string SongKey { get; set; }

        /// <summary>Gets or sets the archive.org format label, e.g. "Flac" or "VBR MP3".</summary>
        public string Format { get; set; }

        /// <summary>Gets or sets the song title from the item's file metadata, if present.</summary>
        public string Title { get; set; }

        /// <summary>Gets or sets the duration in milliseconds, or 0 when unknown.</summary>
        public int DurationMs { get; set; }

        /// <summary>Gets or sets the size in bytes, or 0 when unknown.</summary>
        public long Size { get; set; }

        /// <summary>Gets or sets the MD5 checksum published by archive.org, if any.</summary>
        public string Md5 { get; set; }

        /// <summary>Gets or sets the disc or set number (1-based).</summary>
        public int Medium { get; set; }

        /// <summary>Gets or sets the track number within the medium, or 0 when unknown.</summary>
        public int Track { get; set; }
    }

    /// <summary>Selects the audio files of an archive.org item that can actually be downloaded.</summary>
    public static class AudioFiles
    {
        /// <summary>
        /// Audio formats Lidarr can import, best first: lossless before lossy. Shorten (.shn) is lossless
        /// and common on etree, but Lidarr can't import it, so it's absent until the plugin can convert it.
        /// </summary>
        public static readonly IReadOnlyList<string> FormatPreference = new[] { "Flac", "24bit Flac", "VBR MP3", MPEG4Audio, "Ogg Vorbis", "64Kbps MP3" };

        /// <summary>
        /// archive.org's label for .m4a files, AAC or ALAC. Some uploads have only these, with no FLAC or MP3
        /// derivatives (e.g. josegonzalez2006-09-08). Ranked after MP3 because most are lossy AAC; see
        /// <see cref="ReleaseOffers.QualityTag(string, IReadOnlyCollection{AudioFile})"/> for telling them apart.
        /// </summary>
        public const string MPEG4Audio = "MPEG-4 Audio";

        // Whose length to trust, best first. archive.org's lengths for Shorten originals are wrong
        // (d1t01.shn says 44.58s, its MP3 says 01:51), while its MP3 derivatives are decoded and accurate.
        private static readonly string[] LengthPreference = { "VBR MP3", "Flac", "24bit Flac", MPEG4Audio, "Ogg Vorbis", "64Kbps MP3" };

        private static readonly Regex SetTrackPattern = new(@"(?:d|s|cd|disc|set)(\d+)[._-]?t(?:rack)?(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Formats with at least one downloadable file, in <see cref="FormatPreference"/> order.
        /// Restricted files (e.g. lossless originals in stream-only collections) are flagged
        /// <c>"private": "true"</c>, return 401, and are ignored.
        /// </summary>
        /// <param name="item">The item's <c>/metadata/{identifier}</c> response.</param>
        /// <returns>The available format labels.</returns>
        public static List<string> DownloadableFormats(JObject item)
        {
            var formats = Downloadable(item).Select(f => IaJson.First(f["format"])).ToHashSet();
            return FormatPreference.Where(formats.Contains).ToList();
        }

        /// <summary>
        /// The downloadable files of one format, in playback order (disc, then track, then name). Each file's
        /// title and length are merged from every copy of the same song (the original and its derivatives),
        /// because archive.org fills them in unevenly: Ogg files lack titles, Shorten lengths are wrong.
        /// </summary>
        /// <param name="item">The item's <c>/metadata/{identifier}</c> response.</param>
        /// <param name="format">An archive.org format label, e.g. "Flac".</param>
        /// <returns>The files, possibly empty.</returns>
        public static List<AudioFile> ForFormat(JObject item, string format)
        {
            var copies = AllFiles(item).GroupBy(SongKey).ToDictionary(g => g.Key, g => g.ToList());

            return Downloadable(item)
                .Where(f => IaJson.First(f["format"]) == format)
                .Select(f => ToAudioFile(f, copies[SongKey(f)]))
                .OrderBy(f => f.Medium).ThenBy(f => f.Track).ThenBy(f => f.Name, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>Parses an archive.org length, either seconds ("365.55") or clock time ("06:21", "1:02:03").</summary>
        /// <param name="length">The raw length value.</param>
        /// <returns>The duration in milliseconds, or 0 if it can't be parsed.</returns>
        public static int ParseLengthMs(string length)
        {
            if (string.IsNullOrWhiteSpace(length))
            {
                return 0;
            }

            double seconds = 0;
            foreach (var part in length.Split(':'))
            {
                if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    return 0;
                }

                seconds = (seconds * 60) + value;
            }

            return (int)Math.Round(seconds * 1000);
        }

        private static IEnumerable<JObject> AllFiles(JObject item) =>
            (item["files"] as JArray ?? new JArray()).OfType<JObject>().Where(f => IaJson.First(f["name"]) != null);

        private static IEnumerable<JObject> Downloadable(JObject item) =>
            AllFiles(item).Where(f => IaJson.First(f["private"]) != "true");

        // Derivatives name the file they were made from; originals are their own key.
        private static string SongKey(JObject file) =>
            IaJson.First(file["source"]) == "derivative" && IaJson.First(file["original"]) is { } original
                ? original
                : IaJson.First(file["name"]);

        private static AudioFile ToAudioFile(JObject file, List<JObject> copies)
        {
            var name = IaJson.First(file["name"]);
            var (medium, track) = MediumAndTrack(name, IaJson.First(file["track"]) ?? copies.Select(c => IaJson.First(c["track"])).FirstOrDefault(t => t != null));
            var length = LengthPreference
                .SelectMany(fmt => copies.Where(c => IaJson.First(c["format"]) == fmt))
                .Select(c => ParseLengthMs(IaJson.First(c["length"])))
                .FirstOrDefault(ms => ms > 0);

            return new AudioFile
            {
                Name = name,
                SongKey = SongKey(file),
                Format = IaJson.First(file["format"]),
                Title = IaJson.First(file["title"]) ?? copies.Select(c => IaJson.First(c["title"])).FirstOrDefault(t => t != null),
                DurationMs = length,
                Size = long.TryParse(IaJson.First(file["size"]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : 0,
                Md5 = IaJson.First(file["md5"]),
                Medium = medium,
                Track = track
            };
        }

        // Taper file names usually encode position as d1t01 / s2t03 / cd1track04; fall back to the track field.
        private static (int Medium, int Track) MediumAndTrack(string name, string trackField)
        {
            var match = SetTrackPattern.Match(Path.GetFileName(name));
            if (match.Success)
            {
                return (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
            }

            // "3", "3/11" and "3 of 11" all occur.
            var track = Regex.Match(trackField ?? "", @"^\s*(\d+)").Groups[1].Value;
            return (1, int.TryParse(track, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0);
        }
    }
}
