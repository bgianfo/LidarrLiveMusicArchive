using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Lidarr.Plugin.LiveMusicArchive.Core
{
    /// <summary>
    /// Picks cover art for a show from artwork tapers uploaded with their recordings. Roughly one recording in
    /// eight has any (a 1977 Grateful Dead sample: 5 of 40). The rest carry only spectrograms and
    /// archive.org's generic thumbnail, which are never used.
    /// </summary>
    public static class Artwork
    {
        private static readonly string[] ImageFormats = { "JPEG", "PNG", "GIF" };
        private static readonly Regex Front = new(@"front|cover|folder|poster", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex NotACover = new(@"back|inside|inlay|tray|spectro|thumb|^__ia|\.(png)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>The best cover image across a show's recordings, most downloaded recording first.</summary>
        /// <param name="items">The recordings' <c>/metadata/{identifier}</c> responses, in preference order.</param>
        /// <returns>A download URL, or null when no recording has artwork.</returns>
        public static string Cover(IEnumerable<JObject> items)
        {
            var candidates = items.SelectMany(item =>
                {
                    var identifier = IaJson.First(item["metadata"]?["identifier"]);
                    return (item["files"] as JArray ?? new JArray()).OfType<JObject>()
                        .Where(f => IaJson.First(f["source"]) == "original" && ImageFormats.Contains(IaJson.First(f["format"])) && IaJson.First(f["private"]) != "true")
                        .Select(f => (Identifier: identifier, Name: IaJson.First(f["name"])));
                })
                .Where(c => c.Identifier != null && c.Name != null)
                .ToList();

            var best = candidates.FirstOrDefault(c => Front.IsMatch(Path.GetFileName(c.Name)) && !Regex.IsMatch(c.Name, "back|thumb", RegexOptions.IgnoreCase));
            if (best.Name == null)
            {
                best = candidates.FirstOrDefault(c => !NotACover.IsMatch(Path.GetFileName(c.Name)));
            }

            return best.Name == null
                ? null
                : $"https://archive.org/download/{Uri.EscapeDataString(best.Identifier)}/{string.Join("/", best.Name.Split('/').Select(Uri.EscapeDataString))}";
        }
    }
}
