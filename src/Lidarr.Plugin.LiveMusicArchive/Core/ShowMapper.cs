using System.Globalization;
using System.Text.RegularExpressions;
using Lidarr.Plugin.LiveMusicArchive.Core.Http;
using Newtonsoft.Json.Linq;
using NzbDrone.Core.MetadataSource.SkyHook.Resource;

namespace Lidarr.Plugin.LiveMusicArchive.Core
{
    /// <summary>A recording mapped to a release, with the facts used to rank it against other tapes.</summary>
    public class MappedRelease
    {
        /// <summary>Gets or sets the release in Lidarr's metadata contract.</summary>
        public ReleaseResource Resource { get; set; }

        /// <summary>Gets or sets the archive.org format the track list was built from.</summary>
        public string Format { get; set; }

        /// <summary>Gets or sets "SBD", "AUD" or null when the source type is unknown.</summary>
        public string Lineage { get; set; }
    }

    /// <summary>
    /// Maps Live Music Archive data onto Lidarr's metadata contract (the SkyHook resources):
    /// a show (artist + date) is an album, and each recording of it is a release.
    /// Knows nothing about how the result is delivered to Lidarr.
    /// </summary>
    public static class ShowMapper
    {
        /// <summary>Primary album type for shows; lets metadata profiles include or exclude them as a group.</summary>
        public const string AlbumType = "Broadcast";

        /// <summary>Secondary album type for shows.</summary>
        public const string SecondaryType = "Live";

        /// <summary>Release status for shows and recordings.</summary>
        public const string ReleaseStatus = "Bootleg";

        /// <summary>Disambiguation for shows kept after archive.org removed them.</summary>
        public const string RemovedDisambiguation = "Live Music Archive · removed from archive.org";

        /// <summary>Appended to the disambiguation of releases kept after archive.org removed them.</summary>
        public const string RemovedSuffix = " · removed from archive.org";

        private static readonly Regex SoundboardPattern = new(@"\bsbd\b|soundboard", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AudiencePattern = new(@"\baud\b|audience|\bmics?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Entry for the artist's album list; Lidarr filters these by metadata profile before fetching each one.</summary>
        /// <param name="artistMbid">The artist's MusicBrainz id.</param>
        /// <param name="date">The show date (yyyy-MM-dd).</param>
        /// <param name="title">The show title from <see cref="ShowCatalog.ShowTitle"/>.</param>
        /// <returns>The album summary.</returns>
        public static AlbumResource AlbumSummary(string artistMbid, string date, string title) => new()
        {
            Id = SyntheticIds.AlbumId(artistMbid, date),
            OldIds = new List<string>(),
            Title = title,
            Type = AlbumType,
            SecondaryTypes = new List<string> { SecondaryType },
            ReleaseStatuses = new List<string> { ReleaseStatus },
            ReleaseDate = ParseDate(date),
            Rating = new RatingResource()
        };

        /// <summary>The full album for a show, as returned for <c>album/{id}</c>.</summary>
        /// <param name="artist">The artist, as returned by the metadata server; its album list is dropped.</param>
        /// <param name="collection">The artist's archive.org collection.</param>
        /// <param name="date">The show date (yyyy-MM-dd).</param>
        /// <param name="title">The show title.</param>
        /// <param name="releases">The show's recordings, best first.</param>
        /// <returns>The album.</returns>
        public static AlbumResource Album(ArtistResource artist, string collection, string date, string title, IEnumerable<ReleaseResource> releases)
        {
            var releaseList = releases.ToList();
            artist.Albums = null;

            return new AlbumResource
            {
                Id = SyntheticIds.AlbumId(artist.Id, date),
                OldIds = new List<string>(),
                ArtistId = artist.Id,
                Artists = new List<ArtistResource> { artist },
                Title = title,
                Disambiguation = "Live Music Archive",
                Overview = $"{releaseList.Count} recording(s) on the Internet Archive Live Music Archive.",
                Type = AlbumType,
                SecondaryTypes = new List<string> { SecondaryType },
                ReleaseStatuses = new List<string> { ReleaseStatus },
                ReleaseDate = ParseDate(date),
                Genres = artist.Genres ?? new List<string>(),
                Images = new List<ImageResource>(),
                Links = new List<LinkResource>
                {
                    new() { Type = "archive", Target = $"{ArchiveOrgEndpoints.BaseUrl}/search?query={Uri.EscapeDataString($"collection:{collection} AND date:{date}")}" }
                },
                Rating = new RatingResource(),
                Releases = releaseList
            };
        }

        /// <summary>
        /// A cover for a show no taper uploaded artwork for: the band's own picture from Lidarr's metadata server,
        /// so the show isn't blank in Lidarr and in players. archive.org's other images are no use: its
        /// "spectrograms" are 800×200 waveforms of single tracks, and its thumbnail is the generic etree logo.
        /// </summary>
        /// <param name="artist">The artist, as returned by the metadata server.</param>
        /// <returns>The artist's poster as an album cover, else its fanart; null when it has neither.</returns>
        public static ImageResource BandCover(ArtistResource artist)
        {
            var image = new[] { "Poster", "Fanart" }
                .Select(type => artist?.Images?.FirstOrDefault(i => string.Equals(i.CoverType, type, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(i.Url)))
                .FirstOrDefault(i => i != null);
            return image == null ? null : new ImageResource { CoverType = "Cover", Url = image.Url };
        }

        /// <summary>Marks a saved show as no longer on archive.org; it stays in Lidarr with its releases.</summary>
        /// <param name="saved">The saved copy.</param>
        /// <returns>The same show, disambiguated as removed.</returns>
        public static AlbumResource MarkRemoved(AlbumResource saved)
        {
            saved.Disambiguation = RemovedDisambiguation;
            saved.Overview = "No longer on the Internet Archive Live Music Archive; kept from an earlier listing.";
            return saved;
        }

        /// <summary>
        /// Adds back every release from the saved copy that the new build lacks, so Lidarr never drops a
        /// release (and unlinks its downloaded tracks). That covers recordings archive.org removed, which are
        /// marked as such, and recordings that fell out of the most-downloaded top N.
        /// </summary>
        /// <param name="show">The show as just built.</param>
        /// <param name="saved">The previously saved copy, or null.</param>
        /// <param name="listed">Release ids of every recording archive.org still lists for the show.</param>
        /// <returns>The show, possibly with extra releases at the end.</returns>
        public static AlbumResource KeepRemovedReleases(AlbumResource show, AlbumResource saved, ISet<string> listed)
        {
            var current = show.Releases.Select(r => r.Id).ToHashSet();
            foreach (var release in saved?.Releases ?? new List<ReleaseResource>())
            {
                if (current.Contains(release.Id))
                {
                    continue;
                }

                if (!listed.Contains(release.Id) && !release.Disambiguation.EndsWith(RemovedSuffix, StringComparison.Ordinal))
                {
                    release.Disambiguation += RemovedSuffix;
                }

                show.Releases.Add(release);
            }

            return show;
        }


        /// <summary>One recording as a release, using its best downloadable format for the track list.</summary>
        /// <param name="itemMetadata">The item's <c>/metadata/{identifier}</c> response.</param>
        /// <param name="artistMbid">The artist's MusicBrainz id, used for each track.</param>
        /// <param name="date">The show date (yyyy-MM-dd).</param>
        /// <returns>The release, or null when the item has no downloadable audio.</returns>
        public static MappedRelease Release(JObject itemMetadata, string artistMbid, string date)
        {
            var metadata = itemMetadata["metadata"] as JObject ?? new JObject();
            var identifier = IaJson.First(metadata["identifier"]);
            var format = AudioFiles.DownloadableFormats(itemMetadata).FirstOrDefault();
            if (identifier == null || format == null)
            {
                return null;
            }

            var tracks = Tracks(identifier, artistMbid, AudioFiles.ForFormat(itemMetadata, format));

            var lineage = Lineage(identifier, string.Join(" ", IaJson.All(metadata["source"])));

            var release = new ReleaseResource
            {
                Id = SyntheticIds.ReleaseId(identifier),
                OldIds = new List<string>(),
                Title = IaJson.First(metadata["title"]) ?? identifier,
                Status = ReleaseStatus,
                // What a user sees when choosing between tapes of the same show.
                Disambiguation = string.Join(" · ", new[] { lineage, format, identifier }.Where(x => !string.IsNullOrEmpty(x))),
                Country = new List<string>(),
                Label = new List<string> { "Internet Archive" },
                Media = tracks.Select(t => t.MediumNumber).Distinct().OrderBy(m => m)
                    .Select(m => new MediumResource { Position = m, Name = "", Format = "Digital Media" }).ToList(),
                ReleaseDate = ParseDate(date),
                TrackCount = tracks.Count,
                Tracks = tracks
            };

            return new MappedRelease { Resource = release, Format = format, Lineage = lineage };
        }

        /// <summary>
        /// The tracks of a recording, one per file, in order. Shared by the metadata (the release's track list)
        /// and the download client (the tags written to each file), so both always agree; ids come from the
        /// original file, so any format of the recording maps to the same tracks.
        /// </summary>
        /// <param name="identifier">The archive.org identifier.</param>
        /// <param name="artistMbid">The artist's MusicBrainz id, or null when only identity and order matter.</param>
        /// <param name="files">The recording's files in one format, in playback order (<see cref="AudioFiles.ForFormat"/>).</param>
        /// <returns>The tracks, parallel to <paramref name="files"/>.</returns>
        public static List<TrackResource> Tracks(string identifier, string artistMbid, IReadOnlyList<AudioFile> files) =>
            files.Select((file, i) => new TrackResource
            {
                // Ids issued by earlier versions are listed as old ids so Lidarr migrates existing tracks.
                Id = SyntheticIds.TrackId(identifier, file.SongKey),
                OldIds = SyntheticIds.LegacyTrackIds(identifier, file.SongKey, file.Name),
                RecordingId = SyntheticIds.RecordingId(identifier, file.SongKey),
                OldRecordingIds = SyntheticIds.LegacyRecordingIds(identifier, file.SongKey, file.Name),
                ArtistId = artistMbid,
                TrackName = file.Title ?? Path.GetFileNameWithoutExtension(file.Name),
                TrackNumber = (file.Track > 0 ? file.Track : i + 1).ToString(CultureInfo.InvariantCulture),
                TrackPosition = i + 1,
                DurationMs = file.DurationMs,
                MediumNumber = file.Medium
            }).ToList();

        /// <summary>Orders releases best first: lossless, then soundboards, then most downloaded.</summary>
        /// <param name="releases">The mapped releases with their download counts; null releases are dropped.</param>
        /// <returns>The ordered releases.</returns>
        public static List<ReleaseResource> OrderReleases(IEnumerable<(MappedRelease Release, long Downloads)> releases) =>
            releases.Where(r => r.Release != null)
                .OrderBy(r => AudioFiles.FormatPreference.ToList().IndexOf(r.Release.Format))
                .ThenBy(r => r.Release.Lineage == "SBD" ? 0 : 1)
                .ThenByDescending(r => r.Downloads)
                .Select(r => r.Release.Resource)
                .ToList();

        /// <summary>Guesses whether a recording is a soundboard or audience tape from its identifier and source notes.</summary>
        /// <param name="identifier">The archive.org identifier.</param>
        /// <param name="source">The item's free-text source notes.</param>
        /// <returns>"SBD", "AUD" or null.</returns>
        public static string Lineage(string identifier, string source)
        {
            // Identifiers use dots as separators ("gd77-05-08.sbd.hicks"); treat them as word breaks.
            var text = $"{identifier} {source}".Replace('.', ' ');
            return SoundboardPattern.IsMatch(text) ? "SBD" : AudiencePattern.IsMatch(text) ? "AUD" : null;
        }

        private static DateTime? ParseDate(string date) =>
            DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
    }
}
