#!/usr/bin/env bash
# Publishes the two native AOT samples against the packed packages in the local feed and runs both
# binaries; a publish warning or a non-zero exit code fails the run. run.sh packs the feed first and
# then calls this script with the version it packed.
#
# Native AOT needs clang and zlib1g-dev. CI installs them, and fails here if clang is missing; on a
# developer machine without clang the AOT scenarios are skipped, as the container tests are without
# Docker.
#
# Usage: tests/consumer/aot.sh <package version>
set -euo pipefail

if [ "$#" -ne 1 ]; then
  echo "usage: $0 <package version>" >&2
  exit 2
fi
version=$1

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
out="$root/artifacts/consumer/aot"
consumer="$root/tests/consumer"

if ! command -v clang >/dev/null 2>&1; then
  if [ "${CI:-}" = "true" ] || [ "${GITHUB_ACTIONS:-}" = "true" ]; then
    echo "FAIL: clang is missing; native AOT needs clang and zlib1g-dev, and CI never skips these scenarios" >&2
    exit 1
  fi
  echo "SKIP: clang is missing, so the native AOT scenarios do not run here (install clang and zlib1g-dev)" >&2
  exit 0
fi

case "$(uname -m)" in
  x86_64) rid=linux-x64 ;;
  aarch64 | arm64) rid=linux-arm64 ;;
  *)
    echo "FAIL: no native AOT runtime identifier for machine $(uname -m)" >&2
    exit 1
    ;;
esac

rm -rf "$out"

dotnet publish "$consumer/SkathIO.Rogue.Consumer.Aot/SkathIO.Rogue.Consumer.Aot.csproj" -c Release -r "$rid" \
  -o "$out/SkathIO.Rogue.Aot.Sample" -p:RoguePackageVersion="$version"
dotnet publish "$consumer/SkathIO.Rogue.Consumer.Jobs.Messaging.Aot/SkathIO.Rogue.Consumer.Jobs.Messaging.Aot.csproj" -c Release -r "$rid" \
  -o "$out/SkathIO.Rogue.Jobs.Messaging.Aot.Sample" -p:RoguePackageVersion="$version"

# Each binary checks its own scenario and exits non-zero when it fails; set -e stops the run then.
"$out/SkathIO.Rogue.Aot.Sample/SkathIO.Rogue.Aot.Sample"
"$out/SkathIO.Rogue.Jobs.Messaging.Aot.Sample/SkathIO.Rogue.Jobs.Messaging.Aot.Sample"
echo "OK: both native AOT samples ran from the packed packages"
