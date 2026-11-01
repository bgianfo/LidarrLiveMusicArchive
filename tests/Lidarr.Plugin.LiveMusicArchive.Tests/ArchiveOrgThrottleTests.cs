using Lidarr.Plugin.LiveMusicArchive.Core.Http;
using Xunit;

namespace Lidarr.Plugin.LiveMusicArchive.Tests
{
    public class ArchiveOrgThrottleTests
    {
        private readonly FakeClock _clock = new();
        private readonly ArchiveOrgThrottle _throttle;

        public ArchiveOrgThrottleTests()
        {
            _throttle = new ArchiveOrgThrottle(_clock);
        }

        [Fact]
        public async Task api_requests_start_at_least_a_second_apart()
        {
            // Time moves only when the test moves it, so the check is exact.
            (await _throttle.AcquireApiAsync(CancellationToken.None)).Dispose();
            var second = _throttle.AcquireApiAsync(CancellationToken.None);

            _clock.Fake.Advance(ArchiveOrgThrottle.ApiInterval - TimeSpan.FromMilliseconds(1));
            await Task.Delay(50);
            Assert.False(second.IsCompleted);

            _clock.Fake.Advance(TimeSpan.FromMilliseconds(1));
            for (var i = 0; i < 100 && !second.IsCompleted; i++)
            {
                await Task.Delay(10);
            }

            Assert.True(second.IsCompleted);
            (await second).Dispose();
        }

        [Fact]
        public async Task only_one_api_request_runs_at_a_time()
        {
            using var first = await _throttle.AcquireApiAsync(CancellationToken.None);
            var second = _throttle.AcquireApiAsync(CancellationToken.None);

            _clock.Fake.Advance(TimeSpan.FromSeconds(10));
            await Task.Delay(50);
            Assert.False(second.IsCompleted);

            first.Dispose();
            (await _clock.Run(second)).Dispose();
        }

        [Fact]
        public async Task at_most_two_files_download_at_once()
        {
            using var a = await _clock.Run(_throttle.AcquireDownloadAsync(CancellationToken.None));
            using var b = await _clock.Run(_throttle.AcquireDownloadAsync(CancellationToken.None));
            var c = _throttle.AcquireDownloadAsync(CancellationToken.None);

            _clock.Fake.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(50);
            Assert.False(c.IsCompleted);

            a.Dispose();
            (await _clock.Run(c)).Dispose();
        }

        [Fact]
        public async Task a_pause_holds_back_every_kind_of_request()
        {
            _throttle.Pause(TimeSpan.FromSeconds(30));
            var start = _clock.Fake.GetUtcNow();

            using (await _clock.Run(_throttle.AcquireApiAsync(CancellationToken.None)))
            {
                Assert.True(_clock.Fake.GetUtcNow() - start >= TimeSpan.FromSeconds(30));
            }

            using (await _clock.Run(_throttle.AcquireDownloadAsync(CancellationToken.None)))
            {
                Assert.True(_clock.Fake.GetUtcNow() - start >= TimeSpan.FromSeconds(30));
            }
        }

        [Fact]
        public void a_shorter_pause_never_shortens_a_longer_one()
        {
            _throttle.Pause(TimeSpan.FromMinutes(5));
            _throttle.Pause(TimeSpan.FromSeconds(1));

            Assert.Equal(_clock.Fake.GetUtcNow() + TimeSpan.FromMinutes(5), _throttle.PausedUntil);
        }

        [Fact]
        public async Task waiting_can_be_cancelled()
        {
            using var held = await _throttle.AcquireApiAsync(CancellationToken.None);
            using var cancel = new CancellationTokenSource();
            var waiting = _throttle.AcquireApiAsync(cancel.Token);

            cancel.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _clock.Run(waiting));
        }

        [Fact]
        public async Task leases_release_once_even_if_disposed_twice()
        {
            var lease = await _clock.Run(_throttle.AcquireDownloadAsync(CancellationToken.None));
            lease.Dispose();
            lease.Dispose();

            using var a = await _clock.Run(_throttle.AcquireDownloadAsync(CancellationToken.None));
            using var b = await _clock.Run(_throttle.AcquireDownloadAsync(CancellationToken.None));
            var c = _throttle.AcquireDownloadAsync(CancellationToken.None);
            _clock.Fake.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(50);

            // A double dispose that freed two slots would let a third download start.
            Assert.False(c.IsCompleted);
        }
    }
}
