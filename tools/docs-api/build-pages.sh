#!/usr/bin/env bash
# Builds the GitHub Pages folder locally, as hashira's pages-site.yml builds it in CI, for the
# visual test: artifacts/pages holds the site/ files at its root, the site kit in _kit/ and the
# docs under docs/ (served at /rogue/docs/, the API reference under /rogue/docs/api/).
#
# The docs are built by docs-build's lib/cli.js, the entry point the docs-build action runs, from a
# hashira checkout: HASHIRA_DIR when set (a local clone), else hashira's v3 tag, cloned into
# artifacts/hashira; the commit used is printed. The pre-build is tools/docs-api/pre-build.sh, the
# docs_pre_build_command site.yml passes, so this runs what the deploy runs. Needs Node 20 or later,
# the .NET 10 SDK and, without HASHIRA_DIR, network access. Run from anywhere in the repo. Nothing
# generated is committed.
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
cli="$hashira/.github/actions/docs-build/lib/cli.js"
kit="$hashira/.github/actions/site-kit/kit"
if [[ ! -f "$cli" || ! -d "$kit" ]]; then
  echo "build-pages: $hashira holds no docs-build cli.js or site-kit action." >&2
  exit 1
fi
# v3 is a moving tag, so name the commit this build used.
commit="an unknown commit (not a git checkout)"
if [[ -e "$hashira/.git" ]]; then
  commit="$(git -C "$hashira" rev-parse HEAD)"
fi
echo "build-pages: hashira at $commit"

# The Pages folder of an earlier run goes first, so a failed build leaves no stale site behind
# for the visual test to pick up.
rm -rf "$pages"
mkdir -p "$pages/_kit"
cp -R site/. "$pages/"
cp "$kit/tokens.css" "$kit/theme.js" "$kit/site-nav.js" "$pages/_kit/"

GITHUB_REPOSITORY="skathio/rogue" node "$cli" --out "$pages/docs" --pre-build "bash tools/docs-api/pre-build.sh"
echo "build-pages: wrote $pages"
