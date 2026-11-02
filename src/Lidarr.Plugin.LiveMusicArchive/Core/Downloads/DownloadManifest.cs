using Newtonsoft.Json;

namespace Lidarr.Plugin.LiveMusicArchive.Core.Downloads
{
    /// <summary>A file the manifest expects to find in the download folder.</summary>
    public class ManifestFile
    {
        /// <summary>Gets or sets the file name relative to the archive.org item, and to the download folder.</summary>
        public string Name { get; set; }

        /// <summary>Gets or sets the expected size in bytes, or 0 when archive.org doesn't publish one.</summary>
        public long Size { get; set; }

        /// <summary>Gets or sets the expected MD5 checksum, or null when unknown.</summary>
        public string Md5 { get; set; }

        /// <summary>Gets or sets the song title to tag the file with, or null to leave the file's own.</summary>
        public string Title { get; set; }

        /// <summary>Gets or sets the track's position in its release (Lidarr's absolute track number), or 0.</summary>
        public int TrackNumber { get; set; }

        /// <summary>Gets or sets the disc or set number, or 0.</summary>
        public int Disc { get; set; }

        /// <summary>
        /// Gets or sets the track's recording id, tagged as the MusicBrainz track id. Lidarr matches each file to a
        /// track by it, overriding whatever the taper's tags say. Null for MusicBrainz shows.
        /// </summary>
        public string RecordingId { get; set; }
    }

    /// <summary>
    /// Describes one download, stored as a JSON file inside its folder. The download client keeps no other
    /// state, so downloads survive Lidarr restarts: the manifest says what should be there, and the files on
    /// disk say how far it got.
    /// </summary>
    public class DownloadManifest
    {
        /// <summary>The manifest's file name inside each download folder.</summary>
        public const string FileName = ".archiveorg-download.json";

        /// <summary>Gets or sets the id Lidarr tracks this download by.</summary>
        public string DownloadId { get; set; }

        /// <summary>Gets or sets the release title shown in Lidarr's queue.</summary>
        public string Title { get; set; }

        /// <summary>Gets or sets the archive.org identifier being downloaded.</summary>
        public string Identifier { get; set; }

        /// <summary>Gets or sets the archive.org format being downloaded.</summary>
        public string Format { get; set; }

        /// <summary>Gets or sets the artist name to tag the files with, as Lidarr knows it.</summary>
        public string ArtistName { get; set; }

        /// <summary>Gets or sets the album title to tag the files with, exactly as Lidarr knows it.</summary>
        public string AlbumTitle { get; set; }

        /// <summary>Gets or sets the album's foreign id, tagged as the MusicBrainz release group id.</summary>
        public string AlbumId { get; set; }

        /// <summary>Gets or sets the release's foreign id, tagged as the MusicBrainz release id; null for MusicBrainz shows.</summary>
        public string ReleaseId { get; set; }

        /// <summary>Gets or sets the files that make up the download.</summary>
        public List<ManifestFile> Files { get; set; } = new();

        /// <summary>Gets or sets a value indicating whether every file has been downloaded and verified.</summary>
        public bool Completed { get; set; }

        /// <summary>Gets or sets the reason the download gave up, or null.</summary>
        public string Error { get; set; }

        /// <summary>Gets the total expected size in bytes.</summary>
        [JsonIgnore]
        public long TotalSize => Files.Sum(f => f.Size);

        /// <summary>Reads the manifest from a download folder.</summary>
        /// <param name="folder">The download folder.</param>
        /// <returns>The manifest, or null when the folder has none or it's unreadable.</returns>
        public static DownloadManifest Load(string folder)
        {
            var path = Path.Combine(folder, FileName);
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                return JsonConvert.DeserializeObject<DownloadManifest>(File.ReadAllText(path));
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>Writes the manifest atomically, so a crash never leaves a half-written file.</summary>
        /// <param name="folder">The download folder; created if missing.</param>
        public void Save(string folder)
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, FileName);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(this, Formatting.Indented));
            File.Move(temp, path, overwrite: true);
        }

        /// <summary>Bytes still to download, judged by the files present (including partial ".part" files).</summary>
        /// <param name="folder">The download folder.</param>
        /// <returns>The remaining bytes; 0 when completed.</returns>
        public long RemainingSize(string folder)
        {
            if (Completed)
            {
                return 0;
            }

            long done = 0;
            foreach (var file in Files)
            {
                var path = LocalPath(folder, file.Name);
                var size = File.Exists(path) ? new FileInfo(path).Length : File.Exists(path + ".part") ? new FileInfo(path + ".part").Length : 0;
                done += file.Size > 0 ? Math.Min(size, file.Size) : 0;
            }

            return Math.Max(0, TotalSize - done);
        }

        /// <summary>
        /// Where a manifest file lives on disk. archive.org names may contain sub-folders; any attempt to
        /// escape the download folder ("..", rooted paths) is rejected.
        /// </summary>
        /// <param name="folder">The download folder.</param>
        /// <param name="name">The file name from the manifest.</param>
        /// <returns>The full local path.</returns>
        /// <exception cref="InvalidOperationException">The name points outside the folder.</exception>
        public static string LocalPath(string folder, string name)
        {
            var root = Path.GetFullPath(folder);
            var path = Path.GetFullPath(Path.Combine(root, name));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Refusing to write '{name}' outside the download folder.");
            }

            return path;
        }
    }
}
