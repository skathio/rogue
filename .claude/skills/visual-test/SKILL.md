---
name: visual-test
description: Screenshot the Rogue project site (site/) and its docs route (/rogue/docs/) at phone and desktop widths in light and dark mode, and inspect the screenshots. Use for any change to site/index.html, site/theme.js or site/mark.svg, docs-site/ or docs/, or anything else that changes what the site shows.
---

# Visual test for `site/` and the docs route

The landing page is static: `site/index.html`, `site/theme.js` and `site/mark.svg`, with no build
step. The docs route, `/rogue/docs/`, is the VitePress site that `docs-site/build.sh` builds from
`docs/*.md` and the generated API reference. That script assembles `artifacts/pages` (the `site/`
files at its root, the docs under `docs/`), and `.github/workflows/site.yml` deploys it.

## Steps

1. Build the docs, then take the screenshots (about 1 min once Playwright is cached):

   ```bash
   docs-site/build.sh
   .claude/skills/visual-test/screenshot.sh "$SCRATCH/shots"
   ```

   The docs shots show the last build, so rebuild after changing `docs/` or `docs-site/`. The
   landing shots always show `site/` as it is now.

   Pass an output folder outside the repo, such as your scratchpad. The script:
   - exits 1 if `artifacts/pages/docs` has no build;
   - serves `site/` at `/rogue/` and the built docs at `/rogue/docs/`, as GitHub Pages does, with
     `python3 -m http.server 0`, so the OS picks a free 127.0.0.1 port (never a fixed one: another
     repo's site may already be on 8000 or 8765);
   - exits 1 if a served page's `<title>` is not the expected one: `Rogue` for the landing page,
     `Getting started` for `/rogue/docs/`, and `ISender` for
     `/rogue/docs/api/skathio-rogue-isender.html`;
   - retries a capture up to 5 times, because Chromium sometimes fails with "Unable to capture
     screenshot" when other Chromium instances are loading the machine;
   - takes full-page shots with `npx -y playwright@1.61.1 screenshot`, which is fetched by npx and
     never added to the repo, at phone (390×844) and desktop (1440×900) width in light and dark:
     `site-{phone,desktop}-{light,dark}.png` for the landing page,
     `docs-getting-started-{phone,desktop}-{light,dark}.png` for Getting started and
     `docs-isender-{phone,desktop}-{light,dark}.png` for the `ISender` API page;
   - exits 2 and prints `WARN` when a shot is wider than its viewport, which means the page
     scrolls sideways.

   If Chromium is missing, run `npx -y playwright@1.61.1 install chromium` once.

2. Read all twelve PNGs. Check that:
   - the hero, stats, code blocks, comparison table and docs links render;
   - the docs pages show the nav, sidebar (or the phone "Menu" bar), outline and code blocks in
     the landing page's palette, and the `ISender` page lists its methods with generic names;
   - text stays readable in both themes (`--ink` on `--canvas`, accent colors, `.t` code tokens);
   - at 390px nothing spills sideways and the nav fits;
   - your change looks the way you intended.

3. To test the theme toggle (Auto, Light, Dark, which sets `data-theme` on `<html>`), click
   `#theme` in a browser (for example with the Playwright MCP tools) and screenshot after each click.

4. Compare with `main` when the change is meant to be visual-only: run the script on a `main`
   checkout too.

5. In the PR, attach the screenshots (or describe each one) and list any warnings.

Known baseline: on `main` the comparison table already makes the phone page 647px wide, so the
script warns for both phone shots. Report that warning as existing unless your change fixes or
worsens it.
