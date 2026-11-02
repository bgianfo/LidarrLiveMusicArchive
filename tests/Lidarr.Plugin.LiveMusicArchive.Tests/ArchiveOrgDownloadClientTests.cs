using Lidarr.Plugin.LiveMusicArchive.Core;
using Lidarr.Plugin.LiveMusicArchive.Core.Downloads;
using Lidarr.Plugin.LiveMusicArchive.Download;
using NSubstitute;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using Xunit;

namespace Lidarr.Plugin.LiveMusicArchive.Tests
{
    public sealed class ArchiveOrgDownloadClientTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "lma-tests-" + Guid.NewGuid().ToString("N"));
        private readonly IArchiveOrgClient _client = Substitute.For<IArchiveOrgClient>();
        private readonly IArchiveOrgDownloader _downloader = Substitute.For<IArchiveOrgDownloader>();
        private readonly ArchiveOrgDownloadClient _downloadClient;

        public ArchiveOrgDownloadClientTests()
        {
            _downloadClient = new ArchiveOrgDownloadClient(_client, _downloader, Substitute.For<IConfigService>(), Substitute.For<IDiskProvider>(),
                Substitute.For<IRemotePathMappingService>(), Substitute.For<ILocalizationService>(), TestData.Logger)
            {
                Definition = new DownloadClientDefinition { Id = 3, Name = "LMA", Settings = new ArchiveOrgDownloadSettings { DownloadPath = _root } }
            };
            _client.GetItemMetadata(TestData.SoundboardItem).Returns(TestData.Item(TestData.SoundboardItem));
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        [Fact]
        public async Task download_writes_a_manifest_and_starts_the_downloader()
        {
            var id = await _downloadClient.Download(Remote(ReleaseOffers.DownloadUrl(TestData.SoundboardItem, "VBR MP3")), null);

            var folder = Assert.Single(Directory.GetDirectories(_root));
            var manifest = DownloadManifest.Load(folder);
            Assert.Equal(id, manifest.DownloadId);
            Assert.Equal(TestData.SoundboardItem, manifest.Identifier);
            Assert.Equal("VBR MP3", manifest.Format);
            Assert.All(manifest.Files, f => Assert.EndsWith(".mp3", f.Name));
            Assert.All(manifest.Files, f => Assert.NotNull(f.Md5));
            _downloader.Received().Start(folder);
        }

        [Fact]
        public async Task download_records_the_album_identity_to_tag_files_with()
        {
            var remote = Remote(ReleaseOffers.DownloadUrl(TestData.SoundboardItem, "VBR MP3"));
            remote.Artist = new NzbDrone.Core.Music.Artist { Metadata = new NzbDrone.Core.Music.ArtistMetadata { Name = "Grateful Dead" } };
            remote.Albums = new List<NzbDrone.Core.Music.Album> { new() { Title = "1977-05-08: Barton Hall", ForeignAlbumId = SyntheticIds.AlbumId(TestData.GratefulDead, "1977-05-08") } };

            await _downloadClient.Download(remote, null);

            var manifest = DownloadManifest.Load(Assert.Single(Directory.GetDirectories(_root)));
            Assert.Equal("Grateful Dead", manifest.ArtistName);
            Assert.Equal("1977-05-08: Barton Hall", manifest.AlbumTitle);
            Assert.Equal(SyntheticIds.ReleaseId(TestData.SoundboardItem), manifest.ReleaseId);
        }

        [Fact]
        public async Task musicbrainz_shows_get_no_synthetic_release_id()
        {
            var remote = Remote(ReleaseOffers.DownloadUrl(TestData.SoundboardItem, "VBR MP3"));
            remote.Albums = new List<NzbDrone.Core.Music.Album> { new() { Title = "1969-11-07: Fillmore West", ForeignAlbumId = "ac3b135e-9c03-4639-a44e-bba6eb717694" } };

            await _downloadClient.Download(remote, null);

            var manifest = DownloadManifest.Load(Assert.Single(Directory.GetDirectories(_root)));
            Assert.Null(manifest.ReleaseId);
            Assert.Equal("ac3b135e-9c03-4639-a44e-bba6eb717694", manifest.AlbumId);
        }

        [Fact]
        public void manifest_files_carry_the_same_track_identity_as_the_release()
        {
            var item = TestData.Item(TestData.SoundboardItem);
            var release = ShowMapper.Release(item, TestData.GratefulDead, "1977-05-08").Resource;

            var files = ArchiveOrgDownloadClient.ManifestFiles(TestData.SoundboardItem, AudioFiles.ForFormat(item, "VBR MP3"), isPluginShow: true);

            Assert.Equal(release.Tracks.Select(t => t.RecordingId), files.Select(f => f.RecordingId));
            Assert.Equal(release.Tracks.Select(t => t.TrackName), files.Select(f => f.Title));
            Assert.Equal(release.Tracks.Select(t => t.TrackPosition), files.Select(f => f.TrackNumber));
            Assert.Equal(release.Tracks.Select(t => t.MediumNumber), files.Select(f => f.Disc));
        }

        [Fact]
        public void a_lossless_download_maps_to_the_same_tracks_as_the_mp3_release()
        {
            var item = TestData.SyntheticItem("x",
                new { name = "t01.flac", format = "Flac", source = "original", title = "Opener" },
                new { name = "t02.flac", format = "Flac", source = "original", title = "Closer" },
                new { name = "t01.mp3", format = "VBR MP3", source = "derivative", original = "t01.flac" },
                new { name = "t02.mp3", format = "VBR MP3", source = "derivative", original = "t02.flac" });

            var flac = ArchiveOrgDownloadClient.ManifestFiles("x", AudioFiles.ForFormat(item, "Flac"), isPluginShow: true);
            var mp3 = ArchiveOrgDownloadClient.ManifestFiles("x", AudioFiles.ForFormat(item, "VBR MP3"), isPluginShow: true);

            Assert.Equal(flac.Select(f => f.RecordingId), mp3.Select(f => f.RecordingId));
            Assert.Equal(new[] { "Opener", "Closer" }, mp3.Select(f => f.Title));
        }

        [Fact]
        public void downloads_are_flat_because_lidarr_groups_albums_by_folder()
        {
            var files = ArchiveOrgDownloadClient.ManifestFiles(TestData.ShortenItem, AudioFiles.ForFormat(TestData.Item(TestData.ShortenItem), "VBR MP3"), isPluginShow: true);

            Assert.Equal(19, files.Count);
            Assert.All(files, f => Assert.DoesNotContain("/", f.LocalName));
            Assert.Equal("dispatch1996-04-16d1t01.mp3", files[0].LocalName);
            Assert.Equal("dispatch1996-04-16d1.shnf/dispatch1996-04-16d1t01.mp3", files[0].Name);
        }

        [Fact]
        public void clashing_names_from_different_folders_stay_apart()
        {
            var names = ArchiveOrgDownloadClient.FlatNames(new[] { "set1/t01.flac", "set2/t01.flac", "t02.flac" });

            Assert.Equal("set1_t01.flac", names["set1/t01.flac"]);
            Assert.Equal("set2_t01.flac", names["set2/t01.flac"]);
            Assert.Equal("t02.flac", names["t02.flac"]);
        }

        [Fact]
        public void musicbrainz_shows_get_no_recording_ids()
        {
            var item = TestData.Item(TestData.SoundboardItem);

            Assert.All(ArchiveOrgDownloadClient.ManifestFiles(TestData.SoundboardItem, AudioFiles.ForFormat(item, "VBR MP3"), isPluginShow: false), f => Assert.Null(f.RecordingId));
        }

        [Fact]
        public async Task download_rejects_foreign_urls() =>
            await Assert.ThrowsAsync<InvalidOperationException>(() => _downloadClient.Download(Remote("https://example.com/x.torrent"), null));

        [Fact]
        public async Task download_rejects_formats_the_item_does_not_have() =>
            await Assert.ThrowsAsync<InvalidOperationException>(() => _downloadClient.Download(Remote(ReleaseOffers.DownloadUrl(TestData.SoundboardItem, "Flac")), null));

        [Fact]
        public void queue_reflects_each_download_folder_state()
        {
            Save("done", m => m.Completed = true);
            Save("failed", m => m.Error = "Checksum mismatch");
            Save("running", _ => { });
            Save("waiting", _ => { });
            _downloader.IsRunning(Path.Combine(_root, "running")).Returns(true);
            _downloader.IsRunning(Path.Combine(_root, "waiting")).Returns(true);
            _downloader.IsWaiting(Path.Combine(_root, "waiting")).Returns(true);
            Directory.CreateDirectory(Path.Combine(_root, "not-ours"));

            var items = _downloadClient.GetItems().ToDictionary(i => i.DownloadId);

            Assert.Equal(4, items.Count);
            // Waiting for the show ahead of it to finish.
            Assert.Equal(DownloadItemStatus.Queued, items["waiting"].Status);
            Assert.Equal(DownloadItemStatus.Completed, items["done"].Status);
            Assert.True(items["done"].CanMoveFiles);
            Assert.Equal(0, items["done"].RemainingSize);
            Assert.Equal(DownloadItemStatus.Failed, items["failed"].Status);
            Assert.Equal("Checksum mismatch", items["failed"].Message);
            Assert.Equal(DownloadItemStatus.Downloading, items["running"].Status);
            Assert.False(items["running"].CanBeRemoved);
            Assert.Equal(100, items["running"].RemainingSize);
            _downloader.DidNotReceive().Start(Arg.Any<string>());
        }

        [Fact]
        public void interrupted_downloads_resume_when_the_queue_is_read()
        {
            Save("interrupted", _ => { });

            Assert.Single(_downloadClient.GetItems());

            _downloader.Received().Start(Path.Combine(_root, "interrupted"));
        }

        [Fact]
        public void removing_without_data_keeps_files_but_leaves_the_queue()
        {
            var folder = Save("done", m => m.Completed = true);
            File.WriteAllText(Path.Combine(folder, "t01.mp3"), "audio");

            _downloadClient.RemoveItem(_downloadClient.GetItems().Single(), deleteData: false);

            Assert.Empty(_downloadClient.GetItems());
            Assert.True(File.Exists(Path.Combine(folder, "t01.mp3")));
            _downloader.Received().Cancel(folder);
        }

        [Fact]
        public void status_reports_the_download_folder()
        {
            var status = _downloadClient.GetStatus();

            Assert.True(status.IsLocalhost);
            Assert.Equal(_root, Assert.Single(status.OutputRootFolders).FullPath.TrimEnd('/'));
        }

        [Fact]
        public void test_requires_a_writable_download_path()
        {
            Assert.True(_downloadClient.Test().IsValid);

            ((ArchiveOrgDownloadSettings)_downloadClient.Definition.Settings).DownloadPath = "";
            Assert.False(_downloadClient.Test().IsValid);
        }

        [Fact]
        public void folder_names_are_file_system_safe_and_unique()
        {
            var name = ArchiveOrgDownloadClient.FolderName("Grateful Dead - 1977-05-08: Barton Hall / Cornell? [FLAC]", "lma-abc");

            Assert.Equal("Grateful Dead - 1977-05-08_ Barton Hall _ Cornell_ [FLAC] (lma-abc)", name);
            Assert.True(ArchiveOrgDownloadClient.FolderName(new string('x', 400), "id").Length < 200);
        }

        [Fact]
        public void download_ids_look_like_lma_ids() =>
            Assert.Matches("^lma-[0-9a-f]{16}$", ArchiveOrgDownloadClient.NewDownloadId());

        [Fact]
        public async Task grabbing_a_release_again_starts_a_separate_download()
        {
            // What Lidarr does after a failed import: grab the same release again, then remove the failed download.
            var url = ReleaseOffers.DownloadUrl(TestData.SoundboardItem, "VBR MP3");
            var first = await _downloadClient.Download(Remote(url), null);
            var second = await _downloadClient.Download(Remote(url), null);

            Assert.NotEqual(first, second);
            var folders = Directory.GetDirectories(_root);
            Assert.Equal(2, folders.Length);
            Assert.Equal(new[] { first, second }.OrderBy(x => x), folders.Select(f => DownloadManifest.Load(f).DownloadId).OrderBy(x => x));
        }

        private static RemoteAlbum Remote(string url) => new() { Release = new ReleaseInfo { Title = "Grateful Dead - 1977-05-08: Barton Hall [SBD] [MP3 VBR V0]", DownloadUrl = url } };

        private string Save(string id, Action<DownloadManifest> configure)
        {
            var folder = Path.Combine(_root, id);
            var manifest = new DownloadManifest
            {
                DownloadId = id,
                Title = id,
                Identifier = "gd77",
                Format = "VBR MP3",
                Files = new List<ManifestFile> { new() { Name = "t01.mp3", Size = 100 } }
            };
            configure(manifest);
            manifest.Save(folder);
            return folder;
        }
    }
}
