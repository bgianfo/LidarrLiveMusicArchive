using System.Net;
using Lidarr.Plugin.LiveMusicArchive.Core.Http;
using Microsoft.Extensions.Time.Testing;

namespace Lidarr.Plugin.LiveMusicArchive.Tests
{
    /// <summary>A clock tests move by hand.</summary>
    internal sealed class FakeClock : IArchiveOrgClock
    {
        public FakeTimeProvider Fake { get; } = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));

        public TimeProvider Time => Fake;

        /// <summary>Advances fake time in small steps until the task finishes, so throttle and backoff waits elapse.</summary>
        public async Task<T> Run<T>(Task<T> task, TimeSpan? step = null)
        {
            await Run((Task)task, step);
            return await task;
        }

        /// <summary>Advances fake time in small steps until the task finishes.</summary>
        public async Task Run(Task task, TimeSpan? step = null)
        {
            for (var i = 0; !task.IsCompleted && i < 100_000; i++)
            {
                Fake.Advance(step ?? TimeSpan.FromMilliseconds(250));
                await Task.Delay(1);
            }

            await task;
        }
    }

    /// <summary>An HTTP handler answering from a script, recording every request.</summary>
    internal sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public List<HttpRequestMessage> Requests { get; } = new();

        public static HttpResponseMessage Text(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body) };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_respond(request));
        }
    }
}
