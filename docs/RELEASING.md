# Building and releasing

## Building

Plain dotnet commands work from the repo root (`Lidarr.Plugin.LiveMusicArchive.sln`):

```sh
dotnet build                                # first run downloads Lidarr's assemblies into .refs/
dotnet test
dotnet format --verify-no-changes --severity warn
dotnet build -c Release -p:PackagePlugin=true   # also writes dist/LidarrLiveMusicArchive-v<version>.net8.0.zip
```

`./build.sh` runs all of these the way CI does (`--fix-format` rewrites instead of failing).

The plugin compiles against Lidarr's regular release, which loads plugins since 3.1. The version is pinned by
`LidarrVersion` and `LidarrPackageSha256` in `Directory.Build.props`. On the first build `Directory.Build.targets`
downloads that release from GitHub, checks its hash and unpacks its DLLs into `.refs/<version>/`. The pinned
version is also the release's minimum Lidarr version. To move to a newer Lidarr, update both properties; the hash
is the sha256 of the `windows-core-x64.zip` asset.

`global.json` asks for SDK 8 but rolls forward to any newer SDK, and the tests roll forward to a newer runtime
when .NET 8 isn't installed. CI uses SDK 8 and runs the tests on the .NET 8 runtime that Lidarr uses.

## CI

`.github/workflows/build.yml` runs on every push to `main`/`master`, on every pull request, and on demand. It
builds and packages the plugin, checks formatting and runs the tests. The zip is uploaded as the `plugin` workflow
artifact, so any run can be installed by hand.

The tests also collect line coverage of the plugin assembly (`coverage.runsettings`) and upload it to GitHub code
quality with `actions/upload-code-coverage`. Pushes to `main` set the baseline that pull requests are compared
against. To see the same numbers locally:

```sh
dotnet test --collect:"Code Coverage" --settings coverage.runsettings --results-directory coverage
```

## Cutting a release

The version comes from git tags via [MinVer](https://github.com/adamralph/minver); there is no version in the
csproj. Untagged commits build as the next patch prerelease, e.g. `0.4.1-alpha.0.3` (three commits after
`v0.4.0`), so CI artifacts are never mistaken for a release.

To release, tag a commit that is already on `main` and push the tag:

```sh
git tag v0.4.0 && git push origin v0.4.0
```

The `release` job then:

- fails if the built version is not exactly the tag. That happens when the tag isn't SemVer, e.g. `v0.4`;
- creates a GitHub release that targets the branch containing the tag, and attaches
  `LidarrLiveMusicArchive-v<version>.net8.0.zip`;
- writes release notes with install instructions, a `Minimum Lidarr Version: a.b.c.d` line, and GitHub's
  generated changelog.

## How Lidarr picks a release

These rules come from Lidarr's `PluginService` on the `plugins` branch, and the workflow is shaped by them:

- The asset name must contain `net8.0.zip`. It is unpacked into `plugins/<owner>/<repo>/`.
- A plain repository URL only matches releases whose target branch is `main` or `master`. Releases from another
  branch are offered only to users who install `https://github.com/<owner>/<repo>/tree/<branch>`.
- Lidarr ignores GitHub's prerelease flag. A `0.4.0-beta.1` tagged on `main` reaches everyone, so tag betas
  on a separate branch.
- Releases whose minimum Lidarr version is newer than the running Lidarr are skipped.
- The installed version is read from the assembly's informational version. The csproj turns off the
  `+<commit>` suffix so that this version compares equal to the tag. (MinVer pins `AssemblyVersion` to
  `Major.0.0.0`, which Lidarr would otherwise fall back to.)
