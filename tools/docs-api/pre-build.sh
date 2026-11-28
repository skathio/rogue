#!/usr/bin/env bash
# The docs' pre-build: writes the API reference into the staged docs ($DOCS_STAGE) before
# VitePress runs. site.yml, ci.yml and publish.yml pass this script as hashira's
# docs_pre_build_command (pre_build_command for docs-build), and tools/docs-api/build-pages.sh
# passes it to docs-build's cli.js --pre-build, so every build runs the same steps. Run from the
# repo root, with DOCS_STAGE set.
#
# One solution restore up front is much faster than docfx restoring each of the 12 projects
# (docfx.json sets noRestore). docfx writes into a new temporary folder (docfx.json's `dest` under
# --output), so no page of a removed type survives from an earlier run and two runs never share a
# folder. clean-api.mjs turns docfx's Markdown into pages under api/ (served at /rogue/docs/api/)
# and writes the grouped sidebar to $DOCS_STAGE/.sidebar.json.
set -euo pipefail
: "${DOCS_STAGE:?pre-build: DOCS_STAGE is not set; docs-build sets it to the staged docs folder.}"

dotnet tool restore
dotnet restore SkathIO.Rogue.sln
docfx_out="$(mktemp -d)"
trap 'rm -rf "$docfx_out"' EXIT
dotnet docfx metadata tools/docs-api/docfx.json --output "$docfx_out"
node tools/docs-api/clean-api.mjs "$docfx_out/docfx" "$DOCS_STAGE/api" --sidebar "$DOCS_STAGE/.sidebar.json" .github/pages.json
