#!/usr/bin/env bash
# Convenience wrapper for the checks CI runs; each step is a plain dotnet command that also works on its own from
# the repo root. The first build downloads Lidarr's assemblies (see Directory.Build.targets).
#
# Usage: ./build.sh [--fix-format]
#   --fix-format  rewrite files to satisfy dotnet format instead of failing on differences
#
# Steps: Release build + plugin zip in dist/, dotnet format check, tests.
set -euo pipefail
cd "$(dirname "$0")"

FORMAT="--verify-no-changes"
for arg in "$@"; do
  case "$arg" in
    --fix-format) FORMAT="" ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

dotnet build -c Release --nologo -v q -p:PackagePlugin=true
dotnet format $FORMAT --severity warn --no-restore

# The test host occasionally aborts on stale output after a package change; retry once from clean, never on test failures.
log=$(mktemp)
trap 'rm -f "$log"' EXIT
run_tests() { dotnet test -c Release --no-build --nologo -v q 2>&1 | tee "$log"; }
run_tests || true
if grep -q "Test Run Aborted" "$log"; then
  echo "test host aborted; retrying from clean test output"
  rm -rf tests/Lidarr.Plugin.LiveMusicArchive.Tests/bin tests/Lidarr.Plugin.LiveMusicArchive.Tests/obj
  dotnet build -c Release --nologo -v q
  run_tests || true
fi
grep -q "^Passed!" "$log"
