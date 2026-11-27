#!/usr/bin/env bash
# Serve site/, the site kit and the built docs on a free loopback port, laid out as GitHub Pages
# serves them (/rogue/, /rogue/_kit/ and /rogue/docs/), and screenshot the landing page, the Getting
# started docs page and the ISender API page at phone (390x844) and desktop (1280x800) size in light
# and dark mode, the sizes the shared <site-nav> header is compared at across the skathio sites, and
# at tablet (1024x768) size, a width at which the docs header once ran off the page.
# Usage: screenshot.sh <output-dir>
# The docs and the kit come from the last tools/docs-api/build-pages.sh run (artifacts/pages/docs and
# artifacts/pages/_kit); run it first.
# Playwright is fetched by npx at a pinned version; nothing is added to the repo.
set -euo pipefail

out="${1:?usage: screenshot.sh <output-dir>}"
root="$(git rev-parse --show-toplevel)"
pw_version="1.61.1"
mkdir -p "$out"

docs="$root/artifacts/pages/docs"
kit="$root/artifacts/pages/_kit"
if [ ! -f "$docs/index.html" ] || [ ! -f "$docs/api/skathio-rogue-isender.html" ]; then
  echo "FAIL: no built docs in $docs: run tools/docs-api/build-pages.sh first" >&2
  exit 1
fi
# Without the kit the landing has no tokens and no <site-nav>, so its shots would mislead.
for f in tokens.css theme.js site-nav.js; do
  if [ ! -f "$kit/$f" ]; then
    echo "FAIL: no site kit file $kit/$f: run tools/docs-api/build-pages.sh first" >&2
    exit 1
  fi
done

# The landing page is served from site/ as it is now, the kit and the docs from the last build. All
# sit under /rogue/ because the docs' asset links are absolute to that base, and the landing loads
# the kit by the relative path _kit/.
serve="$out/serve"
rm -rf "$serve"
mkdir -p "$serve/rogue"
for f in "$root"/site/*; do
  ln -s "$f" "$serve/rogue/$(basename "$f")"
done
ln -s "$kit" "$serve/rogue/_kit"
ln -s "$docs" "$serve/rogue/docs"

# The server binds port 0 itself, so the OS gives it a free port with no
# window for another process to take it, and a server another session left on
# a fixed port can never be screenshotted by mistake. The port is read back
# from the server's own startup line.
python3 -u -m http.server 0 --bind 127.0.0.1 --directory "$serve" >"$out/http.log" 2>&1 &
server=$!
trap 'kill "$server" 2>/dev/null || true' EXIT

# The wait ends as soon as the port is printed; the long ceiling is for a machine busy with
# builds, where the server can take several seconds to start.
port=""
for _ in $(seq 1 300); do
  port="$(sed -n 's/^Serving HTTP on .* port \([0-9][0-9]*\) .*/\1/p' "$out/http.log")"
  [ -n "$port" ] && break
  sleep 0.1
done
if [ -z "$port" ]; then
  echo "FAIL: http.server did not start; see $out/http.log" >&2
  exit 1
fi
base="http://127.0.0.1:${port}/rogue/"

# Each page: shot name prefix, URL, and a title the served page must have, so a wrong page
# fails closed instead of being screenshotted.
pages=(
  "site|${base}|<title>Rogue</title>"
  "docs-getting-started|${base}docs/|<title>Getting started[ |<]"
  "docs-isender|${base}docs/api/skathio-rogue-isender.html|<title>[^<]*ISender[ |<]"
)
for page in "${pages[@]}"; do
  IFS='|' read -r prefix url title <<<"$page"
  # Read the whole page before matching: grep -q stopping early would fail curl under pipefail.
  if ! html="$(curl -fsS "$url")" || ! grep -q "$title" <<<"$html"; then
    echo "FAIL: $url did not serve the expected page (title does not match '$title')" >&2
    exit 1
  fi
done

for page in "${pages[@]}"; do
  IFS='|' read -r prefix url _ <<<"$page"
  for size in "phone:390,844" "tablet:1024,768" "desktop:1280,800"; do
    name="${size%%:*}"; viewport="${size#*:}"
    for scheme in light dark; do
      # Chromium's capture intermittently fails with "Unable to capture
      # screenshot" when other Chromium instances load the machine; retry with a
      # backoff, and fail if every attempt fails.
      for attempt in 1 2 3 4 5; do
        if npx -y "playwright@${pw_version}" screenshot --browser chromium \
            --viewport-size="$viewport" --color-scheme="$scheme" --full-page \
            --wait-for-timeout=500 "$url" "$out/${prefix}-${name}-${scheme}.png"; then
          break
        fi
        if [ "$attempt" = 5 ]; then
          echo "FAIL: could not capture ${prefix}-${name}-${scheme}.png" >&2
          exit 1
        fi
        sleep "$attempt"
      done
    done
  done
done
echo "Screenshots in $out:"
ls -1 "$out"/*.png

# A full-page shot wider than its viewport means the page scrolls sideways.
status=0
for page in "${pages[@]}"; do
  prefix="${page%%|*}"
  for size in "phone:390" "tablet:1024" "desktop:1280"; do
    name="${size%%:*}"; want="${size#*:}"
    for scheme in light dark; do
      png="$out/${prefix}-${name}-${scheme}.png"
      got="$(python3 -c 'import struct,sys; f=open(sys.argv[1],"rb"); f.seek(16); print(struct.unpack(">I", f.read(4))[0])' "$png")"
      if [ "$got" != "$want" ]; then
        echo "WARN: $png is ${got}px wide for a ${want}px viewport: horizontal overflow" >&2
        status=2
      fi
    done
  done
done
exit "$status"
