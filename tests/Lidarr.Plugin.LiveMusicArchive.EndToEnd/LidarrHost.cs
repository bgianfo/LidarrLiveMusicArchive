using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace Lidarr.Plugin.LiveMusicArchive.EndToEnd
{
    /// <summary>
    /// A real Lidarr process with the freshly built plugin installed, in a throwaway data folder. Lidarr itself is
    /// downloaded once from its GitHub release (the version the plugin compiles against, or LMA_E2E_LIDARR as
    /// "version" or "version:branch") and cached in .e2e/.
    /// </summary>
    public sealed class LidarrHost : IAsyncDisposable
    {
        private const string ApiKey = "e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e0";

        private readonly Process _process;
        private readonly StringBuilder _output = new();

        private LidarrHost(Process process, string root, int port)
        {
            _process = process;
            Root = root;
            BaseUrl = $"http://127.0.0.1:{port}";
            Http = new HttpClient { BaseAddress = new Uri(BaseUrl + "/api/v1/"), Timeout = TimeSpan.FromSeconds(100) };
            Http.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        }

        public string BaseUrl { get; }

        public string Root { get; }

        public string MusicFolder => Path.Combine(Root, "music");

        public string DownloadFolder => Path.Combine(Root, "downloads");

        public HttpClient Http { get; }

        /// <summary>Gets Lidarr's console output so far, attached to failures.</summary>
        public string Output
        {
            get
            {
                lock (_output)
                {
                    return _output.ToString();
                }
            }
        }

        public static async Task<LidarrHost> StartAsync(string archiveOrgUrl)
        {
            var binary = await EnsureLidarrAsync();
            var root = Path.Combine(Path.GetTempPath(), "lma-e2e-" + Guid.NewGuid().ToString("N"));
            var data = Path.Combine(root, "data");
            var plugin = Path.Combine(data, "plugins", "bgianfo", "LidarrLiveMusicArchive");
            Directory.CreateDirectory(plugin);
            Directory.CreateDirectory(Path.Combine(root, "music"));
            Directory.CreateDirectory(Path.Combine(root, "downloads"));

            var pluginOutput = Metadata("PluginOutputDirectory");
            foreach (var name in new[] { "Lidarr.Plugin.LiveMusicArchive.dll", "Lidarr.Plugin.LiveMusicArchive.deps.json" })
            {
                File.Copy(Path.Combine(pluginOutput, name), Path.Combine(plugin, name));
            }

            var port = FreePort();
            File.WriteAllText(Path.Combine(data, "config.xml"), $"""
                <Config>
                  <BindAddress>127.0.0.1</BindAddress>
                  <Port>{port}</Port>
                  <ApiKey>{ApiKey}</ApiKey>
                  <AuthenticationMethod>External</AuthenticationMethod>
                  <AuthenticationRequired>DisabledForLocalAddresses</AuthenticationRequired>
                  <LaunchBrowser>False</LaunchBrowser>
                  <AnalyticsEnabled>False</AnalyticsEnabled>
                  <UpdateMechanism>External</UpdateMechanism>
                  <LogLevel>debug</LogLevel>
                </Config>
                """);

            var start = new ProcessStartInfo(binary, new[] { "-nobrowser", $"-data={data}" })
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.Environment["LIDARR_LMA_ARCHIVE_URL"] = archiveOrgUrl;
            var process = Process.Start(start);
            var host = new LidarrHost(process, root, port);
            process.OutputDataReceived += (_, e) => host.Append(e.Data);
            process.ErrorDataReceived += (_, e) => host.Append(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await host.WaitUntilAsync(async () => (await host.TryGet("system/status")) != null, TimeSpan.FromMinutes(2), "Lidarr to start");
            return host;
        }

        public async Task<JsonNode> Get(string path) =>
            JsonNode.Parse(await Check(await Http.GetAsync(path), path).Content.ReadAsStringAsync());

        public async Task<JsonNode> Send(HttpMethod method, string path, JsonNode body)
        {
            var response = await Http.SendAsync(new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) });
            var text = await Check(response, path).Content.ReadAsStringAsync();
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }

        /// <summary>Waits for a condition, failing with Lidarr's recent output if it never holds.</summary>
        public async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string what)
        {
            var end = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < end)
            {
                if (_process.HasExited)
                {
                    throw new InvalidOperationException($"Lidarr exited while waiting for {what}.\n{Tail()}");
                }

                if (await condition())
                {
                    return;
                }

                await Task.Delay(1000);
            }

            throw new TimeoutException($"Timed out after {timeout.TotalSeconds:F0}s waiting for {what}.\n{Tail()}");
        }

        /// <summary>Waits until Lidarr has no queued or running commands (refreshes, searches, imports).</summary>
        public Task WaitForIdleAsync(TimeSpan timeout) =>
            WaitUntilAsync(async () => (await Get("command")).AsArray().All(c => c["status"]?.GetValue<string>() is not ("queued" or "started")), timeout, "Lidarr to be idle");

        public string Tail(int lines = 60) => string.Join('\n', Output.Split('\n').TakeLast(lines));

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }

            try
            {
                Directory.Delete(Root, true);
            }
            catch (IOException)
            {
                // Lidarr's log files can linger briefly; the temp folder is cleaned up eventually anyway.
            }
        }

        private static HttpResponseMessage Check(HttpResponseMessage response, string path) =>
            response.IsSuccessStatusCode ? response : throw new HttpRequestException($"Lidarr {(int)response.StatusCode} for {path}: {response.Content.ReadAsStringAsync().Result}");

        private async Task<JsonNode> TryGet(string path)
        {
            try
            {
                using var response = await Http.GetAsync(path);
                return response.IsSuccessStatusCode ? JsonNode.Parse(await response.Content.ReadAsStringAsync()) : null;
            }
            catch (HttpRequestException)
            {
                return null;
            }
        }

        private void Append(string line)
        {
            if (line != null)
            {
                lock (_output)
                {
                    _output.AppendLine(line);
                }
            }
        }

        private static string Metadata(string key) =>
            typeof(LidarrHost).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == key).Value;

        private static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        // Downloads and unpacks Lidarr's linux-x64 build once per version.
        private static async Task<string> EnsureLidarrAsync()
        {
            var spec = Environment.GetEnvironmentVariable("LMA_E2E_LIDARR") ?? Metadata("LidarrVersion");
            var parts = spec.Split(':');
            var version = parts[0];
            var branch = parts.Length > 1 ? parts[1] : "master";
            var folder = Path.Combine(Metadata("CacheDirectory"), $"lidarr-{version}");
            var binary = Path.Combine(folder, "Lidarr", "Lidarr");
            if (File.Exists(binary))
            {
                return binary;
            }

            var url = $"https://github.com/Lidarr/Lidarr/releases/download/v{version}/Lidarr.{branch}.{version}.linux-core-x64.tar.gz";
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            await using var download = await http.GetStreamAsync(url);
            await using var gzip = new GZipStream(download, CompressionMode.Decompress);
            var staging = folder + ".partial";
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, true);
            }

            Directory.CreateDirectory(staging);
            await TarFile.ExtractToDirectoryAsync(gzip, staging, overwriteFiles: true);
            Directory.Move(staging, folder);
            return binary;
        }
    }
}
