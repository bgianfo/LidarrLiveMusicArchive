using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

namespace Lidarr.Plugin.LiveMusicArchive.Core.Http
{
    /// <summary>Decides whether and when to retry a failed archive.org request.</summary>
    public static class RetryPolicy
    {
        /// <summary>Attempts per request, including the first.</summary>
        public const int MaxAttempts = 6;

        /// <summary>The first backoff delay; each retry doubles it.</summary>
        public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(1);

        /// <summary>The longest wait between attempts, and the most of a Retry-After we honour per attempt.</summary>
        public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);

        /// <summary>Whether a response status is worth retrying: rate limiting, overload and server errors.</summary>
        /// <param name="status">The HTTP status.</param>
        /// <returns>True for 408, 429 and 5xx.</returns>
        public static bool IsRetryable(HttpStatusCode status) =>
            status == HttpStatusCode.RequestTimeout || status == HttpStatusCode.TooManyRequests || (int)status >= 500;

        /// <summary>Whether a status means archive.org wants every client to slow down, not just this request.</summary>
        /// <param name="status">The HTTP status.</param>
        /// <returns>True for 429 and 503.</returns>
        public static bool IsThrottling(HttpStatusCode status) =>
            status == HttpStatusCode.TooManyRequests || status == HttpStatusCode.ServiceUnavailable;

        /// <summary>
        /// How long to wait before the next attempt: the server's Retry-After when given, otherwise exponential
        /// backoff (1 s, 2 s, 4 s …) with ±20 % jitter so many clients don't retry in lockstep. Never more than
        /// <see cref="MaxDelay"/>.
        /// </summary>
        /// <param name="attempt">The attempt that just failed, starting at 1.</param>
        /// <param name="retryAfter">The response's Retry-After header, if any.</param>
        /// <param name="now">The current time, for HTTP-date Retry-After values.</param>
        /// <param name="random">A value in [0, 1) for jitter.</param>
        /// <returns>The delay.</returns>
        public static TimeSpan Delay(int attempt, RetryConditionHeaderValue retryAfter, DateTimeOffset now, double random)
        {
            var requested = retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - now : (TimeSpan?)null);
            if (requested is { } fromServer && fromServer > TimeSpan.Zero)
            {
                return fromServer < MaxDelay ? fromServer : MaxDelay;
            }

            var exponential = BaseDelay.TotalSeconds * Math.Pow(2, Math.Max(0, attempt - 1));
            var jittered = exponential * (0.8 + (0.4 * random));
            return TimeSpan.FromSeconds(Math.Min(jittered, MaxDelay.TotalSeconds));
        }

        /// <summary>Parses a raw Retry-After value ("120" or an HTTP date).</summary>
        /// <param name="value">The header value.</param>
        /// <returns>The parsed header, or null when absent or invalid.</returns>
        public static RetryConditionHeaderValue ParseRetryAfter(string value) =>
            RetryConditionHeaderValue.TryParse(value?.Trim(), out var parsed) ? parsed
            : int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) ? new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds))
            : null;
    }
}
