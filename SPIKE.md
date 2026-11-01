
# Plugin v0.2: indexer + download client, end to end (2026-10-31)

Verified in Lidarr 3.1.2 (plugins branch): Cornell 1977-05-08 → interactive search (12 results, quality
parsed) → grab → HTTP download with MD5 verification → tagging → **8 tracks imported**. Searches were also
verified against stock Lidarr with Tubifarry removed. `./build.sh` checks `dotnet format`, runs 141 tests
and builds. Every member has XML docs; CS1591 and other warnings fail the build.

Format priority: FLAC > 24-bit FLAC > VBR MP3, with one search result per format so the quality profile
decides. Shorten (.shn) is skipped because Lidarr can't import it (not in MediaFileExtensions). Ogg and
64 kbps MP3 are skipped as low quality. Track lengths come from the MP3 derivative first, because
archive.org's .shn lengths are wrong (44.58 s vs 01:51).

Lessons:
- **Import identification**: a completed download is imported knowing only its artist. Lidarr then
  uses the files' `MusicBrainz Release Id` tag, and otherwise fuzzy-matches the album tag, which fails
  with dozens of "1977-…" titles. The downloader therefore tags `lma-{identifier}` plus the exact album
  title after the checksums pass.
- **User-Agent**: Lidarr's ManagedHttpDispatcher *throws* if a request sets User-Agent (it worked
  only because Tubifarry replaces the dispatcher). Lidarr's own UA already identifies it, and the plugin
  adds `X-Lidarr-Plugin: Lidarr.Plugin.ArchiveOrg/0.2 (+url)`.
- The MP3 quality tag is "MP3 VBR V0" because that's what Lidarr detects from the imported files. A
  mismatch makes search results and files on disk disagree.
- Artists without an archive.org link in MusicBrainz (e.g. Dispatch) fall back to a `creator:` search.
- Lidarr creates one disabled settings entry per metadata consumer; duplicates made through the API
  are resolved newest-first, with a warning.
- Test-environment trap: the download folder must not be inside a Lidarr root folder.

Open: no year filter means ~2,000 shows and ~20k archive.org requests on the first refresh of the
Grateful Dead; Shorten → FLAC conversion; MusicBrainz shows have no synthetic release id to tag, so they
depend on fuzzy matching (untested end to end).

# Plugin v0.3: being a good archive.org citizen (2026-11-01)

archive.org's only rules (archive.org/developers/bots.html): a descriptive User-Agent naming the tool and
version, delays for bulk work, limited concurrency, honour 429 + Retry-After, cache, back off. No numbers.

- **Own HTTP client** (`Core/Http/ArchiveOrgHttp`): UA `Lidarr.Plugin.ArchiveOrg/0.3.0 (+url) Lidarr/x`.
  Lidarr's IHttpClient can't send it (its dispatcher throws) or do Range requests. Lidarr's proxy
  settings are applied by hand.
- **One global throttle** (`ArchiveOrgThrottle`): 1 API request at a time, ≥1 s apart; ≤2 files at
  a time, starts ≥1 s apart. 429/503 pause *all* traffic.
- **Backoff** (`RetryPolicy`): Retry-After (seconds or date, capped at 5 min), else 1, 2, 4 … s ±20 %,
  6 attempts. 4xx is not retried.
- **Change-aware cache**: the metadata API has no ETag/Last-Modified and scrape is `no-cache`, but
  scrape's `oai_updatedate` agrees with an item's `item_last_updated`. Items are refetched only when
  changed, with a 180-day ±15 % safety refresh; listings use 24 h ±10 %. A refresh of an unchanged
  show costs one listing request, or none within the day (`RefreshCostTests`).
- **Downloads resume** with `Range` + `If-Range: <ETag>` (files have ETags; If-None-Match → 304
  verified). There's a 60 s stall timeout.
- **Bounds**: default 5 recordings per show; warning plus health check when >300 shows and no year filter.
- **Torrent indexer** (optional): archive.org's `_archive.torrent`. It holds *every* format, so sizes
  are 3–5× a single format and Lidarr's size limits often reject it (e.g. 1.2 GB vs an 808 MB MP3
  cap). Restricted (stream_only) items have no torrent (403).

Live: an uncached search made 9 requests with no errors; a 228 MB download in 97 s imported 22 tracks.
197 tests.
