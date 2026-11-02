using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.LiveMusicArchive.EndToEnd
{
    /// <summary>
    /// Local stand-ins for archive.org (scrape, metadata and download APIs) and for Lidarr's metadata server, on
    /// one loopback port. Lidarr is pointed at them through its Metadata Source setting and the plugin's
    /// LIDARR_LMA_ARCHIVE_URL, so no test request leaves the machine.
    /// </summary>
    public sealed class FakeServices : IAsyncDisposable
    {
        private static readonly Regex CollectionQuery = new(@"collection:(\S+) AND mediatype:etree", RegexOptions.Compiled);
        private static readonly Regex CreatorQuery = new("creator:\"([^\"]+)\"", RegexOptions.Compiled);

        private readonly WebApplication _app;

        private FakeServices(WebApplication app, FakeCatalog catalog)
        {
            _app = app;
            Catalog = catalog;
        }

        public FakeCatalog Catalog { get; }

        public string BaseUrl { get; private set; }

        /// <summary>Gets or sets a value indicating whether the fake archive.org answers every request with 503.</summary>
        public bool ArchiveOrgDown { get; set; }

        /// <summary>Gets every request path seen, for assertions about what the plugin asked for.</summary>
        public ConcurrentQueue<string> Requests { get; } = new();

        public static async Task<FakeServices> StartAsync(FakeCatalog catalog)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var services = new FakeServices(app, catalog);
            services.Map(app);
            await app.StartAsync();
            services.BaseUrl = app.Urls.First().TrimEnd('/');
            return services;
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();

        private void Map(WebApplication app)
        {
            app.Use(async (context, next) =>
            {
                Requests.Enqueue(context.Request.Path + context.Request.QueryString);
                var archive = !context.Request.Path.StartsWithSegments("/api");
                if (archive && ArchiveOrgDown)
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return;
                }

                await next();
            });

            // Lidarr's metadata server (SkyHook), only what Lidarr asks for here.
            app.MapGet("/api/v0.4/artist/{mbid}", (string mbid) =>
                Catalog.Artists.FirstOrDefault(a => a.Mbid == mbid) is { } artist ? Results.Json(Catalog.ArtistJson(artist)) : Results.NotFound());
            app.MapGet("/api/v0.4/album/{id}", (string id) => Results.NotFound());
            app.MapGet("/api/v0.4/recent/{kind}", () => Results.Json(new { Since = 0, Items = Array.Empty<string>(), Limited = false }));
            app.MapGet("/api/v0.4/search", () => Results.Json(Array.Empty<object>()));

            // archive.org.
            app.MapGet("/services/search/v1/scrape", (HttpRequest request) => Scrape(request.Query["q"].ToString(), request.Query["fields"].ToString()));
            app.MapGet("/metadata/{identifier}", (string identifier) =>
                Catalog.Items.FirstOrDefault(i => i.Identifier == identifier) is { } item ? Results.Json(Catalog.MetadataJson(item)) : Results.Json(new JsonObject()));
            app.MapGet("/download/{identifier}/{**path}", (string identifier, string path, HttpContext context) => Download(identifier, path, context));
        }

        private IResult Scrape(string query, string fields)
        {
            var wanted = fields.Split(',', StringSplitOptions.RemoveEmptyEntries);
            IEnumerable<FakeItem> items = Catalog.Items;
            if (CollectionQuery.Match(query) is { Success: true } c)
            {
                items = items.Where(i => i.Collection == c.Groups[1].Value);
            }
            else if (CreatorQuery.Match(query) is { Success: true } a)
            {
                items = items.Where(i => string.Equals(i.Creator, a.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
            }

            var rows = items.Select(i => (JsonNode)Catalog.ScrapeRow(i, wanted)).ToArray();
            return Results.Json(new JsonObject { ["items"] = new JsonArray(rows), ["count"] = rows.Length, ["total"] = rows.Length });
        }

        // Serves a file like archive.org's data nodes: ETag, and Range honoured when If-Range matches.
        private IResult Download(string identifier, string path, HttpContext context)
        {
            var file = Catalog.Items.FirstOrDefault(i => i.Identifier == identifier)?.Files.FirstOrDefault(f => f.Name == path);
            if (file == null)
            {
                return Results.NotFound();
            }

            if (file.Private)
            {
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            var etag = FakeCatalog.ETag(file.Content);
            context.Response.Headers.ETag = etag;
            var range = context.Request.GetTypedHeaders().Range;
            var ifRange = context.Request.Headers.IfRange.ToString();
            if (range?.Ranges.Count == 1 && (string.IsNullOrEmpty(ifRange) || ifRange == etag))
            {
                var from = range.Ranges.First().From ?? 0;
                if (from >= file.Content.Length)
                {
                    return Results.StatusCode((int)HttpStatusCode.RequestedRangeNotSatisfiable);
                }

                context.Response.Headers.ContentRange = new ContentRangeHeaderValue(from, file.Content.Length - 1, file.Content.Length).ToString();
                return new PartialContent(file.Content[(int)from..]);
            }

            return Results.Bytes(file.Content, "application/octet-stream");
        }


        // Results.Bytes always answers 200; a resumed download needs 206 Partial Content.
        private sealed class PartialContent : IResult
        {
            private readonly byte[] _content;

            public PartialContent(byte[] content)
            {
                _content = content;
            }

            public async Task ExecuteAsync(HttpContext httpContext)
            {
                httpContext.Response.StatusCode = StatusCodes.Status206PartialContent;
                httpContext.Response.ContentType = "application/octet-stream";
                httpContext.Response.ContentLength = _content.Length;
                await httpContext.Response.Body.WriteAsync(_content);
            }
        }
    }
}
