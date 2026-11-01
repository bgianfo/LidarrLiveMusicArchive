using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Lidarr.Plugin.LiveMusicArchive.Core
{
    /// <summary>One archive.org item (a single taper's recording) as returned by the scrape API.</summary>
    public class RecordingSummary
    {
        /// <summary>Gets or sets the archive.org identifier.</summary>
        public string Identifier { get; set; }

        /// <summary>Gets or sets the show date as archive.org has it, usually yyyy-MM-dd but sometimes a placeholder.</summary>
        public string Date { get; set; }

        /// <summary>Gets or sets the venue name.</summary>
        public string Venue { get; set; }

        /// <summary>Gets or sets the location, e.g. "Ithaca, NY".</summary>
        public string Coverage { get; set; }

        /// <summary>Gets or sets the all-time download count, used to rank recordings.</summary>
        public long Downloads { get; set; }

        /// <summary>Gets or sets when archive.org last changed the item (latest <c>oai_updatedate</c>), or null if unknown.</summary>
        public DateTimeOffset? UpdatedAt { get; set; }

        /// <summary>Reads a scrape API item.</summary>
        /// <param name="item">One element of the scrape response's "items".</param>
        /// <returns>The summary.</returns>
        public static RecordingSummary FromScrape(JObject item) => new()
        {
            Identifier = IaJson.First(item["identifier"]),
            Date = (IaJson.First(item["date"]) ?? "").Split('T')[0],
            Venue = IaJson.First(item["venue"]),
            Coverage = IaJson.First(item["coverage"]),
            Downloads = long.TryParse(IaJson.First(item["downloads"]), out var d) ? d : 0,
            // oai_updatedate lists every change, not in order; the newest is what matters.
            UpdatedAt = IaJson.All(item["oai_updatedate"])
                .Select(v => DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : (DateTimeOffset?)null)
                .Where(t => t != null)
                .DefaultIfEmpty(null)
                .Max()
        };
    }

    /// <summary>Helpers for archive.org's loosely typed JSON.</summary>
    public static class IaJson
    {
        /// <summary>
        /// Settings for reading Lidarr's metadata JSON into its resource types. Some MusicBrainz data carries
        /// <c>"rating": {"Value": null}</c>, which Lidarr tolerates but a non-nullable decimal does not; nulls are
        /// skipped so the property keeps its default.
        /// </summary>
        public static readonly JsonSerializerSettings LenientSettings = new() { NullValueHandling = NullValueHandling.Ignore };

        /// <summary>Converts Lidarr metadata JSON to a resource type, ignoring nulls (see <see cref="LenientSettings"/>).</summary>
        /// <typeparam name="T">The resource type.</typeparam>
        /// <param name="json">The JSON object.</param>
        /// <returns>The resource.</returns>
        public static T ToResource<T>(JToken json) => json.ToObject<T>(JsonSerializer.Create(LenientSettings));

        /// <summary>
        /// Parses JSON with dates kept as strings. Newtonsoft would otherwise turn "1977-05-08T00:00:00Z"
        /// into a DateTime whose ToString() is culture-formatted ("05/08/1977 00:00:00").
        /// </summary>
        /// <param name="json">The JSON text.</param>
        /// <returns>The parsed object.</returns>
        public static JObject Parse(string json)
        {
            using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
            return JObject.Load(reader);
        }

        /// <summary>The first value of a field that may be a string or an array of strings.</summary>
        /// <param name="token">The field, possibly null.</param>
        /// <returns>The value, or null when missing or empty.</returns>
        public static string First(JToken token) => token switch
        {
            null => null,
            JArray array => array.Count > 0 ? array[0].ToString() : null,
            _ => token.Type == JTokenType.Null ? null : token.ToString()
        };

        /// <summary>Every value of a field that may be a string or an array of strings.</summary>
        /// <param name="token">The field, possibly null.</param>
        /// <returns>The values; empty when missing.</returns>
        public static List<string> All(JToken token) => token switch
        {
            null => new List<string>(),
            JArray array => array.Select(x => x.ToString()).ToList(),
            _ => token.Type == JTokenType.Null ? new List<string>() : new List<string> { token.ToString() }
        };
    }
}
