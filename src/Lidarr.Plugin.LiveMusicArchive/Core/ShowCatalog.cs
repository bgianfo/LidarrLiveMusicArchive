using System.Globalization;
using System.Text.RegularExpressions;

namespace Lidarr.Plugin.LiveMusicArchive.Core
{
    /// <summary>Optional era filter, e.g. "1977" or "1972-1974". Empty means every year.</summary>
    public class YearRange
    {
        private static readonly Regex Pattern = new(@"^\s*(\d{4})\s*(?:-\s*(\d{4})\s*)?$", RegexOptions.Compiled);

        /// <summary>Gets the first year included, or null for no lower bound.</summary>
        public int? From { get; private set; }

        /// <summary>Gets the last year included, or null for no upper bound.</summary>
        public int? To { get; private set; }

        /// <summary>Parses "1977", "1972-1974" or an empty string.</summary>
        /// <param name="value">The setting value.</param>
        /// <param name="range">The parsed range; unbounded when parsing fails.</param>
        /// <returns>False for text that isn't a year or an ascending range.</returns>
        public static bool TryParse(string value, out YearRange range)
        {
            range = new YearRange();
            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            var match = Pattern.Match(value);
            if (!match.Success)
            {
                return false;
            }

            var from = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var to = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : from;
            if (from > to)
            {
                return false;
            }

            range = new YearRange { From = from, To = to };
            return true;
        }

        /// <summary>Whether a year falls in the range.</summary>
        /// <param name="year">The year.</param>
        /// <returns>True when included.</returns>
        public bool Contains(int year) => (From == null || year >= From) && (To == null || year <= To);
    }

    /// <summary>Groups an artist's recordings into shows.</summary>
    public static class ShowCatalog
    {
        /// <summary>Groups recordings into shows by exact date, dropping undated items and "1966-00-00" placeholders.</summary>
        /// <param name="recordings">The artist's recordings.</param>
        /// <param name="years">The years to keep.</param>
        /// <returns>Recordings keyed by show date (yyyy-MM-dd).</returns>
        public static Dictionary<string, List<RecordingSummary>> GroupByShow(IEnumerable<RecordingSummary> recordings, YearRange years)
        {
            var shows = new Dictionary<string, List<RecordingSummary>>();
            foreach (var recording in recordings)
            {
                if (!DateTime.TryParseExact(recording.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                    !years.Contains(date.Year))
                {
                    continue;
                }

                if (!shows.TryGetValue(recording.Date, out var list))
                {
                    shows[recording.Date] = list = new List<RecordingSummary>();
                }

                list.Add(recording);
            }

            return shows;
        }

        /// <summary>"1977-05-08: Barton Hall, Cornell University, Ithaca, NY", using the venue most tapers agree on.</summary>
        /// <param name="date">The show date (yyyy-MM-dd).</param>
        /// <param name="recordings">The show's recordings.</param>
        /// <returns>The album title.</returns>
        public static string ShowTitle(string date, IReadOnlyCollection<RecordingSummary> recordings)
        {
            static string MostCommon(IEnumerable<string> values) =>
                values.Where(v => !string.IsNullOrWhiteSpace(v))
                    .GroupBy(v => v.Trim())
                    .OrderByDescending(g => g.Count())
                    .ThenBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => g.Key)
                    .FirstOrDefault();

            var place = string.Join(", ", new[] { MostCommon(recordings.Select(r => r.Venue)), MostCommon(recordings.Select(r => r.Coverage)) }
                .Where(x => x != null));

            return place.Length > 0 ? $"{date}: {place}" : date;
        }
    }
}
