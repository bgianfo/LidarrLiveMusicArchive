using Lidarr.Plugin.LiveMusicArchive.Core.Downloads;
using Xunit;

namespace Lidarr.Plugin.LiveMusicArchive.Tests
{
    public sealed class DownloadManifestTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "lma-tests-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, true);
            }
        }

        [Fact]
        public void saves_and_loads()
        {
            Manifest().Save(_folder);

            var loaded = DownloadManifest.Load(_folder);

            Assert.Equal("lma-1", loaded.DownloadId);
            Assert.Equal(2, loaded.Files.Count);
            Assert.Equal(300, loaded.TotalSize);
            Assert.False(File.Exists(Path.Combine(_folder, DownloadManifest.FileName + ".tmp")));
        }

        [Fact]
        public void missing_or_corrupt_manifest_loads_as_null()
        {
            Assert.Null(DownloadManifest.Load(_folder));

            Directory.CreateDirectory(_folder);
            File.WriteAllText(Path.Combine(_folder, DownloadManifest.FileName), "{ not json");
            Assert.Null(DownloadManifest.Load(_folder));
        }

        [Fact]
        public void remaining_size_counts_finished_and_partial_files()
        {
            var manifest = Manifest();
            Directory.CreateDirectory(Path.Combine(_folder, "d1"));
            File.WriteAllBytes(Path.Combine(_folder, "d1", "t01.flac"), new byte[100]);
            File.WriteAllBytes(Path.Combine(_folder, "t02.flac.part"), new byte[50]);

            Assert.Equal(150, manifest.RemainingSize(_folder));

            manifest.Completed = true;
            Assert.Equal(0, manifest.RemainingSize(_folder));
        }

        [Fact]
        public void older_manifests_without_local_names_keep_the_archive_path()
        {
            var file = new ManifestFile { Name = "d1/t01.flac" };

            Assert.Equal("d1/t01.flac", file.DiskName);
            file.LocalName = "t01.flac";
            Assert.Equal("t01.flac", file.DiskName);
        }

        [Theory]
        [InlineData("../escape.flac")]
        [InlineData("d1/../../escape.flac")]
        [InlineData("/etc/passwd")]
        public void refuses_paths_outside_the_folder(string name) =>
            Assert.Throws<InvalidOperationException>(() => DownloadManifest.LocalPath(_folder, name));

        [Fact]
        public void keeps_sub_folders_inside_the_folder() =>
            Assert.Equal(Path.Combine(Path.GetFullPath(_folder), "d1", "t01.flac"), DownloadManifest.LocalPath(_folder, "d1/t01.flac"));

        private static DownloadManifest Manifest() => new()
        {
            DownloadId = "lma-1",
            Title = "Grateful Dead - 1977-05-08",
            Identifier = "gd77",
            Format = "Flac",
            Files = new List<ManifestFile>
            {
                new() { Name = "d1/t01.flac", Size = 100 },
                new() { Name = "t02.flac", Size = 200 }
            }
        };
    }
}
