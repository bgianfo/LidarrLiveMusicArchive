using Newtonsoft.Json;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.MetadataSource.SkyHook.Resource;

namespace Lidarr.Plugin.LiveMusicArchive.Core
{
    /// <summary>
    /// Keeps a copy of every show served to Lidarr. archive.org does remove recordings (whole shows or single
    /// tapes, e.g. the Grateful Dead soundboards in 2005); if the plugin stopped describing them, Lidarr would
    /// delete the album, or the release and its tracks, from its catalogue. Saved copies let the plugin keep
    /// describing what it once offered.
    /// </summary>
    public interface IShowArchive
    {
        /// <summary>Saves the show as served.</summary>
        /// <param name="show">The album, including its releases.</param>
        void Save(AlbumResource show);

        /// <summary>The saved copy of a show.</summary>
        /// <param name="albumId">The synthetic album id.</param>
        /// <returns>The album, or null when it was never saved.</returns>
        AlbumResource Load(string albumId);

        /// <summary>
        /// Records that none of a show's recordings has audio Lidarr can import (only Shorten, say). Lidarr deletes
        /// an album without releases, so listing the show again would add and delete it on every refresh.
        /// </summary>
        /// <param name="albumId">The synthetic album id.</param>
        /// <param name="identifiers">The recordings that were examined.</param>
        void MarkUnplayable(string albumId, IEnumerable<string> identifiers);

        /// <summary>Whether a show was found unplayable and archive.org has added no recording of it since.</summary>
        /// <param name="albumId">The synthetic album id.</param>
        /// <param name="identifiers">The recordings archive.org lists for the show now.</param>
        /// <returns>True when every listed recording was already examined and found unplayable.</returns>
        bool IsUnplayable(string albumId, IEnumerable<string> identifiers);

        /// <summary>Forgets an unplayable mark, once the show has an importable recording.</summary>
        /// <param name="albumId">The synthetic album id.</param>
        void ClearUnplayable(string albumId);

        /// <summary>Every saved show of an artist.</summary>
        /// <param name="artistMbid">The artist's MusicBrainz id.</param>
        /// <returns>The saved albums.</returns>
        List<AlbumResource> ForArtist(string artistMbid);
    }

    /// <summary>Stores shows as JSON files under Lidarr's data folder, one folder per artist.</summary>
    public class ShowArchive : IShowArchive
    {
        private readonly string _folder;

        /// <summary>Initializes a new instance of the <see cref="ShowArchive"/> class.</summary>
        /// <param name="appFolderInfo">Locates Lidarr's data folder.</param>
        public ShowArchive(IAppFolderInfo appFolderInfo)
        {
            _folder = Path.Combine(appFolderInfo.AppDataFolder, "archiveorg-shows");
        }

        /// <inheritdoc />
        public void Save(AlbumResource show)
        {
            var path = PathFor(show.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(show));
            File.Move(temp, path, overwrite: true);
        }

        /// <inheritdoc />
        public AlbumResource Load(string albumId)
        {
            var path = PathFor(albumId);
            return path != null && File.Exists(path) ? Read(path) : null;
        }

        /// <inheritdoc />
        public void MarkUnplayable(string albumId, IEnumerable<string> identifiers)
        {
            var path = UnplayablePath(albumId);
            if (path != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(identifiers.Distinct().OrderBy(i => i, StringComparer.Ordinal)));
            }
        }

        /// <inheritdoc />
        public bool IsUnplayable(string albumId, IEnumerable<string> identifiers)
        {
            var path = UnplayablePath(albumId);
            if (path == null || !File.Exists(path))
            {
                return false;
            }

            try
            {
                var examined = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(path)).ToHashSet();
                return identifiers.All(examined.Contains);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <inheritdoc />
        public void ClearUnplayable(string albumId)
        {
            var path = UnplayablePath(albumId);
            if (path != null && File.Exists(path))
            {
                File.Delete(path);
            }
        }

        /// <inheritdoc />
        public List<AlbumResource> ForArtist(string artistMbid)
        {
            var folder = Path.Combine(_folder, artistMbid);
            return Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, "*.json").Select(Read).Where(a => a != null).ToList()
                : new List<AlbumResource>();
        }

        private static AlbumResource Read(string path)
        {
            try
            {
                return JsonConvert.DeserializeObject<AlbumResource>(File.ReadAllText(path), IaJson.LenientSettings);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private string UnplayablePath(string albumId) =>
            PathFor(albumId) is { } path ? Path.ChangeExtension(path, ".unplayable") : null;

        // Only synthetic ids are stored, which also keeps paths inside the folder.
        private string PathFor(string albumId) =>
            SyntheticIds.TryParseAlbumId(albumId, out var artistMbid, out _) ? Path.Combine(_folder, artistMbid, albumId + ".json") : null;
    }
}
