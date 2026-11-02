using System.Security.Cryptography;
using System.Text;
using Lidarr.Plugin.LiveMusicArchive.Core;
using Lidarr.Plugin.LiveMusicArchive.Core.Downloads;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Lidarr.Plugin.LiveMusicArchive.Tests
{
    public sealed class ArchiveOrgDownloaderTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "lma-tests-" + Guid.NewGuid().ToString("N"));
        private readonly IArchiveOrgClient _client = Substitute.For<IArchiveOrgClient>();
        private readonly ITrackTagger _tagger = Substitute.For<ITrackTagger>();
        private readonly ArchiveOrgDownloader _downloader;

        public ArchiveOrgDownloaderTests()
        {
            _downloader = new ArchiveOrgDownloader(_client, _tagger, TestData.Logger);
        }

        public void Dispose()
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, true);
            }
        }

        [Fact]
        public async Task downloads_every_file_and_marks_the_manifest_complete()
        {
            ServeFiles(("d1/t01.flac", "one"), ("t02.flac", "two"));
            Manifest(("d1/t01.flac", "one"), ("t02.flac", "two")).Save(_folder);

            await _downloader.RunAsync(_folder, CancellationToken.None);

            var manifest = DownloadManifest.Load(_folder);
            Assert.True(manifest.Completed);
            Assert.Null(manifest.Error);
            Assert.Equal("one", File.ReadAllText(Path.Combine(_folder, "d1", "t01.flac")));
        }

        [Fact]
        public async Task tags_every_file_only_after_all_are_verified()
        {
            ServeFiles(("t01.flac", "one"), ("t02.flac", "two"));
            Manifest(("t01.flac", "one"), ("t02.flac", "two")).Save(_folder);

            await _downloader.RunAsync(_folder, CancellationToken.None);

            _tagger.Received(1).Tag(Path.Combine(Path.GetFullPath(_folder), "t01.flac"), Arg.Any<DownloadManifest>(), Arg.Any<ManifestFile>());
            _tagger.Received(1).Tag(Path.Combine(Path.GetFullPath(_folder), "t02.flac"), Arg.Any<DownloadManifest>(), Arg.Any<ManifestFile>());
        }

        [Fact]
        public async Task failed_downloads_are_not_tagged()
        {
            ServeFiles(("t01.flac", "garbage"));
            Manifest(("t01.flac", "one")).Save(_folder);

            await _downloader.RunAsync(_folder, CancellationToken.None);

            _tagger.DidNotReceiveWithAnyArgs().Tag(default, default, default);
        }

        [Fact]
        public async Task skips_files_already_present_and_intact()
        {
            ServeFiles(("t01.flac", "one"), ("t02.flac", "two"));
            Manifest(("t01.flac", "one"), ("t02.flac", "two")).Save(_folder);
            File.WriteAllText(Path.Combine(_folder, "t01.flac"), "one");

            await _downloader.RunAsync(_folder, CancellationToken.None);

            await _client.DidNotReceive().DownloadFileAsync("gd77", "t01.flac", Arg.Any<string>(), Arg.Any<CancellationToken>());
            await _client.Received(1).DownloadFileAsync("gd77", "t02.flac", Arg.Any<string>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task replaces_a_file_whose_checksum_is_wrong()
        {
            ServeFiles(("t01.flac", "one"));
            Manifest(("t01.flac", "one")).Save(_folder);
            File.WriteAllText(Path.Combine(_folder, "t01.flac"), "corrupt");

            await _downloader.RunAsync(_folder, CancellationToken.None);

            Assert.Equal("one", File.ReadAllText(Path.Combine(_folder, "t01.flac")));
            Assert.True(DownloadManifest.Load(_folder).Completed);
        }

        [Fact]
        public async Task gives_up_after_repeated_checksum_mismatches()
        {
            ServeFiles(("t01.flac", "garbage"));
            Manifest(("t01.flac", "one")).Save(_folder);

            await _downloader.RunAsync(_folder, CancellationToken.None);

            var manifest = DownloadManifest.Load(_folder);
            Assert.False(manifest.Completed);
            Assert.Contains("Checksum mismatch", manifest.Error);
            await _client.Received(ArchiveOrgDownloader.AttemptsPerFile).DownloadFileAsync("gd77", "t01.flac", Arg.Any<string>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task retries_transient_failures()
        {
            var calls = 0;
            _client.DownloadFileAsync("gd77", "t01.flac", Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    if (++calls == 1)
                    {
                        throw new IOException("connection reset");
                    }

                    File.WriteAllText(ci.ArgAt<string>(2), "one");
                    return Task.CompletedTask;
                });
            Manifest(("t01.flac", "one")).Save(_folder);

            await _downloader.RunAsync(_folder, CancellationToken.None);

            Assert.True(DownloadManifest.Load(_folder).Completed);
            Assert.Equal(2, calls);
        }

        [Fact]
        public async Task cancellation_leaves_the_manifest_resumable()
        {
            _client.DownloadFileAsync(default, default, default, default).ThrowsAsyncForAnyArgs(new OperationCanceledException());
            Manifest(("t01.flac", "one")).Save(_folder);

            await _downloader.RunAsync(_folder, CancellationToken.None);

            var manifest = DownloadManifest.Load(_folder);
            Assert.False(manifest.Completed);
            Assert.Null(manifest.Error);
        }

        [Fact]
        public async Task start_runs_in_the_background_and_reports_running_until_cancelled()
        {
            var gate = new TaskCompletionSource();
            // Like the real client, the download stops when cancelled.
            _client.DownloadFileAsync(default, default, default, default).ReturnsForAnyArgs(ci => gate.Task.WaitAsync(ci.ArgAt<CancellationToken>(3)));
            Manifest(("t01.flac", "one")).Save(_folder);

            _downloader.Start(_folder);
            Assert.True(_downloader.IsRunning(_folder));

            _downloader.Cancel(_folder);

            Assert.False(_downloader.IsRunning(_folder));
            await Task.Delay(50);
            Assert.False(DownloadManifest.Load(_folder).Completed);
            Assert.Null(DownloadManifest.Load(_folder).Error);
        }

        [Fact]
        public async Task shows_download_one_at_a_time_in_the_order_they_were_started()
        {
            var second = _folder + "-second";
            try
            {
                var firstFile = new TaskCompletionSource();
                _client.DownloadFileAsync("gd77", "a.flac", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async ci =>
                {
                    await firstFile.Task;
                    File.WriteAllText(ci.ArgAt<string>(2), "one");
                });
                ServeFiles(("b.flac", "two"));
                Manifest(("a.flac", "one")).Save(_folder);
                Manifest(("b.flac", "two")).Save(second);

                _downloader.Start(_folder);
                await WaitUntil(() => _client.ReceivedCalls().Any());
                _downloader.Start(second);
                await WaitUntil(() => _downloader.IsWaiting(second));

                // The second show touches nothing until the first is done.
                await Task.Delay(100);
                await _client.DidNotReceive().DownloadFileAsync("gd77", "b.flac", Arg.Any<string>(), Arg.Any<CancellationToken>());
                Assert.False(_downloader.IsWaiting(_folder));

                firstFile.SetResult();
                await WaitUntil(() => !_downloader.IsRunning(second));

                Assert.True(DownloadManifest.Load(_folder).Completed);
                Assert.True(DownloadManifest.Load(second).Completed);
                Assert.False(_downloader.IsWaiting(second));
            }
            finally
            {
                Directory.Delete(second, true);
            }
        }

        [Fact]
        public async Task cancelling_a_waiting_show_leaves_it_untouched_and_the_running_one_unaffected()
        {
            var second = _folder + "-second";
            try
            {
                var firstFile = new TaskCompletionSource();
                _client.DownloadFileAsync("gd77", "a.flac", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async ci =>
                {
                    await firstFile.Task;
                    File.WriteAllText(ci.ArgAt<string>(2), "one");
                });
                Manifest(("a.flac", "one")).Save(_folder);
                Manifest(("b.flac", "two")).Save(second);
                _downloader.Start(_folder);
                await WaitUntil(() => _client.ReceivedCalls().Any());
                _downloader.Start(second);
                await WaitUntil(() => _downloader.IsWaiting(second));

                _downloader.Cancel(second);

                Assert.False(_downloader.IsWaiting(second));
                Assert.False(DownloadManifest.Load(second).Completed);
                Assert.Null(DownloadManifest.Load(second).Error);
                firstFile.SetResult();
                await WaitUntil(() => !_downloader.IsRunning(_folder));
                Assert.True(DownloadManifest.Load(_folder).Completed);
                await _client.DidNotReceive().DownloadFileAsync("gd77", "b.flac", Arg.Any<string>(), Arg.Any<CancellationToken>());
            }
            finally
            {
                Directory.Delete(second, true);
            }
        }

        [Fact]
        public async Task a_cancelled_download_writes_nothing_even_if_a_request_outlives_the_cancel()
        {
            var gate = new TaskCompletionSource();
            var inFlight = new TaskCompletionSource();
            // A request that ignores cancellation and then fails (as a checksum mismatch would).
            _client.DownloadFileAsync(default, default, default, default).ReturnsForAnyArgs(_ =>
            {
                inFlight.TrySetResult();
                return gate.Task;
            });
            Manifest(("t01.flac", "one")).Save(_folder);
            _downloader.CancelWaitTimeout = TimeSpan.FromMilliseconds(50);

            _downloader.Start(_folder);
            await inFlight.Task.WaitAsync(TimeSpan.FromSeconds(5));
            _downloader.Cancel(_folder);
            // Lidarr deletes the folder (RemoveItem with deleteData) while the request is still in flight.
            Directory.Delete(_folder, true);
            gate.SetResult();
            await Task.Delay(500);

            Assert.False(Directory.Exists(_folder));
            await _client.Received(1).DownloadFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task cancel_waits_for_a_tag_in_progress_and_later_tags_are_skipped()
        {
            var tagStarted = new TaskCompletionSource();
            var releaseTag = new ManualResetEventSlim();
            _tagger.When(t => t.Tag(Arg.Any<string>(), Arg.Any<DownloadManifest>(), Arg.Any<ManifestFile>())).Do(_ =>
            {
                tagStarted.TrySetResult();
                releaseTag.Wait(TimeSpan.FromSeconds(10));
            });
            ServeFiles(("t01.flac", "one"), ("t02.flac", "two"));
            Manifest(("t01.flac", "one"), ("t02.flac", "two")).Save(_folder);

            _downloader.Start(_folder);
            await tagStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var cancel = Task.Run(() => _downloader.Cancel(_folder));

            // The first tag is still writing, so Cancel must not have returned.
            await Task.Delay(200);
            Assert.False(cancel.IsCompleted);

            releaseTag.Set();
            await cancel.WaitAsync(TimeSpan.FromSeconds(5));

            // The second tag and the "completed" manifest save were skipped.
            _tagger.Received(1).Tag(Arg.Any<string>(), Arg.Any<DownloadManifest>(), Arg.Any<ManifestFile>());
            Assert.False(DownloadManifest.Load(_folder).Completed);
        }

        [Fact]
        public void intact_check_falls_back_to_size_without_a_checksum()
        {
            Directory.CreateDirectory(_folder);
            var path = Path.Combine(_folder, "t.flac");
            File.WriteAllText(path, "abc");

            Assert.True(ArchiveOrgDownloader.IsIntact(path, new ManifestFile { Size = 3 }));
            Assert.False(ArchiveOrgDownloader.IsIntact(path, new ManifestFile { Size = 4 }));
            Assert.True(ArchiveOrgDownloader.IsIntact(path, new ManifestFile()));
            Assert.False(ArchiveOrgDownloader.IsIntact(path + ".missing", new ManifestFile()));
        }

        private DownloadManifest Manifest(params (string Name, string Content)[] files) => new()
        {
            DownloadId = "lma-test",
            Title = "test",
            Identifier = "gd77",
            Format = "Flac",
            Files = files.Select(f => new ManifestFile { Name = f.Name, Size = f.Content.Length, Md5 = Md5(f.Content) }).ToList()
        };

        private void ServeFiles(params (string Name, string Content)[] files)
        {
            foreach (var (name, content) in files)
            {
                _client.DownloadFileAsync("gd77", name, Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(ci =>
                    {
                        File.WriteAllText(ci.ArgAt<string>(2), content);
                        return Task.CompletedTask;
                    });
            }
        }

        private static async Task WaitUntil(Func<bool> condition)
        {
            for (var i = 0; i < 200 && !condition(); i++)
            {
                await Task.Delay(25);
            }

            Assert.True(condition());
        }

        private static string Md5(string content) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }
}
