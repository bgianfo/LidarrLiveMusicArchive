using System.Security.Cryptography;
using System.Text;
using FluentValidation.Results;
using Lidarr.Plugin.LiveMusicArchive.Core;
using Lidarr.Plugin.LiveMusicArchive.Core.Downloads;
using Lidarr.Plugin.LiveMusicArchive.Indexers;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;

namespace Lidarr.Plugin.LiveMusicArchive.Download
{
    /// <summary>
    /// Downloads Live Music Archive recordings straight from archive.org over HTTP. Each download is a
    /// folder holding a <see cref="DownloadManifest"/>; the client keeps no other state, so the queue is
    /// rebuilt from disk and interrupted downloads resume after a restart.
    /// </summary>
    public class ArchiveOrgDownloadClient : DownloadClientBase<ArchiveOrgDownloadSettings>
    {
        private readonly IArchiveOrgClient _client;
        private readonly IArchiveOrgDownloader _downloader;

        /// <summary>Initializes a new instance of the <see cref="ArchiveOrgDownloadClient"/> class.</summary>
        /// <param name="client">The archive.org client, for file lists.</param>
        /// <param name="downloader">Runs downloads in the background.</param>
        /// <param name="configService">Lidarr's configuration.</param>
        /// <param name="diskProvider">Lidarr's file system access.</param>
        /// <param name="remotePathMappingService">Lidarr's remote path mappings.</param>
        /// <param name="localizationService">Lidarr's localization.</param>
        /// <param name="logger">The logger.</param>
        public ArchiveOrgDownloadClient(IArchiveOrgClient client,
                                        IArchiveOrgDownloader downloader,
                                        IConfigService configService,
                                        IDiskProvider diskProvider,
                                        IRemotePathMappingService remotePathMappingService,
                                        ILocalizationService localizationService,
                                        Logger logger)
            : base(configService, diskProvider, remotePathMappingService, localizationService, logger)
        {
            _client = client;
            _downloader = downloader;
        }

        /// <inheritdoc />
        public override string Name => "Live Music Archive";

        /// <inheritdoc />
        public override string Protocol => nameof(ArchiveOrgDownloadProtocol);

        /// <inheritdoc />
        public override Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer)
        {
            var release = remoteAlbum.Release;
            if (!ReleaseOffers.TryParseDownloadUrl(release.DownloadUrl, out var identifier, out var format))
            {
                throw new InvalidOperationException($"Not a Live Music Archive release: {release.DownloadUrl}");
            }

            var files = AudioFiles.ForFormat(_client.GetItemMetadata(identifier), format);
            if (files.Count == 0)
            {
                throw new InvalidOperationException($"{identifier} has no downloadable {format} files.");
            }

            var album = remoteAlbum.Albums?.FirstOrDefault();
            var manifest = new DownloadManifest
            {
                DownloadId = DownloadId(identifier, format),
                Title = release.Title,
                Identifier = identifier,
                Format = format,
                ArtistName = remoteAlbum.Artist?.Name ?? release.Artist,
                AlbumTitle = album?.Title ?? release.Album,
                AlbumId = album?.ForeignAlbumId,
                // Shows this plugin created have a release per recording; MusicBrainz shows don't.
                ReleaseId = SyntheticIds.TryParseAlbumId(album?.ForeignAlbumId, out _, out _) ? SyntheticIds.ReleaseId(identifier) : null,
                Files = ManifestFiles(identifier, files, isPluginShow: SyntheticIds.TryParseAlbumId(album?.ForeignAlbumId, out _, out _))
            };

            var folder = Path.Combine(Settings.DownloadPath, FolderName(release.Title, manifest.DownloadId));
            manifest.Save(folder);
            _downloader.Start(folder);

            _logger.Info("Queued {0} ({1} {2} files)", release.Title, files.Count, format);
            return Task.FromResult(manifest.DownloadId);
        }

        /// <inheritdoc />
        public override IEnumerable<DownloadClientItem> GetItems()
        {
            if (string.IsNullOrWhiteSpace(Settings.DownloadPath) || !Directory.Exists(Settings.DownloadPath))
            {
                yield break;
            }

            foreach (var folder in Directory.EnumerateDirectories(Settings.DownloadPath))
            {
                var manifest = DownloadManifest.Load(folder);
                if (manifest == null)
                {
                    continue;
                }

                // Interrupted by a restart: pick up where it left off.
                if (!manifest.Completed && manifest.Error == null && !_downloader.IsRunning(folder))
                {
                    _downloader.Start(folder);
                }

                yield return ToItem(manifest, folder);
            }
        }

        /// <inheritdoc />
        public override void RemoveItem(DownloadClientItem item, bool deleteData)
        {
            var folder = item.OutputPath.FullPath;
            _downloader.Cancel(folder);

            if (deleteData)
            {
                DeleteItemData(item);
            }
            else
            {
                // Dropping the manifest removes the item from the queue but leaves the files.
                File.Delete(Path.Combine(folder, DownloadManifest.FileName));
            }
        }

        /// <inheritdoc />
        public override DownloadClientInfo GetStatus() => new()
        {
            IsLocalhost = true,
            OutputRootFolders = new List<OsPath> { new(Settings.DownloadPath) }
        };

        /// <summary>
        /// The files to download, each with the track identity to tag it with. The identities come from the same
        /// mapping as the release's track list, so Lidarr matches every file to its track exactly.
        /// </summary>
        /// <param name="identifier">The archive.org identifier.</param>
        /// <param name="files">The files of the chosen format, in playback order.</param>
        /// <param name="isPluginShow">Whether the album is one of this plugin's shows; only those have its recording ids.</param>
        /// <returns>The manifest entries.</returns>
        internal static List<ManifestFile> ManifestFiles(string identifier, IReadOnlyList<AudioFile> files, bool isPluginShow)
        {
            var tracks = ShowMapper.Tracks(identifier, null, files);
            return files.Zip(tracks, (f, t) => new ManifestFile
            {
                Name = f.Name,
                Size = f.Size,
                Md5 = f.Md5,
                Title = t.TrackName,
                TrackNumber = t.TrackPosition,
                Disc = t.MediumNumber,
                RecordingId = isPluginShow ? t.RecordingId : null
            }).ToList();
        }

        /// <summary>A stable id for a recording in one format.</summary>
        /// <param name="identifier">The archive.org identifier.</param>
        /// <param name="format">The archive.org format.</param>
        /// <returns>"lma-" followed by 16 hex characters.</returns>
        internal static string DownloadId(string identifier, string format) =>
            "lma-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{identifier}|{format}")))[..16].ToLowerInvariant();

        /// <summary>A folder name for a download: the release title made file-system safe, plus the id.</summary>
        /// <param name="title">The release title.</param>
        /// <param name="downloadId">The download id, which keeps names unique.</param>
        /// <returns>The folder name.</returns>
        internal static string FolderName(string title, string downloadId)
        {
            var invalid = Path.GetInvalidFileNameChars().Concat(new[] { ':', '*', '?', '"', '<', '>', '|', '\\', '/' }).ToHashSet();
            var safe = new string(title.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
            return $"{(safe.Length > 150 ? safe[..150] : safe)} ({downloadId})";
        }

        /// <summary>The queue entry for a download folder.</summary>
        /// <param name="manifest">The folder's manifest.</param>
        /// <param name="folder">The download folder.</param>
        /// <returns>The item Lidarr shows and imports.</returns>
        internal DownloadClientItem ToItem(DownloadManifest manifest, string folder)
        {
            var status = manifest.Completed ? DownloadItemStatus.Completed
                : manifest.Error != null ? DownloadItemStatus.Failed
                : DownloadItemStatus.Downloading;

            return new DownloadClientItem
            {
                DownloadClientInfo = DownloadClientItemClientInfo.FromDownloadClient(this, false),
                DownloadId = manifest.DownloadId,
                Title = manifest.Title,
                TotalSize = manifest.TotalSize,
                RemainingSize = manifest.RemainingSize(folder),
                OutputPath = new OsPath(folder),
                Message = manifest.Error,
                Status = status,
                CanMoveFiles = manifest.Completed,
                CanBeRemoved = status != DownloadItemStatus.Downloading
            };
        }

        /// <inheritdoc />
        protected override void Test(List<ValidationFailure> failures)
        {
            if (string.IsNullOrWhiteSpace(Settings.DownloadPath))
            {
                failures.Add(new ValidationFailure(nameof(Settings.DownloadPath), "Download path is required."));
                return;
            }

            try
            {
                Directory.CreateDirectory(Settings.DownloadPath);
                var probe = Path.Combine(Settings.DownloadPath, ".archiveorg-write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
            }
            catch (Exception ex)
            {
                failures.Add(new ValidationFailure(nameof(Settings.DownloadPath), $"Download path is not writable: {ex.Message}"));
            }
        }
    }
}
