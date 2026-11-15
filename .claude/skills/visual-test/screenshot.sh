#!/usr/bin/env bash
# Serve site/ on a free loopback port and screenshot it at phone and desktop
# widths in light and dark mode. Usage: screenshot.sh <output-dir>
# Playwright is fetched by npx at a pinned version; nothing is added to the repo.
set -euo pipefail

out="${1:?usage: screenshot.sh <output-dir>}"
root="$(git rev-parse --show-toplevel)"
pw_version="1.61.1"
mkdir -p "$out"

# The server binds port 0 itself, so the OS gives it a free port with no
# window for another process to take it, and a server another session left on
# a fixed port can never be screenshotted by mistake. The port is read back
# from the server's own startup line.
python3 -u -m http.server 0 --bind 127.0.0.1 --directory "$root/site" >"$out/http.log" 2>&1 &
server=$!
trap 'kill "$server" 2>/dev/null || true' EXIT

port=""
for _ in $(seq 1 50); do
  port="$(sed -n 's/^Serving HTTP on .* port \([0-9][0-9]*\) .*/\1/p' "$out/http.log")"
  [ -n "$port" ] && break
  sleep 0.1
done
if [ -z "$port" ]; then
  echo "FAIL: http.server did not start; see $out/http.log" >&2
  exit 1
fi
url="http://127.0.0.1:${port}/"
# Fail closed if the page served is not this repo's site.
if ! curl -fsS "$url" | grep -q '<title>Rogue</title>'; then
  echo "FAIL: $url did not serve site/index.html (title is not 'Rogue')" >&2
  exit 1
fi

for size in "phone:390,844" "desktop:1440,900"; do
  name="${size%%:*}"; viewport="${size#*:}"
  for scheme in light dark; do
    # Chromium's capture intermittently fails with "Unable to capture
    # screenshot" when other Chromium instances load the machine; retry with a
    # backoff, and fail if every attempt fails.
    for attempt in 1 2 3 4 5; do
      if npx -y "playwright@${pw_version}" screenshot --browser chromium \
          --viewport-size="$viewport" --color-scheme="$scheme" --full-page \
          --wait-for-timeout=500 "$url" "$out/site-${name}-${scheme}.png"; then
        break
      fi
      if [ "$attempt" = 5 ]; then
        echo "FAIL: could not capture site-${name}-${scheme}.png" >&2
        exit 1
      fi
      sleep "$attempt"
    done
  done
done
echo "Screenshots in $out:"
ls -1 "$out"/site-*.png

# A full-page shot wider than its viewport means the page scrolls sideways.
status=0
for size in "phone:390" "desktop:1440"; do
  name="${size%%:*}"; want="${size#*:}"
  for scheme in light dark; do
    png="$out/site-${name}-${scheme}.png"
    got="$(python3 -c 'import struct,sys; f=open(sys.argv[1],"rb"); f.seek(16); print(struct.unpack(">I", f.read(4))[0])' "$png")"
    if [ "$got" != "$want" ]; then
      echo "WARN: $png is ${got}px wide for a ${want}px viewport: horizontal overflow" >&2
      status=2
    fi
  done
done
exit "$status"
