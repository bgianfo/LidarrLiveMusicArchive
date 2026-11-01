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
        void Tag(string path, DownloadManifest manifest);
    }

    /// <summary>
    /// Tags downloads so Lidarr's importer finds the exact release. A completed download is imported knowing
    /// only its artist; Lidarr then picks candidates by the files' MusicBrainz release id tag, and only falls
    /// back to fuzzy-matching the album tag, which can't tell apart dozens of "1977-…" shows. So every file
    /// gets the synthetic release id (when there is one) and Lidarr's exact album title and artist.
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
        public void Tag(string path, DownloadManifest manifest)
        {
            try
            {
                using var file = TagLib.File.Create(path);
                // FLAC and Ogg use Vorbis comments; everything else here is MP3 with ID3v2.
                var tag = file.GetTag(file is TagLib.Flac.File || file is TagLib.Ogg.File ? TagTypes.Xiph : TagTypes.Id3v2, true);

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
