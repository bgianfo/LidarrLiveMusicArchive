using System.Net;
using System.Net.Http.Headers;
using Lidarr.Plugin.LiveMusicArchive.Core.Http;
using Xunit;

namespace Lidarr.Plugin.LiveMusicArchive.Tests
{
    public class RetryPolicyTests
    {
        private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

        [Theory]
        [InlineData(HttpStatusCode.TooManyRequests, true, true)]
        [InlineData(HttpStatusCode.ServiceUnavailable, true, true)]
        [InlineData(HttpStatusCode.BadGateway, true, false)]
        [InlineData(HttpStatusCode.RequestTimeout, true, false)]
        [InlineData(HttpStatusCode.NotFound, false, false)]
        [InlineData(HttpStatusCode.Forbidden, false, false)]
        public void classifies_statuses(HttpStatusCode status, bool retryable, bool throttling)
        {
            Assert.Equal(retryable, RetryPolicy.IsRetryable(status));
            Assert.Equal(throttling, RetryPolicy.IsThrottling(status));
        }

        [Fact]
        public void honours_retry_after_seconds() =>
            Assert.Equal(TimeSpan.FromSeconds(120), RetryPolicy.Delay(1, new RetryConditionHeaderValue(TimeSpan.FromSeconds(120)), Now, 0.5));

        [Fact]
        public void honours_retry_after_dates() =>
            Assert.Equal(TimeSpan.FromSeconds(90), RetryPolicy.Delay(1, new RetryConditionHeaderValue(Now.AddSeconds(90)), Now, 0.5));

        [Fact]
        public void caps_what_the_server_asks_for() =>
            Assert.Equal(RetryPolicy.MaxDelay, RetryPolicy.Delay(1, new RetryConditionHeaderValue(TimeSpan.FromHours(3)), Now, 0.5));

        [Fact]
        public void a_retry_after_date_in_the_past_falls_back_to_backoff() =>
            Assert.Equal(TimeSpan.FromSeconds(1), RetryPolicy.Delay(1, new RetryConditionHeaderValue(Now.AddSeconds(-10)), Now, 0.5));

        [Theory]
        [InlineData(1, 1)]
        [InlineData(2, 2)]
        [InlineData(3, 4)]
        [InlineData(6, 32)]
        public void backs_off_exponentially(int attempt, double seconds) =>
            Assert.Equal(TimeSpan.FromSeconds(seconds), RetryPolicy.Delay(attempt, null, Now, 0.5));

        [Fact]
        public void jitter_stays_within_twenty_percent()
        {
            Assert.Equal(TimeSpan.FromSeconds(3.2), RetryPolicy.Delay(3, null, Now, 0));
            Assert.True(RetryPolicy.Delay(3, null, Now, 0.9999) < TimeSpan.FromSeconds(4.8));
        }

        [Fact]
        public void backoff_never_exceeds_the_maximum() =>
            Assert.Equal(RetryPolicy.MaxDelay, RetryPolicy.Delay(30, null, Now, 0.99));

        [Theory]
        [InlineData("120", 120)]
        [InlineData(" 5 ", 5)]
        public void parses_retry_after_seconds(string value, int seconds) =>
            Assert.Equal(TimeSpan.FromSeconds(seconds), RetryPolicy.ParseRetryAfter(value).Delta);

        [Fact]
        public void parses_retry_after_http_dates() =>
            Assert.Equal(new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero), RetryPolicy.ParseRetryAfter("Sun, 01 Nov 2026 08:00:00 GMT").Date);

        [Theory]
        [InlineData(null)]
        [InlineData("soon")]
        public void ignores_unparseable_retry_after(string value) =>
            Assert.Null(RetryPolicy.ParseRetryAfter(value));
    }
}
