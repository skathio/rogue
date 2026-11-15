---
name: visual-test
description: Screenshot the Rogue project site (site/) at phone and desktop widths in light and dark mode, and inspect the screenshots. Use for any change to site/index.html, site/theme.js or site/mark.svg, or anything else that changes what the site shows.
---

# Visual test for `site/`

The site is static: `site/index.html`, `site/theme.js` and `site/mark.svg`, with no build step.
GitHub Pages serves the `site/` folder as it is (`.github/workflows/site.yml`).

## Steps

1. Take the screenshots (about 20 s once Playwright is cached):

   ```bash
   .claude/skills/visual-test/screenshot.sh "$SCRATCH/shots"
   ```

   Pass an output folder outside the repo, such as your scratchpad. The script:
   - serves `site/` with `python3 -m http.server 0`, so the OS picks a free 127.0.0.1 port (never
     a fixed one: another repo's site may already be on 8000 or 8765);
   - exits 1 if the served page's `<title>` is not `Rogue`;
   - retries a capture up to 5 times, because Chromium sometimes fails with "Unable to capture
     screenshot" when other Chromium instances are loading the machine;
   - takes full-page shots with `npx -y playwright@1.61.1 screenshot`, which is fetched by npx and
     never added to the repo: `site-phone-light.png`, `site-phone-dark.png` (390×844) and
     `site-desktop-light.png`, `site-desktop-dark.png` (1440×900);
   - exits 2 and prints `WARN` when a shot is wider than its viewport, which means the page
     scrolls sideways.

   If Chromium is missing, run `npx -y playwright@1.61.1 install chromium` once.

2. Read all four PNGs. Check that:
   - the hero, stats, code blocks, comparison table and docs links render;
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
