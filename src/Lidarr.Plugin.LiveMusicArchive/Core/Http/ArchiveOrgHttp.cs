using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http.Proxy;

namespace Lidarr.Plugin.LiveMusicArchive.Core.Http
{
    /// <summary>HTTP access to archive.org, throttled, retried and identified as this plugin.</summary>
    public interface IArchiveOrgHttp
    {
        /// <summary>GETs an API URL (metadata or search) and returns the body.</summary>
        /// <param name="url">The URL.</param>
        /// <param name="cancellationToken">Cancels the request, including any backoff wait.</param>
        /// <returns>The response body.</returns>
        Task<string> GetStringAsync(string url, CancellationToken cancellationToken);

        /// <summary>Downloads a file, resuming a previous partial download ("{destination}.part") when the server's copy is unchanged.</summary>
        /// <param name="url">The file URL.</param>
        /// <param name="destination">The local path to write once complete.</param>
        /// <param name="cancellationToken">Cancels the download.</param>
        /// <returns>A task that completes when the file is in place.</returns>
        Task DownloadFileAsync(string url, string destination, CancellationToken cancellationToken);
    }

    /// <summary>
    /// The plugin's own HTTP client for archive.org. Lidarr's IHttpClient can't be used: its dispatcher throws
    /// on any User-Agent other than Lidarr's, which archive.org's automated-access rules require, and it
    /// rejects Range requests, which resuming needs. Lidarr's proxy settings are applied here instead.
    /// </summary>
    public sealed class ArchiveOrgHttp : IArchiveOrgHttp, IDisposable
    {
        /// <summary>How long an API request may take before it counts as failed (and is retried).</summary>
        public static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(60);

        /// <summary>How long a download may receive no data before it counts as stalled (and is resumed).</summary>
        public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

        private readonly IArchiveOrgThrottle _throttle;
        private readonly IHttpProxySettingsProvider _proxySettings;
        private readonly TimeProvider _time;
        private readonly Logger _logger;
        private readonly Lazy<HttpClient> _client;

        /// <summary>Initializes a new instance of the <see cref="ArchiveOrgHttp"/> class.</summary>
        /// <param name="throttle">The shared archive.org throttle.</param>
        /// <param name="proxySettings">Lidarr's proxy configuration.</param>
        /// <param name="clock">The clock used for backoff waits.</param>
        /// <param name="logger">The logger.</param>
        public ArchiveOrgHttp(IArchiveOrgThrottle throttle, IHttpProxySettingsProvider proxySettings, IArchiveOrgClock clock, Logger logger)
        {
            _throttle = throttle;
            _proxySettings = proxySettings;
            _time = clock.Time;
            _logger = logger;
            _client = new Lazy<HttpClient>(CreateClient);
        }

        /// <summary>
        /// Gets the User-Agent sent with every request: tool name and version as archive.org's
        /// automated-access rules require, plus where to find it and the Lidarr version hosting it.
        /// </summary>
        public static string UserAgent { get; } =
            $"Lidarr.Plugin.LiveMusicArchive/{typeof(ArchiveOrgHttp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0"} " +
            $"(+https://github.com/bgianfo/LidarrLiveMusicArchive) {BuildInfo.AppName}/{BuildInfo.Version}";

        /// <summary>Gets or sets the handler factory; tests replace it to fake the network.</summary>
        internal Func<HttpMessageHandler> HandlerFactory { get; set; }

        /// <summary>Gets or sets the jitter source, returning values in [0, 1); tests make it deterministic.</summary>
        internal Func<double> Random { get; set; } = System.Random.Shared.NextDouble;

        /// <inheritdoc />
        public async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
        {
            for (var attempt = 1; ; attempt++)
            {
                TimeSpan delay;
                using (await _throttle.AcquireApiAsync(cancellationToken))
                {
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        timeout.CancelAfter(ApiTimeout);
                        using var response = await _client.Value.GetAsync(url, HttpCompletionOption.ResponseContentRead, timeout.Token);

                        if (response.IsSuccessStatusCode)
                        {
                            return await response.Content.ReadAsStringAsync(cancellationToken);
                        }

                        delay = Backoff(url, attempt, response);
                    }
                    catch (Exception ex) when (IsTransient(ex, cancellationToken) && attempt < RetryPolicy.MaxAttempts)
                    {
                        delay = RetryPolicy.Delay(attempt, null, _time.GetUtcNow(), Random());
                        _logger.Debug("archive.org request {0} failed ({1}); retrying in {2:F1}s", url, ex.Message, delay.TotalSeconds);
                    }
                }

                await Task.Delay(delay, _time, cancellationToken);
            }
        }

        /// <inheritdoc />
        public async Task DownloadFileAsync(string url, string destination, CancellationToken cancellationToken)
        {
            var part = destination + ".part";
            var etagPath = part + ".etag";

            for (var attempt = 1; ; attempt++)
            {
                TimeSpan delay;
                using (await _throttle.AcquireDownloadAsync(cancellationToken))
                {
                    try
                    {
                        if (await TryDownloadAsync(url, part, etagPath, attempt, cancellationToken) is { } backoff)
                        {
                            delay = backoff;
                        }
                        else
                        {
                            File.Move(part, destination, overwrite: true);
                            File.Delete(etagPath);
                            return;
                        }
                    }
                    catch (Exception ex) when (IsTransient(ex, cancellationToken) && attempt < RetryPolicy.MaxAttempts)
                    {
                        // The partial file stays; the next attempt resumes it.
                        delay = RetryPolicy.Delay(attempt, null, _time.GetUtcNow(), Random());
                        _logger.Debug("Download of {0} interrupted ({1}); resuming in {2:F1}s", url, ex.Message, delay.TotalSeconds);
                    }
                }

                await Task.Delay(delay, _time, cancellationToken);
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_client.IsValueCreated)
            {
                _client.Value.Dispose();
            }
        }

        // Returns null when the file is complete in "part", or the delay before the next attempt.
        private async Task<TimeSpan?> TryDownloadAsync(string url, string part, string etagPath, int attempt, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(part)));
            var existing = File.Exists(part) ? new FileInfo(part).Length : 0;
            var etag = File.Exists(etagPath) ? File.ReadAllText(etagPath) : null;

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (existing > 0 && etag != null)
            {
                // If-Range: the server sends the rest only if the file is unchanged, otherwise the whole file.
                request.Headers.Range = new RangeHeaderValue(existing, null);
                request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue(etag, isWeak: etag.StartsWith("W/", StringComparison.Ordinal)));
            }

            using var response = await _client.Value.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
            {
                // Nothing past what we have: the previous attempt got the whole file.
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                return Backoff(url, attempt, response);
            }

            if (response.Headers.ETag is { } newTag)
            {
                File.WriteAllText(etagPath, newTag.ToString());
            }

            var resume = response.StatusCode == HttpStatusCode.PartialContent;
            await using var output = new FileStream(part, resume ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await CopyWithStallTimeoutAsync(input, output, cancellationToken);
            return null;
        }

        private static async Task CopyWithStallTimeoutAsync(Stream input, Stream output, CancellationToken cancellationToken)
        {
            var buffer = new byte[81920];
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            while (true)
            {
                stall.CancelAfter(StallTimeout);
                var read = await input.ReadAsync(buffer, stall.Token);
                if (read == 0)
                {
                    return;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }

        // Fails permanently on client errors; otherwise computes the wait and, for 429/503, pauses all traffic.
        private TimeSpan Backoff(string url, int attempt, HttpResponseMessage response)
        {
            if (!RetryPolicy.IsRetryable(response.StatusCode) || attempt >= RetryPolicy.MaxAttempts)
            {
                throw new HttpRequestException($"archive.org returned {(int)response.StatusCode} {response.ReasonPhrase} for {url}", null, response.StatusCode);
            }

            var delay = RetryPolicy.Delay(attempt, response.Headers.RetryAfter, _time.GetUtcNow(), Random());
            if (RetryPolicy.IsThrottling(response.StatusCode))
            {
                _throttle.Pause(delay);
                _logger.Warn("archive.org asked us to slow down ({0}); pausing all requests for {1:F0}s", (int)response.StatusCode, delay.TotalSeconds);
            }

            return delay;
        }

        // Network failures and our own timeouts are worth retrying; the caller cancelling is not.
        private static bool IsTransient(Exception ex, CancellationToken cancellationToken) =>
            !cancellationToken.IsCancellationRequested &&
            (ex is HttpRequestException { StatusCode: null } || ex is IOException || ex is OperationCanceledException);

        private HttpClient CreateClient()
        {
            var handler = HandlerFactory?.Invoke() ?? CreateHandler();
            var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            return client;
        }

        private SocketsHttpHandler CreateHandler()
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = ArchiveOrgThrottle.MaxConcurrentDownloads + ArchiveOrgThrottle.MaxConcurrentApiRequests
            };

            // Honour Lidarr's Settings → General → Proxy.
            if (_proxySettings.GetProxySettings() is { } proxy)
            {
                var scheme = proxy.Type switch
                {
                    ProxyType.Socks4 => "socks4",
                    ProxyType.Socks5 => "socks5",
                    _ => "http"
                };

                handler.Proxy = new WebProxy($"{scheme}://{proxy.Host}:{proxy.Port}", proxy.BypassLocalAddress, proxy.BypassListAsArray)
                {
                    Credentials = string.IsNullOrEmpty(proxy.Username) ? null : new NetworkCredential(proxy.Username, proxy.Password)
                };
                handler.UseProxy = true;
            }

            return handler;
        }
    }
}
