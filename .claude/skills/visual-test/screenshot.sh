#!/usr/bin/env bash
# Serve site/ on a free loopback port and screenshot it at phone and desktop
# widths in light and dark mode. Usage: screenshot.sh <output-dir>
# Playwright is fetched by npx at a pinned version; nothing is added to the repo.
set -euo pipefail

out="${1:?usage: screenshot.sh <output-dir>}"
root="$(git rev-parse --show-toplevel)"
pw_version="1.61.1"
mkdir -p "$out"

# Port 0 lets the OS pick a free port, so a server another session left on a
# fixed port can never be screenshotted by mistake.
port="$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1]); s.close()')"
python3 -m http.server "$port" --bind 127.0.0.1 --directory "$root/site" >"$out/http.log" 2>&1 &
server=$!
trap 'kill "$server" 2>/dev/null || true' EXIT

url="http://127.0.0.1:${port}/"
for _ in $(seq 1 50); do
  curl -fsS -o /dev/null "$url" 2>/dev/null && break
  sleep 0.1
done
# Fail closed if the page served is not this repo's site.
if ! curl -fsS "$url" | grep -q '<title>Rogue</title>'; then
  echo "FAIL: $url did not serve site/index.html (title is not 'Rogue')" >&2
  exit 1
fi

for size in "phone:390,844" "desktop:1440,900"; do
  name="${size%%:*}"; viewport="${size#*:}"
  for scheme in light dark; do
    npx -y "playwright@${pw_version}" screenshot --browser chromium \
      --viewport-size="$viewport" --color-scheme="$scheme" --full-page \
      "$url" "$out/site-${name}-${scheme}.png"
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
