#!/usr/bin/env bash
# Builds the VitePress docs site from the repo's docs/*.md, which it reads and never edits, and
# the API reference, which it generates from the /// comments with docfx and clean-api.mjs.
# Run from the repo root after `npm ci` in docs-site/. It then assembles the one GitHub Pages
# folder, artifacts/pages: the site/ files at its root (served at /rogue/) and the VitePress
# output under docs/ (served at /rogue/docs/, the API under /rogue/docs/api/). site.yml uploads
# that folder and ci.yml runs this same script on every pull request. Nothing generated is committed.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(dirname "$here")"
stage="$root/artifacts/docs-site"
pages="$root/artifacts/pages"

if [[ ! -d "$here/node_modules/vitepress" ]]; then
  echo "docs-site/node_modules is missing: run 'npm ci' in docs-site/ first." >&2
  exit 1
fi

# The docs live under the landing page's docs/ route, so site/ must not hold a docs entry of
# its own: copying over it would mix the two, so fail instead.
if [[ -e "$root/site/docs" ]]; then
  echo "site/docs exists and would collide with the docs route: rename or remove it." >&2
  exit 1
fi

# VitePress reads one source folder and resolves node modules from the sources, so the
# pages and the logo are staged together next to a link to docs-site's node_modules.
# The Pages folder of an earlier run goes first, so a build that fails leaves no stale site
# behind for site.yml or the visual test to pick up.
rm -rf "$stage" "$pages"
mkdir -p "$stage/src/public"
cp "$root"/docs/*.md "$stage/src/"
cp "$root/site/mark.svg" "$stage/src/public/mark.svg"
ln -s "$here/node_modules" "$stage/node_modules"

# docfx.json writes the API Markdown to $stage/docfx; the cleanup turns it into pages and a
# sidebar under the staged api/ folder. One solution restore up front is much faster than
# docfx restoring each of the 12 projects in turn, so docfx.json sets noRestore.
(cd "$root" && dotnet tool restore && dotnet restore SkathIO.Rogue.sln && dotnet docfx metadata docs-site/docfx.json)
node "$here/clean-api.mjs" "$stage/docfx" "$stage/src/api"

node "$here/node_modules/vitepress/bin/vitepress.js" build "$here"

# Assemble the Pages folder.
mkdir -p "$pages"
cp -R "$root/site/." "$pages/"
cp -R "$stage/dist" "$pages/docs"
