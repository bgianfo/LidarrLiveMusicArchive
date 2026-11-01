# LidarrLiveMusicArchive: Architecture

Status: verified against Lidarr 3.1.6.5079 (master), which includes the plugin system.
Audience: contributors and reviewers.

## 1. Purpose

The plugin makes recordings from the Internet Archive Live Music Archive (LMA, the `etree` collection on
archive.org) available in Lidarr. It does three things:

1. Adds an artist's concerts to Lidarr as albums, alongside the artist's MusicBrainz releases.
2. Finds downloadable recordings of a concert when Lidarr searches for that album.
3. Downloads, verifies and tags the files so Lidarr can import them without manual intervention.

The plugin, `LidarrLiveMusicArchive`, ships as one assembly, `Lidarr.Plugin.LiveMusicArchive.dll`,
installed through Lidarr's plugin mechanism. It needs no external service.

## 2. Constraints

The design follows from these properties of Lidarr and archive.org.

| Constraint | Consequence |
|---|---|
| Lidarr's data model is Artist → Album → Release → Track. Albums come only from the metadata provider. Plugins cannot add entity types. | Concerts must be expressed as albums served through the metadata path. |
| MusicBrainz lists few concerts (134 of roughly 2,000 Grateful Dead shows). | The plugin creates album entries for shows MusicBrainz lacks. |
| Lidarr resolves exactly one `IProvideArtistInfo` and `IProvideAlbumInfo`: the one registered last. Plugin load order follows directory enumeration. | Replacing the metadata provider conflicts with other plugins that do the same, such as Tubifarry. |
| Every registered `IHttpRequestInterceptor` runs on every request made through Lidarr's `IHttpClient`, before the HTTP error check. | Metadata can be extended by editing responses, which works alongside other plugins. |
| Lidarr's HTTP dispatcher throws on a custom `User-Agent` and rejects `Range` requests. | archive.org traffic uses the plugin's own HTTP client. |
| archive.org requires a descriptive User-Agent, delays for bulk work, limited concurrency, and honouring `429`/`Retry-After` (archive.org/developers/bots.html). No numeric limits are published. | All archive.org traffic passes through one throttle with conservative limits. |
| archive.org's metadata API sends no `ETag` or `Last-Modified`, and the scrape API sends `Cache-Control: no-cache`. File downloads send both. | Change detection uses the scrape API's `oai_updatedate` field. Downloads use `If-Range`. |
| Lidarr cannot import Shorten (`.shn`), a common lossless format on etree. | Shorten is excluded. |

## 3. Domain mapping

| Lidarr | Live Music Archive | Id |
|---|---|---|
| Artist | MusicBrainz artist, unchanged | MusicBrainz id |
| Album | One show: an artist on a date | `lma-{artistMbid}-{yyyyMMdd}` |
| Release | One archive.org item (one taper's recording of the show) | `lma-{identifier}` |
| Track | One song of the item | `lma-` + UUIDv5 of `track/{identifier}/{originalFile}` |
| Recording | Same song | `lma-` + UUIDv5 of `recording/{identifier}/{originalFile}` |

Shows the plugin creates have primary type `Broadcast`, secondary type `Live` and status `Bootleg`. A
metadata profile can therefore include or exclude them independently of MusicBrainz's own live albums,
which are typed `Album` + `Live`.

Album ids encode the artist and date, so a show can be rebuilt from its id alone. Release and track ids
are deterministic, so refreshes update rows in place rather than duplicating them. Every id carries the `lma-` prefix, so neither Lidarr nor tools reading tags can mistake one for a
MusicBrainz id. Track ids derive from the original file each song was made from, so they are the same
whichever format the track list is built from. Ids issued by earlier versions (bare UUIDs from the
original file or from the format's file name) are listed as `OldIds`, so Lidarr migrates existing
tracks. Verified: files stay linked across the change.

A show is added for every date archive.org has recordings for, including dates for which MusicBrainz
lists a bootleg. A MusicBrainz bootleg describes one specific bootleg CD, with its own track split,
media and country. An archive.org tape of the same concert rarely matches it closely enough for
Lidarr to import (1977-05-07, Boston Garden scored 65.6 % against an 80 % threshold). The metadata
profile decides whether MusicBrainz's bootlegs are shown as well; when they are, such dates appear
twice.

Album titles have the form `{date}: {venue}, {location}`. The venue and location are the values most
recordings of the show agree on.

## 4. Structure

```
Lidarr.Plugin.LiveMusicArchive.dll
│
├── Core/                     No dependency on any Lidarr extension point.
│   ├── Http/                 archive.org transport
│   │   ├── ArchiveOrgHttp        HttpClient, User-Agent, proxy, retries, resumable downloads
│   │   ├── ArchiveOrgThrottle    global rate and concurrency limits, shared pause
│   │   ├── RetryPolicy           retry classification and backoff calculation
│   │   └── ArchiveOrgClock       TimeProvider wrapper (lets tests substitute time)
│   ├── ArchiveOrgClient      scrape / metadata / download endpoints, change-aware disk cache
│   ├── LiveMusicArchiveService   orchestration used by the adapters
│   ├── ShowCatalog           grouping recordings into shows, year filter, show titles
│   ├── ShowMapper            show → AlbumResource, item → ReleaseResource
│   ├── AudioFiles            format selection, track order, merged titles and lengths
│   ├── ReleaseOffers         item → indexer results (HTTP and torrent)
│   ├── ShowArchive           saved copies of served shows
│   ├── Artwork               cover selection from uploaded artwork
│   ├── SyntheticIds          id formats
│   ├── Recording             scrape rows, archive.org JSON helpers
│   └── Downloads/
│       ├── DownloadManifest      per-download state file
│       ├── ArchiveOrgDownloader  background download, checksum verification
│       └── TrackTagger           tags written before import
│
├── Metadata/                 Lidarr adapters: metadata
│   ├── MetadataInterceptor       IHttpRequestInterceptor
│   ├── LiveMusicArchiveMetadata  settings host (Settings → Metadata)
│   ├── LiveMusicArchiveSettings
│   └── LargeCatalogueCheck       health check
│
├── Indexers/                 Lidarr adapters: search
│   ├── LiveMusicArchiveIndexerBase
│   ├── ArchiveOrgIndexer         per-format HTTP results
│   ├── ArchiveOrgTorrentIndexer  archive.org torrents (optional)
│   ├── ArchiveOrgIndexerSettings
│   └── ArchiveOrgDownloadProtocol + DelayProfileRegistration
│
├── Download/                 Lidarr adapters: download client
│   ├── ArchiveOrgDownloadClient
│   └── ArchiveOrgDownloadSettings
│
└── LiveMusicArchivePlugin    plugin registration (Name must equal the GitHub repository name)
```

Core uses Lidarr's SkyHook resource types (`AlbumResource`, `ReleaseResource`, …) and `ReleaseInfo` as
its output contract. These are the shapes Lidarr consumes. Core does not reference any hook interface,
so the delivery mechanism can change without touching the mapping.

Classes whose names Lidarr stores in its database keep their original names: `ArchiveOrgIndexer`,
`ArchiveOrgTorrentIndexer`, `ArchiveOrgDownloadClient`, their settings classes, `ArchiveOrgDownloadProtocol`
and `LiveMusicArchiveMetadata`/`LiveMusicArchiveSettings`. Lidarr records provider implementations and
protocols by type name, so renaming them would orphan configured indexers, download clients and delay
profiles. On-disk names (`archiveorg-cache`, `archiveorg-shows`, `.archiveorg-download.json`) are kept for
the same reason.

All classes are registered by Lidarr's container, which registers every class in a plugin assembly. No
explicit wiring code exists. The interceptor takes its dependencies as `Lazy<T>`, because Lidarr's
`HttpClient` depends on every interceptor and eager dependencies would form a cycle.

## 5. Data flows

### 5.1 Metadata: adding shows to an artist

```
Lidarr                        MetadataInterceptor              LiveMusicArchiveService / ArchiveOrgClient
  │ GET {metadata}/artist/{mbid}    │                                   │
  │────────────────────────────────▶│ (response from api.lidarr.audio)  │
  │                                 │ settings enabled? host matches?   │
  │                                 │ AddShows(artist JSON) ───────────▶│ collection = MusicBrainz archive.org
  │                                 │                                   │   link, else creator search
  │                                 │                                   │ scrape collection (cached 24 h)
  │                                 │                                   │ group by date, apply year filter,
  │                                 │                                   │ drop dates MusicBrainz has
  │                                 │◀── album summaries appended ──────│
  │◀── modified response ───────────│                                   │
  │ filter by metadata profile
  │ for each album: GET {metadata}/album/{id}
  │────────────────────────────────▶│ upstream answered 404 for lma-…   │
  │                                 │ fetch upstream artist (marked so  │
  │                                 │ it is not re-augmented)           │
  │                                 │ BuildShow(artist, date) ─────────▶│ top N recordings by downloads
  │                                 │                                   │ item metadata (cached, change-aware)
  │                                 │                                   │ map to releases, order them
  │◀── 200 with AlbumResource ──────│◀──────────────────────────────────│
```

The interceptor acts only on responses from `api.lidarr.audio` or from the host configured in Lidarr's
`MetadataSource` setting, and only on the `artist/{mbid}` and `album/lma-…` routes. All other traffic
passes through unchanged.

If processing fails, the interceptor answers `503`. Passing the upstream response through would be
harmful: without the plugin's shows the artist appears to have lost them, and `lma-` album ids return
404 upstream, and Lidarr deletes albums in both cases (section 5.2). On a 503 Lidarr logs the failure,
changes nothing, and retries on the next refresh.

Because the plugin edits responses instead of replacing the provider, it works with Lidarr's
`SkyHookProxy` and with Tubifarry's metadata wrapper. Both were verified. One exception: if Tubifarry's
MetaMix serves an artist from a source whose responses are not in SkyHook format (Discogs, Deezer), no
shows are added for that artist.

`PostResponse` is synchronous. archive.org calls inside it block the refresh thread, which is one
reason caching matters (section 6.4).

### 5.2 Keeping what archive.org removes

Lidarr deletes a catalogue entry when the metadata server stops describing it:

- an album, when its fetch returns 404 or it has no releases, unless it has files or was added manually;
- a release, together with its tracks, when it disappears from its album. Any downloaded files for those
  tracks stay on disk but are no longer linked to the album.

archive.org does remove recordings. In 2005, for example, the Grateful Dead soundboards were withdrawn
and made stream-only, and single tapes are taken down. `ShowArchive` therefore stores every show as
served, under `{AppData}/archiveorg-shows/{artistMbid}/{albumId}.json`, and the service uses it as
follows:

| Situation | Result |
|---|---|
| Show listed on archive.org | Rebuilt from archive.org. Every release in the saved copy that the new build lacks is added back. Releases whose recording is no longer listed are marked `removed from archive.org`; releases that only fell out of the top N are kept unmarked. |
| Show no longer listed, saved copy exists | Saved copy served, disambiguated `Live Music Archive · removed from archive.org`. It also stays in the artist's album list. |
| Show outside the year filter | 404. This is the only intended removal. |
| Show never saved and not listed | 404. |

Downloaded music is never deleted by any of these paths; only Lidarr's catalogue entries are affected.

### 5.3 Search

Both indexers derive from `LiveMusicArchiveIndexerBase` and handle album searches only. Artist and RSS
searches return nothing.

1. Only the plugin's own shows are searched. The show date comes from the synthetic album id.
   MusicBrainz albums are skipped because their downloads would not import (section 3).
2. The recordings offered are exactly the album's releases as stored in Lidarr (`lma-{identifier}`),
   so a grabbed result always has a matching release at import time.
3. Each recording becomes results:
   - `ArchiveOrgIndexer`: one result per available format among FLAC, 24-bit FLAC and VBR MP3, with
     protocol `ArchiveOrgDownloadProtocol`;
   - `ArchiveOrgTorrentIndexer`: one result per recording pointing at archive.org's `_archive.torrent`,
     with protocol `TorrentDownloadProtocol`.

Result titles have the form

```
{Artist} - {Album title} [SBD|AUD] [FLAC|FLAC 24bit|MP3 VBR V0] [{identifier}]
```

Lidarr takes the quality from the bracketed tag. During a search it matches the artist and album by
finding their names verbatim in the title (`ParseAlbumTitleWithSearchCriteria`). The MP3 tag is
`MP3 VBR V0` because that is the quality Lidarr assigns to imported archive.org MP3s. Matching it keeps
search results and files on disk comparable for cutoff and upgrade decisions.

`DelayProfileRegistration` adds `ArchiveOrgDownloadProtocol` to every delay profile at startup. Lidarr
rejects releases whose protocol a delay profile does not list.

### 5.4 Download and import

```
Lidarr ── Download(RemoteAlbum) ──▶ ArchiveOrgDownloadClient
                                      │ parse identifier + format from DownloadUrl
                                      │ file list for that format (AudioFiles)
                                      │ write {DownloadPath}/{title} ({id})/.archiveorg-download.json
                                      └─▶ ArchiveOrgDownloader.Start(folder)   (background task)
                                             for each file: skip if intact (MD5, else size)
                                                            download (resumable, throttled)
                                                            verify MD5; up to 3 attempts
                                             tag every file (TrackTagger)
                                             manifest.Completed = true
Lidarr ── GetItems() (polled) ────▶ one queue item per folder with a manifest
Lidarr imports the folder once the item is Completed
```

The download client keeps no state in memory beyond the running tasks. The queue is rebuilt from
the manifests on disk. A manifest found incomplete and not running, for example after a restart, is
resumed when the queue is next read.

**Tagging is required for import.** A completed download is imported with only the artist known.
Lidarr then picks candidate releases from the files' `MusicBrainz Release Id` tag, and otherwise
fuzzy-matches the album tag. Fuzzy matching cannot distinguish dozens of titles beginning with the same
year. So, after verification, `TrackTagger` writes:

| Tag | Value |
|---|---|
| MusicBrainz Release Id | `lma-{identifier}` (plugin shows only) |
| MusicBrainz Release Group Id | the album's foreign id |
| Album | Lidarr's album title, exactly |
| Album Artist | Lidarr's artist name; also Artist if the file has none |

The tags stay in the files after import. During a rescan Lidarr re-identifies any file whose size or
modification time no longer matches its database record, and the release id is what lets it find these
files again. Without it Lidarr falls back to fuzzy album matching, which fails for shows, and the files
would become unmapped.

Tagging happens only after every checksum passes, because tagging changes the file. If Lidarr stops
between tagging and saving the manifest, the next run sees checksum mismatches and downloads again.

`RemoveItem` with `deleteData = false` deletes only the manifest. The files stay and the item leaves
the queue.

## 6. archive.org access

### 6.1 Endpoints

| Use | Endpoint |
|---|---|
| List a collection's recordings | `GET /services/search/v1/scrape?q=collection:{c} AND mediatype:etree&fields=identifier,date,venue,coverage,downloads,oai_updatedate&count=10000` (cursor-paged) |
| Find an artist's collection when MusicBrainz has no link | `GET /services/search/v1/scrape?q=creator:"{name}" AND collection:etree …&fields=identifier,collection` |
| Item metadata and file list | `GET /metadata/{identifier}` |
| File download | `GET /download/{identifier}/{path}` (redirects to a data node) |
| Connection test | `GET /services/search/v1/scrape?q=collection:etree&count=100` |

### 6.2 HTTP client

`ArchiveOrgHttp` wraps a dedicated `HttpClient`:

- **User-Agent:** `Lidarr.Plugin.LiveMusicArchive/{version} (+https://github.com/bgianfo/LidarrLiveMusicArchive) Lidarr/{version}`.
- **Proxy:** taken from Lidarr's settings (`IHttpProxySettingsProvider`), covering HTTP, SOCKS4 and SOCKS5, with bypass list and credentials.
- **Timeouts:** 60 s per API request; downloads fail as stalled after 60 s with no data received.
- **Resume:** a partial file is kept as `{name}.part`, with its ETag in `{name}.part.etag`. The next
  attempt sends `Range: bytes={length}-` and `If-Range: {etag}`:
  - `206` appends to the partial file;
  - `200` means the server's copy changed, so the file restarts from the beginning;
  - `416` means the partial file was already complete.

### 6.3 Throttle and retries

`ArchiveOrgThrottle` is a singleton shared by every component.

| Limit | Value |
|---|---|
| Concurrent API requests | 1 |
| Spacing between API request starts | 1 s |
| Concurrent file downloads (across all queued downloads) | 2 |
| Spacing between download starts | 1 s |

archive.org publishes no limits. Its only example is `parallel -j4 --delay 1`, and these values are
deliberately below it.

`RetryPolicy` governs failures:

- **Retried:** 408, 429, 5xx, network errors and timeouts.
- **Not retried:** other 4xx (for example 401 for restricted files, or 404).
- **Delay:** `Retry-After` when present (seconds or HTTP date, capped at 5 min); otherwise
  1 s × 2^(attempt−1), ±20 % jitter, capped at 5 min.
- **Attempts:** up to 6 per request.
- **Shared pause:** 429 and 503 pause **all** archive.org traffic for the computed delay, not only the
  failing request.

The downloader's own 3 attempts per file cover checksum mismatches only. Network failures are retried,
and resumed, inside the HTTP client.

### 6.4 Caching and change detection

| Data | Store | Lifetime | Invalidation |
|---|---|---|---|
| Collection listing (scrape pages) | disk, `{AppData}/archiveorg-cache` | 24 h ±10 % | expiry |
| Creator search | disk | 24 h ±10 % | expiry |
| Item metadata | disk | 180 days ±15 % | the listing reports an `oai_updatedate` later than the cached copy |
| Served shows | disk, `{AppData}/archiveorg-shows` | permanent | replaced on each rebuild |
| Shows grouped by date | memory (Lidarr `ICached`) | 1 h | expiry |

The metadata API offers no validators, so the collection listing serves as the change feed. Each row's
`oai_updatedate` lists the times archive.org changed the item, and the latest of them agrees with the
item's `item_last_updated`. Item metadata is refetched only when that time is newer than the cached
file's write time. The long lifetime is a safety net for changes the listing misses.

When a request fails after its retries, any cached copy is returned regardless of age, and a warning
is logged. Only data never fetched before is unavailable during an outage.

Responses are also checked for plausibility before they are used or cached. On 2026-11-01 the scrape API
cached responses by field list and ignored the query, so queries returned other queries' results, and at
times nothing at all, with status 200. The checks:

| Request | Rejected when |
|---|---|
| Collection listing | rows are present but none belongs to the requested collection; or it is empty while the cached copy has rows |
| Creator search | it finds no item whose creator is the artist, while the cached copy did |
| Item metadata | it describes a different item, or none |

A rejected response is never cached. The cached copy is used if there is one; otherwise the request
counts as failed (item metadata) or as finding nothing (listings, creator search). Each scrape query also
asks for its fields in an order derived from the query text, which keeps different queries apart in a
cache keyed that way. The order is fixed per query, so the plugin's own cache keys are stable.

The creator search compares names after removing case, accents, punctuation and a leading "The", and
searches for the name without "The" and with straight apostrophes. archive.org lists "Disco Biscuits"
where MusicBrainz has "The Disco Biscuits", and a phrase search alone also matches tribute acts
("Chum, A Tribute to Phish").

Jitter is derived from a hash of the URL. It is stable per URL and spread across URLs, so a catalogue
cached in one refresh does not expire on a single day.

Request costs for refreshing one show with the default of 5 recordings (`RefreshCostTests`):

| Situation | archive.org requests |
|---|---|
| First refresh | 1 listing + 5 items, at least 5 s due to spacing |
| Later refresh, nothing changed | 1 listing |
| Later refresh, one recording changed | 1 listing + 1 item |
| Repeat refresh within the in-memory hour | 0 |

The first refresh of a large catalogue without a year filter costs up to `shows × recordings per show`
item requests. For the Grateful Dead that is about 2,000 × 5. At 1 request per second this takes hours,
and the throttle enforces that pace. The plugin logs a warning and `LargeCatalogueCheck` raises a
health warning when an artist has more than 300 shows and no year filter is set.

## 7. Format selection

`AudioFiles` considers only files not flagged `"private": "true"`. Restricted originals in stream-only
collections are flagged this way and return 401.

| Order | Format | Offered by the HTTP indexer | Notes |
|---|---|---|---|
| 1 | Flac | yes | |
| 2 | 24bit Flac | yes | |
| 3 | VBR MP3 | yes | archive.org derivative, about 180 kbps on average |
| 4 | Ogg Vorbis | no | Lidarr assigns Unknown quality to `.ogg` |
| 5 | 64Kbps MP3 | no | |
| n/a | Shorten | no | Lidarr cannot import it |

A release's track list is built from the best available format. For each file, title and length are
merged across every copy of the same song: the original plus the derivatives that name it in their
`original` field. Length is taken from the VBR MP3 copy first, because archive.org's lengths for
Shorten originals are wrong (`d1t01.shn` reports 44.58 s; its MP3 reports 01:51). Track order comes
from `d{n}t{nn}` / `s{n}t{nn}` patterns in file names, falling back to the `track` field.

Release ordering (lossless first, then soundboard, then download count) is advisory. Lidarr selects
the monitored release by its own rules.

Soundboard/audience classification (`SBD`/`AUD`) is a pattern match on the identifier and source
notes. It is shown in release disambiguation and result titles, and is not used for matching.

## 8. Cover art

archive.org has no cover art service for these recordings. Some tapers upload artwork with a
recording, as original JPEG, PNG or GIF files. A 1977 Grateful Dead sample had artwork on 5 of 40
recordings. `Artwork` picks the album cover from the show's recordings, most downloaded first:

1. an image whose name contains `front`, `cover`, `folder` or `poster`, excluding backs and thumbnails;
2. otherwise any other original image that is not a back, inlay, tray, spectrogram or thumbnail.

Spectrogram PNGs and archive.org's generated `__ia_thumb.jpg` are never used. The collection image
(`/services/img/{collection}`) is a 180×180 logo and is not used either. Selection costs no requests,
because the file lists are already fetched. The chosen URL is published as the album's `Cover` image,
and Lidarr downloads it. Shows without artwork get Lidarr's placeholder. If artwork disappears from
archive.org, the saved show's image is reused.

## 9. Configuration

| Location | Setting | Default |
|---|---|---|
| Settings → Metadata → Live Music Archive | Enable | off |
| | Years (`1977`, `1972-1974`, or empty for all) | empty |
| | Max Recordings per Show (releases per show) | 5 |
| Settings → Download Clients → Live Music Archive | Download Path | none (required) |

Lidarr creates one disabled metadata entry per metadata consumer. If more than one enabled entry
exists, which can only happen through the API, the newest wins and a warning is logged.

Users must also:

- enable a metadata profile with primary type `Broadcast`, secondary type `Live` and status
  `Bootleg` for the artists concerned;
- set a Download Path that is not inside a Lidarr root folder.

## 10. State and persistence

| State | Location | Owner |
|---|---|---|
| Shows, releases, tracks, files | Lidarr database | Lidarr (via the metadata path) |
| archive.org response cache | `{AppData}/archiveorg-cache/*.json` | `ArchiveOrgClient` |
| Shows as served | `{AppData}/archiveorg-shows/{artist}/{album}.json` | `ShowArchive` |
| Download state | `{DownloadPath}/{folder}/.archiveorg-download.json` | `ArchiveOrgDownloadClient` / `ArchiveOrgDownloader` |
| Partial downloads | `{file}.part`, `{file}.part.etag` | `ArchiveOrgHttp` |
| Settings | Lidarr database (metadata, indexer and download client definitions) | Lidarr |

Deleting the cache folder is safe: entries are refetched on demand. Deleting `archiveorg-shows` loses
the ability to keep shows and releases that archive.org has since removed. Manifests are written atomically
(temporary file, then rename).

## 11. Failure behaviour

| Failure | Behaviour |
|---|---|
| archive.org unreachable during metadata refresh | Cached data is served regardless of age. With nothing cached, the interceptor answers 503; Lidarr logs the failure and deletes nothing. |
| Show or tape removed from archive.org | Kept from the saved copy and marked as removed (section 5.2). |
| 429 / 503 | Global pause for `Retry-After` or backoff, then retry, up to 6 attempts. |
| Restricted file (401) | Not retried. The download fails with the HTTP status in the manifest error. |
| Corrupt file (MD5 mismatch) | File deleted and refetched, up to 3 times, then the download is marked failed. |
| Lidarr restart during a download | Partial files kept. The download resumes when the queue is next read. |
| File cannot be tagged | Warning logged. Download completes; import may need manual matching. |
| Plugin settings disabled | Interceptor passes all traffic through. Indexer and download client still work for albums that already exist. |

## 12. Interaction with other plugins

- **Metadata:** compatible with any plugin that replaces the metadata provider, provided it fetches
  through Lidarr's `IHttpClient` and receives SkyHook-format responses. Verified with Tubifarry 2.1.0.
- **HTTP dispatcher:** the plugin does not use Lidarr's dispatcher for archive.org. It is therefore
  unaffected by plugins that replace it, as Tubifarry's `FlexibleHttpDispatcher` does.
- **Download protocol:** the plugin defines its own protocol, so its results go only to its own
  download client. Torrent results use Lidarr's standard torrent protocol and any configured torrent
  client.

## 13. Alternatives considered

| Option | Outcome |
|---|---|
| Separate metadata proxy service, configured through `MetadataSource` | Worked (spike 1). Rejected: requires a second deployable component. |
| Replace `IProvideArtistInfo` / `IProvideAlbumInfo` in the plugin | Worked alone. Rejected: the last-loaded plugin wins, so it conflicts with Tubifarry depending on folder order (spike 2). |
| Write albums directly to Lidarr's database after refresh | Rejected: refresh deletes albums absent from metadata, or logs errors for each one on every refresh. |
| Import shows into MusicBrainz | Rejected: requires bulk bot edits of over 100,000 bootleg releases. |
| Use Lidarr's `IHttpClient` for archive.org | Used until v0.2. Replaced: it cannot send the required User-Agent and cannot resume downloads. |

## 14. Build and test

`dotnet build`, `dotnet test` and `dotnet format` work from the repository root. The first build
downloads the Lidarr release named by `LidarrVersion` in `Directory.Build.props` into `.refs/`; its
assemblies are referenced, but not shipped. `./build.sh` runs the same steps CI runs: Release build
with the plugin zip in `dist/`, format check, then tests. Release packaging is described in
`docs/RELEASING.md`.

The plugin project requires XML documentation on every member and treats warnings as errors.

Tests use xUnit and NSubstitute. Network access is replaced by `FakeHandler`, time by
`FakeTimeProvider` through `IArchiveOrgClock`. Fixtures in `tests/…/Fixtures` are recorded archive.org
responses, plus a truncated MP3 for tag tests. Quality and title parsing are tested against Lidarr's
own `Parser` and `QualityParser`.

To install manually, copy `Lidarr.Plugin.LiveMusicArchive.dll` and its `.deps.json` to
`{AppData}/plugins/bgianfo/LidarrLiveMusicArchive/` and restart Lidarr. Use the GitHub repository's owner and name:
Lidarr's installer unpacks updates there, and a copy anywhere else would stay loaded alongside them.

## 15. Known limitations

- **Hook stability:** `IHttpRequestInterceptor` has no implementation in Lidarr itself and could be
  removed. The mapping in Core is independent of it, so a replacement adapter would be small.
- **SkyHook dependency:** the plugin depends on the SkyHook JSON format and on Lidarr's internal
  classes, as all Lidarr plugins do.
- **MusicBrainz bootlegs are not served:** the plugin offers recordings only for its own shows (section 3).
- **No RSS:** the indexers do not support RSS, so Lidarr grabs only when a search runs (on adding an
  album, or a manual or "search monitored" search), not as new recordings appear on archive.org.
- **Synthetic ids in tags:** files keep `lma-` values in their MusicBrainz release, release group and,
  if Lidarr writes tags, track id fields. Lidarr needs them to re-identify the files (section 5.4).
  Tools that look up MusicBrainz ids will find no match.
- **Shorten:** no conversion. Recordings whose only lossless copy is Shorten are offered as MP3.
- **Torrents:** a torrent contains every format of a recording, typically 3 to 5 times the size of one
  format. Lidarr's size limits often reject them, and imports may include several formats.
- **Throttle limits** are fixed and not user-configurable. They have not been confirmed with archive.org.
- **First refresh of large catalogues** is slow by design (section 6.4).
