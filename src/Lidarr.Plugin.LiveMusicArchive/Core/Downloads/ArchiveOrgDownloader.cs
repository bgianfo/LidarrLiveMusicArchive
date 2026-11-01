using System.Collections.Concurrent;
using System.Security.Cryptography;
using NLog;

namespace Lidarr.Plugin.LiveMusicArchive.Core.Downloads
{
    /// <summary>Runs downloads in the background, one task per download folder.</summary>
    public interface IArchiveOrgDownloader
    {
        /// <summary>Starts (or resumes) downloading the files in a folder's manifest, unless it's already running.</summary>
        /// <param name="folder">A download folder containing a manifest.</param>
        void Start(string folder);

        /// <summary>Whether a download for the folder is in progress.</summary>
        /// <param name="folder">The download folder.</param>
        /// <returns>True while the background task runs.</returns>
        bool IsRunning(string folder);

        /// <summary>
        /// Stops a running download, if any, and waits for it to finish. Short writes (creating folders, tagging,
        /// saving the manifest) share a lock with this method: one in progress completes before it returns, and
        /// later ones are skipped. A file transfer stops at its next read, and the HTTP client checks for
        /// cancellation before it creates, opens or moves a file. If the download is still running after
        /// a timeout (10 s), which needs a request that ignores cancellation, this returns anyway.
        /// </summary>
        /// <param name="folder">The download folder.</param>
        void Cancel(string folder);
    }

    /// <summary>
    /// Downloads every file listed in a <see cref="DownloadManifest"/>, skipping files already present
    /// and intact, verifying MD5 checksums, retrying failed files, and finally tagging them for import.
    /// </summary>
    public class ArchiveOrgDownloader : IArchiveOrgDownloader
    {
        /// <summary>
        /// How many times a file is fetched before the download is marked failed because it keeps arriving
        /// corrupt. Network errors and throttling are retried (and resumed) inside the HTTP client.
        /// </summary>
        public const int AttemptsPerFile = 3;

        private readonly IArchiveOrgClient _client;
        private readonly ITrackTagger _tagger;
        private readonly Logger _logger;
        private readonly ConcurrentDictionary<string, Job> _running = new();

        /// <summary>Initializes a new instance of the <see cref="ArchiveOrgDownloader"/> class.</summary>
        /// <param name="client">The archive.org client used to fetch files.</param>
        /// <param name="tagger">Tags the finished files so Lidarr can identify them.</param>
        /// <param name="logger">The logger.</param>
        public ArchiveOrgDownloader(IArchiveOrgClient client, ITrackTagger tagger, Logger logger)
        {
            _client = client;
            _tagger = tagger;
            _logger = logger;
        }

        /// <inheritdoc />
        public void Start(string folder)
        {
            var key = Path.GetFullPath(folder);
            _running.AddOrUpdate(key, Launch, (k, existing) => existing.Task.IsCompleted ? Launch(k) : existing);
        }

        /// <inheritdoc />
        public bool IsRunning(string folder) =>
            _running.TryGetValue(Path.GetFullPath(folder), out var job) && !job.Task.IsCompleted;

        /// <inheritdoc />
        public void Cancel(string folder)
        {
            if (_running.TryRemove(Path.GetFullPath(folder), out var job))
            {
                job.Cancel.Cancel();

                // Wait out any folder write already in progress; every later one re-checks the token under this
                // lock and is skipped, so the folder can be deleted as soon as Cancel returns.
                lock (job.WriteLock)
                {
                }

                if (!job.Task.Wait(CancelWaitTimeout))
                {
                    _logger.Debug("Download in {0} is still finishing a request after cancellation; it will write nothing more", folder);
                }
            }
        }

        /// <summary>Gets or sets how long <see cref="Cancel"/> waits for a download to stop; tests shorten it.</summary>
        internal TimeSpan CancelWaitTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>Downloads the folder's files and records the outcome in its manifest.</summary>
        /// <param name="folder">The download folder.</param>
        /// <param name="cancellationToken">Stops the download between files.</param>
        /// <returns>A task that completes when the download finishes, fails or is cancelled.</returns>
        /// <param name="writeLock">Serialises folder writes with <see cref="Cancel"/>; null when nothing can cancel concurrently.</param>
        internal async Task RunAsync(string folder, CancellationToken cancellationToken, object writeLock = null)
        {
            var guard = new WriteGuard(writeLock ?? new object(), cancellationToken);
            var manifest = DownloadManifest.Load(folder);
            if (manifest == null || manifest.Completed)
            {
                return;
            }

            try
            {
                foreach (var file in manifest.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await DownloadFileAsync(manifest, folder, file, guard, cancellationToken);
                }

                // Tagging changes the files, so it happens only once everything is verified. If Lidarr stops
                // before the manifest is saved, the next run re-downloads the (now mismatching) files.
                // Each tag is a short write under the guard, so Cancel waits for one in progress and skips the rest.
                foreach (var file in manifest.Files)
                {
                    if (!guard.Write(() => _tagger.Tag(DownloadManifest.LocalPath(folder, file.Name), manifest)))
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }
                }

                manifest.Completed = true;
                manifest.Error = null;
                _logger.Info("Downloaded {0} ({1} files)", manifest.Title, manifest.Files.Count);
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested || ex is OperationCanceledException)
            {
                // Cancelled: whatever failed afterwards is a consequence. Write nothing, because the folder may
                // already be deleted; the manifest on disk still describes a resumable download.
                return;
            }
            catch (Exception ex)
            {
                manifest.Error = ex.Message;
                _logger.Error(ex, "Download of {0} failed", manifest.Title);
            }

            guard.Write(() => manifest.Save(folder));
        }

        /// <summary>Whether a file on disk matches the manifest (MD5 when known, otherwise size).</summary>
        /// <param name="path">The local file.</param>
        /// <param name="file">The expected file.</param>
        /// <returns>True when the file is present and intact.</returns>
        internal static bool IsIntact(string path, ManifestFile file)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(file.Md5))
            {
                using var stream = File.OpenRead(path);
                return Convert.ToHexString(MD5.HashData(stream)).Equals(file.Md5, StringComparison.OrdinalIgnoreCase);
            }

            return file.Size <= 0 || new FileInfo(path).Length == file.Size;
        }

        private Job Launch(string folder)
        {
            var cancel = new CancellationTokenSource();
            var writeLock = new object();
            return new Job(Task.Run(() => RunAsync(folder, cancel.Token, writeLock)), cancel, writeLock);
        }

        private async Task DownloadFileAsync(DownloadManifest manifest, string folder, ManifestFile file, WriteGuard guard, CancellationToken cancellationToken)
        {
            var path = DownloadManifest.LocalPath(folder, file.Name);
            if (!guard.Write(() => Directory.CreateDirectory(Path.GetDirectoryName(path))))
            {
                throw new OperationCanceledException(cancellationToken);
            }

            for (var attempt = 1; ; attempt++)
            {
                if (IsIntact(path, file))
                {
                    return;
                }

                try
                {
                    await _client.DownloadFileAsync(manifest.Identifier, file.Name, path, cancellationToken);
                    if (IsIntact(path, file))
                    {
                        return;
                    }

                    File.Delete(path);
                    throw new IOException($"Checksum mismatch for {file.Name}");
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is not OperationCanceledException && attempt < AttemptsPerFile)
                {
                    _logger.Warn("Attempt {0} for {1}/{2} failed: {3}", attempt, manifest.Identifier, file.Name, ex.Message);
                }
            }
        }

        private sealed record Job(Task Task, CancellationTokenSource Cancel, object WriteLock);

        // Runs a folder write only if the download hasn't been cancelled, as one step with respect to Cancel.
        private sealed class WriteGuard
        {
            private readonly object _lock;
            private readonly CancellationToken _cancellationToken;

            public WriteGuard(object writeLock, CancellationToken cancellationToken)
            {
                _lock = writeLock;
                _cancellationToken = cancellationToken;
            }

            public bool Write(Action write)
            {
                lock (_lock)
                {
                    if (_cancellationToken.IsCancellationRequested)
                    {
                        return false;
                    }

                    write();
                    return true;
                }
            }
        }
    }
}
