namespace Lidarr.Plugin.LiveMusicArchive.Core.Http
{
    /// <summary>
    /// The one gate every archive.org request passes through, shared by metadata, search and downloads.
    /// archive.org publishes no numbers, only "add delays", "limit concurrent requests" and "honor 429 /
    /// Retry-After" (archive.org/developers/bots.html), with <c>parallel -j4 --delay 1</c> as its example.
    /// These limits are deliberately below that example.
    /// </summary>
    public interface IArchiveOrgThrottle
    {
        /// <summary>Waits for permission to make one API request (metadata or search).</summary>
        /// <param name="cancellationToken">Cancels the wait.</param>
        /// <returns>A lease to dispose when the request has finished.</returns>
        Task<IDisposable> AcquireApiAsync(CancellationToken cancellationToken);

        /// <summary>Waits for permission to download one file.</summary>
        /// <param name="cancellationToken">Cancels the wait.</param>
        /// <returns>A lease to dispose when the download has finished.</returns>
        Task<IDisposable> AcquireDownloadAsync(CancellationToken cancellationToken);

        /// <summary>Holds back every new request until the given time has passed, after a 429 or 503.</summary>
        /// <param name="delay">How long archive.org asked us to wait.</param>
        void Pause(TimeSpan delay);

        /// <summary>Gets when requests may resume, or the past if not paused.</summary>
        DateTimeOffset PausedUntil { get; }
    }

    /// <summary>Default <see cref="IArchiveOrgThrottle"/>: one API request at a time, a second apart; two downloads at a time.</summary>
    public class ArchiveOrgThrottle : IArchiveOrgThrottle
    {
        /// <summary>Minimum time between the starts of two API requests.</summary>
        public static readonly TimeSpan ApiInterval = TimeSpan.FromSeconds(1);

        /// <summary>Most API requests in flight at once.</summary>
        public const int MaxConcurrentApiRequests = 1;

        /// <summary>Minimum time between the starts of two file downloads.</summary>
        public static readonly TimeSpan DownloadInterval = TimeSpan.FromSeconds(1);

        /// <summary>Most file downloads in flight at once, across every download in the queue.</summary>
        public const int MaxConcurrentDownloads = 2;

        // While waiting, re-check at least this often so a Pause() or a freed slot is noticed promptly.
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

        private readonly TimeProvider _time;
        private readonly object _lock = new();
        private readonly Gate _api;
        private readonly Gate _downloads;
        private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;

        /// <summary>Initializes a new instance of the <see cref="ArchiveOrgThrottle"/> class.</summary>
        /// <param name="clock">The clock used for spacing and pauses.</param>
        public ArchiveOrgThrottle(IArchiveOrgClock clock)
        {
            _time = clock.Time;
            _api = new Gate(MaxConcurrentApiRequests, ApiInterval);
            _downloads = new Gate(MaxConcurrentDownloads, DownloadInterval);
        }

        /// <inheritdoc />
        public DateTimeOffset PausedUntil
        {
            get
            {
                lock (_lock)
                {
                    return _pausedUntil;
                }
            }
        }

        /// <inheritdoc />
        public Task<IDisposable> AcquireApiAsync(CancellationToken cancellationToken) => AcquireAsync(_api, cancellationToken);

        /// <inheritdoc />
        public Task<IDisposable> AcquireDownloadAsync(CancellationToken cancellationToken) => AcquireAsync(_downloads, cancellationToken);

        /// <inheritdoc />
        public void Pause(TimeSpan delay)
        {
            lock (_lock)
            {
                var until = _time.GetUtcNow() + delay;
                if (until > _pausedUntil)
                {
                    _pausedUntil = until;
                }
            }
        }

        private async Task<IDisposable> AcquireAsync(Gate gate, CancellationToken cancellationToken)
        {
            while (true)
            {
                TimeSpan wait;
                lock (_lock)
                {
                    var now = _time.GetUtcNow();
                    var earliest = _pausedUntil > gate.NextStart ? _pausedUntil : gate.NextStart;
                    if (gate.Active < gate.Limit && earliest <= now)
                    {
                        gate.Active++;
                        gate.NextStart = now + gate.Interval;
                        return new Lease(this, gate);
                    }

                    wait = earliest > now ? earliest - now : PollInterval;
                }

                await Task.Delay(wait < PollInterval ? wait : PollInterval, _time, cancellationToken);
            }
        }

        private void Release(Gate gate)
        {
            lock (_lock)
            {
                gate.Active--;
            }
        }

        private sealed class Gate
        {
            public Gate(int limit, TimeSpan interval)
            {
                Limit = limit;
                Interval = interval;
            }

            public int Limit { get; }

            public TimeSpan Interval { get; }

            public int Active { get; set; }

            public DateTimeOffset NextStart { get; set; } = DateTimeOffset.MinValue;
        }

        private sealed class Lease : IDisposable
        {
            private readonly ArchiveOrgThrottle _owner;
            private readonly Gate _gate;
            private int _disposed;

            public Lease(ArchiveOrgThrottle owner, Gate gate)
            {
                _owner = owner;
                _gate = gate;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _owner.Release(_gate);
                }
            }
        }
    }
}
