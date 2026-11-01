using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;

namespace Lidarr.Plugin.LiveMusicArchive.Metadata
{
    /// <summary>
    /// Writes no files. It exists to give the plugin an enable switch and settings page under
    /// Settings → Metadata (the same pattern Tubifarry uses for its metadata sources).
    /// </summary>
    public class LiveMusicArchiveMetadata : MetadataBase<LiveMusicArchiveSettings>
    {
        /// <inheritdoc />
        public override string Name => "Live Music Archive (archive.org)";

        /// <inheritdoc />
        public override MetadataFile FindMetadataFile(Artist artist, string path) => null;

        /// <inheritdoc />
        public override MetadataFileResult ArtistMetadata(Artist artist) => null;

        /// <inheritdoc />
        public override MetadataFileResult AlbumMetadata(Artist artist, Album album, string albumPath) => null;

        /// <inheritdoc />
        public override MetadataFileResult TrackMetadata(Artist artist, TrackFile trackFile) => null;

        /// <inheritdoc />
        public override List<ImageFileResult> ArtistImages(Artist artist) => new();

        /// <inheritdoc />
        public override List<ImageFileResult> AlbumImages(Artist artist, Album album, string albumPath) => new();

        /// <inheritdoc />
        public override List<ImageFileResult> TrackImages(Artist artist, TrackFile trackFile) => new();
    }
}
