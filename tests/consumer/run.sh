#!/usr/bin/env bash
# Packs the twelve SkathIO.Rogue packages into a local feed and runs the consumer solution, which
# restores them by package reference as an app outside this repository would.
#
# Usage: tests/consumer/run.sh   (from any directory; CI runs it in the consumer job of ci.yml)
set -euo pipefail

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
out="$root/artifacts/consumer"
# Every run packs the same version; NuGet.config gives the consumer its own package folder, emptied
# here, so a restore never serves the copy of an earlier run.
version=2.0.0-consumer
# The core package at another minor version, for the ROGUE013 test.
mismatch_version=2.1.0-consumer

rm -rf "$out"

# The build output goes under artifacts/ too, so packing leaves the repository's bin/ and obj/ alone.
dotnet pack "$root/SkathIO.Rogue.sln" -c Release -o "$out/feed" --artifacts-path "$out/build" \
  -p:MinVerVersionOverride="$version"

count=$(find "$out/feed" -name '*.nupkg' | wc -l)
if [ "$count" -ne 12 ]; then
  echo "FAIL: expected 12 packages in $out/feed, found $count" >&2
  exit 1
fi

for project in SkathIO.Rogue.Abstractions SkathIO.Rogue; do
  dotnet pack "$root/src/$project/$project.csproj" -c Release -o "$out/feed-mismatch" \
    --artifacts-path "$out/build-mismatch" -p:MinVerVersionOverride="$mismatch_version"
done

dotnet test "$root/tests/consumer/SkathIO.Rogue.Consumer.sln" -c Release -p:RoguePackageVersion="$version"
