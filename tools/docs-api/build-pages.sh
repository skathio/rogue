#!/usr/bin/env bash
# Builds the GitHub Pages folder locally, as hashira's pages-site.yml builds it in CI, for the
# visual test: artifacts/pages holds the site/ files at its root, the site kit in _kit/ and the
# docs under docs/ (served at /rogue/docs/, the API reference under /rogue/docs/api/).
#
# The docs are built by hashira's docs-build action, run step by step from a hashira checkout:
# HASHIRA_DIR when set (a local clone), else hashira's v3 tag, cloned into artifacts/hashira. The
# pre-build command is read from site.yml's docs_pre_build_command, so this runs exactly what the
# deploy runs. Needs Node 20 or later, the .NET 10 SDK and, without HASHIRA_DIR, network access.
# Run from anywhere in the repo. Nothing generated is committed.
set -euo pipefail

root="$(git rev-parse --show-toplevel)"
cd "$root"
pages="artifacts/pages"

hashira="${HASHIRA_DIR:-}"
if [[ -z "$hashira" ]]; then
  hashira="$root/artifacts/hashira"
  rm -rf "$hashira"
  git clone --quiet --depth 1 --branch v3 https://github.com/skathio/hashira.git "$hashira"
fi
action="$hashira/.github/actions/docs-build"
kit="$hashira/.github/actions/site-kit/kit"
if [[ ! -f "$action/lib/prepare.js" || ! -d "$kit" ]]; then
  echo "build-pages: $hashira holds no docs-build or site-kit action." >&2
  exit 1
fi

# site.yml's docs_pre_build_command: the lines indented under that key.
pre_build="$(awk '
  /^ *docs_pre_build_command: *\| *$/ { match($0, /^ */); depth = RLENGTH; on = 1; next }
  on { match($0, /^ */); if ($0 !~ /^ *$/ && RLENGTH <= depth) exit; print }
' .github/workflows/site.yml)"
if [[ -z "$pre_build" ]]; then
  echo "build-pages: no docs_pre_build_command block in .github/workflows/site.yml." >&2
  exit 1
fi

# The Pages folder of an earlier run goes first, so a failed build leaves no stale site behind
# for the visual test to pick up.
rm -rf "$pages" artifacts/docs-build
mkdir -p "$pages/_kit" artifacts/docs-build
cp -R site/. "$pages/"
cp "$kit/tokens.css" "$kit/theme.js" "$kit/site-nav.js" "$pages/_kit/"

outputs="artifacts/docs-build/outputs"
RUNNER_TEMP="$root/artifacts/docs-build" GITHUB_OUTPUT="$outputs" GITHUB_REPOSITORY="skathio/rogue" \
  DB_OUTPUT_DIR="$pages/docs" node "$action/lib/prepare.js"
work="$(sed -n 's/^work=//p' "$outputs")"
stage="$(sed -n 's/^stage=//p' "$outputs")"

DOCS_STAGE="$stage" bash -eo pipefail -c "$pre_build"
(cd "$work/vitepress" && npm ci --ignore-scripts --include=dev --no-audit --no-fund)
node "$work/vitepress/node_modules/vitepress/bin/vitepress.js" build "$work/vitepress"
cp -R "$work/dist" "$pages/docs"
echo "build-pages: wrote $pages"
