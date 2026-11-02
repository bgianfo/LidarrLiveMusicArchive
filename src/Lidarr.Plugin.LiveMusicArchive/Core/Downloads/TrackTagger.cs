using NLog;
using TagLib;

namespace Lidarr.Plugin.LiveMusicArchive.Core.Downloads
{
    /// <summary>Writes the tags Lidarr needs to recognise a downloaded recording.</summary>
    public interface ITrackTagger
    {
        /// <summary>Tags one downloaded audio file from its download's manifest.</summary>
        /// <param name="path">The audio file.</param>
        /// <param name="manifest">The download's manifest, carrying the album and release identity.</param>
        /// <param name="file">The file's manifest entry, carrying its track identity.</param>
        void Tag(string path, DownloadManifest manifest, ManifestFile file);
    }

    /// <summary>
    /// Tags downloads so Lidarr's importer finds the exact release. A completed download is imported knowing
    /// only its artist; Lidarr then picks candidates by the files' MusicBrainz release id tag, and only falls
    /// back to fuzzy-matching the album tag, which can't tell apart dozens of "1977-…" shows. So every file
    /// gets the synthetic release id (when there is one) and Lidarr's exact album title and artist.
    /// Each file then has to match its track: the taper's own tags are often missing or stale (no titles in
    /// FLAC originals, recording ids left by an earlier version), so title, track and disc number and the
    /// track's recording id are written too.
    /// </summary>
    public class TrackTagger : ITrackTagger
    {
        private readonly Logger _logger;

        /// <summary>Initializes a new instance of the <see cref="TrackTagger"/> class.</summary>
        /// <param name="logger">The logger.</param>
        public TrackTagger(Logger logger)
        {
            _logger = logger;
        }

        /// <inheritdoc />
        public void Tag(string path, DownloadManifest manifest, ManifestFile entry)
        {
            try
            {
                using var file = TagLib.File.Create(path);
                // FLAC and Ogg use Vorbis comments, .m4a uses iTunes atoms, MP3 uses ID3v2. An unexpected kind
                // falls back to the file's combined tag rather than failing.
                var type = file switch
                {
                    TagLib.Flac.File or TagLib.Ogg.File => TagTypes.Xiph,
                    TagLib.Mpeg4.File => TagTypes.Apple,
                    _ => TagTypes.Id3v2
                };
                var tag = file.GetTag(type, true) ?? file.Tag;

                if (!string.IsNullOrEmpty(entry?.Title))
                {
                    tag.Title = entry.Title;
                }

                if (entry?.TrackNumber > 0)
                {
                    tag.Track = (uint)entry.TrackNumber;
                }

                if (entry?.Disc > 0)
                {
                    tag.Disc = (uint)entry.Disc;
                }

                if (!string.IsNullOrEmpty(entry?.RecordingId))
                {
                    // Lidarr reads a file's recording id from this field (Picard's mapping).
                    tag.MusicBrainzTrackId = entry.RecordingId;
                }

                if (!string.IsNullOrEmpty(manifest.AlbumTitle))
                {
                    tag.Album = manifest.AlbumTitle;
                }

                if (!string.IsNullOrEmpty(manifest.ArtistName))
                {
                    tag.AlbumArtists = new[] { manifest.ArtistName };
                    if (tag.Performers == null || tag.Performers.Length == 0)
                    {
                        tag.Performers = new[] { manifest.ArtistName };
                    }
                }

                if (!string.IsNullOrEmpty(manifest.ReleaseId))
                {
                    tag.MusicBrainzReleaseId = manifest.ReleaseId;
                }

                if (!string.IsNullOrEmpty(manifest.AlbumId))
                {
                    tag.MusicBrainzReleaseGroupId = manifest.AlbumId;
                }

                file.Save();
            }
            catch (Exception ex) when (ex is UnsupportedFormatException || ex is CorruptFileException || ex is IOException)
            {
                // Untagged files can still be imported by hand; don't fail the whole download over one.
                _logger.Warn("Couldn't tag {0}: {1}", path, ex.Message);
            }
        }
    }
}
