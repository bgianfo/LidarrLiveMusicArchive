# Testing

There are two test projects.

| Project | What it covers | When it runs |
|---|---|---|
| `tests/Lidarr.Plugin.LiveMusicArchive.Tests` | Unit tests: mapping, caching, throttling, downloads, tagging. Lidarr's own parser and tag reader are used where the plugin depends on them. | Always (`dotnet test`) |
| `tests/Lidarr.Plugin.LiveMusicArchive.EndToEnd` | The plugin inside a real Lidarr process. | Only with `LMA_E2E=1` |

## End-to-end tests

The fixture:

- downloads Lidarr's Linux build once into `.e2e/`, by default the version in `Directory.Build.props`;
- installs the freshly built plugin into a throwaway data folder, and starts Lidarr on a free loopback port;
- configures Lidarr through its API the way the README tells users to: metadata profile, plugin settings,
  indexer and download client.

archive.org and Lidarr's metadata server are replaced by local fakes in `FakeServices`, on one loopback port:

- Lidarr is pointed at the fake metadata server through its Metadata Source setting.
- The plugin is pointed at the fake archive.org through the `LIDARR_LMA_ARCHIVE_URL` environment variable,
  which only the tests set.
- Apart from the Lidarr download, no test request leaves the machine.

The fake catalogue (`FakeCatalog`) reproduces layouts that broke imports on real instances:

- FLAC originals without titles, in archive.org's disc sub-folders;
- an upload with only `.m4a` files;
- a show whose only audio is Shorten, which must not appear at all.

The audio is the unit tests' tiny tone fixtures.

The scenarios check:

- every settings screen has a fields list (Lidarr 3.2 crashes otherwise);
- shows appear with their releases;
- each recording is searched, grabbed, downloaded and imported in full;
- an archive.org outage during a refresh deletes nothing.

Run them with:

```sh
LMA_E2E=1 dotnet test tests/Lidarr.Plugin.LiveMusicArchive.EndToEnd -c Release
LMA_E2E=1 LMA_E2E_LIDARR=3.2.0.5144:develop dotnet test tests/Lidarr.Plugin.LiveMusicArchive.EndToEnd -c Release
```

`LMA_E2E_LIDARR` takes a release as `version` or `version:branch`, matching the asset name
`Lidarr.<branch>.<version>.linux-core-x64.tar.gz`. The branch defaults to `master`. A run takes a few
minutes, mostly Lidarr starting and polling its download queue.

CI (`.github/workflows/end-to-end.yml`) runs them on every pull request, on pushes to `main` and weekly, against
the pinned Lidarr and the newest develop build.

### Adding a scenario

Add the recording to `FakeCatalog`: an item, its files and their formats, using the tone fixtures for audio.
Then add a test in `ImportTests` that drives Lidarr through `EndToEndEnvironment`. If a scenario needs
archive.org to misbehave, add a switch to `FakeServices` (as `ArchiveOrgDown` does) and reset it in a
`finally` block, since the scenarios share one Lidarr.
