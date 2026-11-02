# LidarrLiveMusicArchive

A [Lidarr](https://lidarr.audio) plugin for the [Internet Archive Live Music Archive](https://archive.org/details/etree):
concert recordings from bands that allow taping, such as the Grateful Dead, moe. and Yonder Mountain String Band.

The plugin adds each concert to Lidarr as an album, finds the recordings of it, downloads them from
archive.org and tags them so Lidarr imports them like any other album.

- **Shows as albums.** Each concert becomes an album named after its date and venue, for example
  `1977-05-08: Barton Hall - Cornell University, Ithaca, NY`. Each taper's recording is a release, and
  each song is a track.
- **Search and download.** An indexer finds a show's recordings. A download client fetches the format
  your quality profile prefers (FLAC or MP3) and checks every file against archive.org's checksums.
- **Cover art**, where a taper uploaded artwork with the recording.
- **Nothing disappears.** Shows and recordings that archive.org later removes stay in Lidarr, marked as
  removed.

## Requirements

- Lidarr **3.1.6.5079 or newer**. Plugin support is part of regular Lidarr releases.
- Artists whose recordings are on the Live Music Archive. The plugin finds an artist's collection from its
  MusicBrainz link to archive.org, or otherwise by searching archive.org for the artist's name.

## Install

1. In Lidarr, open **System → Plugins**.
2. Enter `https://github.com/bgianfo/LidarrLiveMusicArchive` and click **Install**.
3. Restart Lidarr.

Lidarr offers updates on the same page when a new release is published.

To install by hand, download `LidarrLiveMusicArchive-v<version>.net8.0.zip` from the
[releases page](https://github.com/bgianfo/LidarrLiveMusicArchive/releases). Unzip it into
`<Lidarr data folder>/plugins/bgianfo/LidarrLiveMusicArchive/` and restart Lidarr. Use exactly that folder:
Lidarr installs updates there, and a copy anywhere else would stay loaded alongside them.

## Configure

Four settings are involved. The first three are required.

### 1. Turn on show listings

**Settings → Metadata → Live Music Archive (archive.org)**: tick **Enable**, then set:

| Setting | Meaning | Default |
|---|---|---|
| Years | Only add shows from these years: a single year (`1977`) or a range (`1972-1974`). Leave empty for every year. | empty |
| Max Recordings per Show | How many recordings of each show (the most downloaded first) become releases you can choose between. | 5 |

**Set a year range for artists with large catalogues.** The Grateful Dead have about 2,000 archived shows.
The first refresh fetches details for every recording it lists, and the plugin deliberately limits itself
to one archive.org request per second, so a full catalogue takes hours. Later refreshes are quick, because
the plugin refetches only recordings that archive.org reports as changed. Lidarr shows a warning under
**System → Status** when an artist has more than 300 shows and no year range is set.

### 2. Let artists show the concerts

Lidarr only adds albums an artist's **metadata profile** allows. Shows from this plugin have:

- primary type **Broadcast**
- secondary type **Live**
- release status **Bootleg**

Under **Settings → Profiles → Metadata Profiles**, either add those three to an existing profile, or create
a profile for concerts. Then give the artists that profile, in each artist's **Edit** dialog or when adding
them.

> MusicBrainz also lists some concerts, as type **Album** with status **Bootleg**. Each one describes a
> particular bootleg CD, which archive.org's recordings rarely match closely enough for Lidarr to import,
> so the plugin doesn't search for those albums. If your profile allows **Album** + **Bootleg**, such dates
> appear twice; use the one from this plugin.

### 3. Add the indexer and the download client

- **Settings → Indexers → Add → Live Music Archive.** No settings are needed. Keep **Interactive Search** and
  **Automatic Search** enabled.
- **Settings → Download Clients → Add → Live Music Archive.** Set **Download Path** to the folder downloads
  are written to. It must **not** be inside one of Lidarr's root folders; Lidarr moves the files into your
  library after importing them.

The plugin adds its download protocol to your delay profiles automatically on startup.

### 4. Optional: quality and tags

- **Quality profile.** Recordings are offered as **FLAC**, **FLAC 24bit**, **MP3-VBR-V0** and, for uploads
  that only have `.m4a` files, **AAC** or **ALAC**, so allow the ones you want. To have FLAC replace an MP3 automatically when one appears, enable **Upgrades Allowed**
  with **FLAC** as the cutoff.
- **Tags.** With **Settings → Metadata → Tag Audio Files with Metadata** set to anything other than
  *Never*, Lidarr writes the show's title, date, disc and track numbers and cover art into the files, which
  players use.
  Files keep a few `lma-…` values in their MusicBrainz ID tags either way. Lidarr needs these to recognise
  the files during later rescans. Tools that look up MusicBrainz IDs, such as Picard or beets, will find no
  match for them.

### Torrents (optional)

**Live Music Archive (Torrent)** is a second indexer. It returns archive.org's own torrent for each recording
and sends it to your torrent client. A torrent contains every format of a recording (FLAC, MP3, Ogg and
others), so it is several times larger than the single format the regular indexer downloads, and Lidarr's
size limits often reject it. For most setups the regular indexer is the better choice.

## Use

1. Add an artist, or edit an existing one, so it uses a metadata profile that allows the concerts (step 2).
2. After Lidarr refreshes the artist, the concerts in the selected years appear as albums.
3. Search for a show with **Search** or **Interactive Search** on its album page. To fetch several, monitor
   them and use **Search Monitored**. Lidarr picks a recording that fits your quality profile, the download
   client fetches it, and Lidarr imports it.

There is no RSS feed. Lidarr fetches a show when it searches for it, not automatically as new recordings
appear on archive.org.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| No concerts appear for an artist | The metadata profile doesn't allow Broadcast + Live + Bootleg; the year range excludes them; the plugin isn't enabled under Settings → Metadata; or the artist has no collection on the Live Music Archive. |
| A show has no releases | archive.org couldn't be reached during the refresh. The plugin keeps what it had and retries on the next refresh. Refresh the artist to retry now. |
| "Download path is not writable" | The Lidarr user can't write to the Download Path. |
| Downloads import twice, or files vanish from the download folder | The Download Path is inside a Lidarr root folder. |
| Releases rejected with "Existing files meets cutoff" | Lidarr already has files for that show, and your quality profile doesn't allow upgrades. |
| A recording only offers MP3 or AAC | Its lossless files are restricted on archive.org (stream-only), or they are Shorten (`.shn`), which Lidarr can't import. |
| A show is missing although archive.org has it | None of its recordings has a format Lidarr can import (for example only Shorten). It reappears when archive.org adds another recording. |

Lidarr's log (**System → Logs**) records the plugin's activity under `LiveMusicArchiveService`,
`MetadataInterceptor`, `ArchiveOrgClient`, `ArchiveOrgHttp`, `ArchiveOrgDownloader` and `TrackTagger`.

## How it treats archive.org

The plugin follows archive.org's
[guidelines for automated access](https://archive.org/developers/bots.html):

- It identifies itself as `Lidarr.Plugin.LiveMusicArchive/<version>`.
- It makes at most one API request per second and downloads at most two files at a time.
- It backs off when archive.org asks it to slow down.
- It caches results, and refetches a recording's details only when archive.org reports a change.

## More

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): how the plugin works, its design decisions and its
  limitations.
- [docs/RELEASING.md](docs/RELEASING.md): building, testing and publishing releases.

## License

MIT; see [LICENSE](LICENSE). This project is not affiliated with the Internet Archive or with Lidarr.
