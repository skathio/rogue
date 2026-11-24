// Rules for the landing page's links into the docs and for the visual test of the docs route:
// the landing page's "Docs" section and nav link point to /rogue/docs/ pages instead of GitHub,
// while the NuGet and Code links stay; and the visual-test screenshot script also shoots
// Getting started and the ISender API page at 390 px and 1440 px, in light and dark, with the
// same width warning as the landing shots.
//
// The link cases read site/index.html, and check each docs link against the Pages folder that
// docs-site/build.sh assembles in a copy of the working tree. The screenshot cases run
// .claude/skills/visual-test/screenshot.sh in that copy with a stand-in `npx` first on PATH:
// it records each Playwright screenshot call, fetches the page from the script's own server,
// and writes a PNG header of the width the case asks for, so no browser is needed.
import { test, describe, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import {
  chmodSync, cpSync, existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, rmSync,
  symlinkSync, writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const docsSite = dirname(fileURLToPath(import.meta.url));
const root = join(docsSite, '..');
const LANDING = 'site/index.html';
const SITE_URL = 'https://skathio.github.io/rogue/';
const DOCS_BASE = '/rogue/docs/';
const SCREENSHOT = '.claude/skills/visual-test/screenshot.sh';
const SKILL = '.claude/skills/visual-test/SKILL.md';
const STAYS = ['NuGet', 'Code'];
const WIDTHS = [390, 1440];
const SCHEMES = ['light', 'dark'];

function git(cwd, ...args) { return spawnSync('git', ['-C', cwd, ...args], { encoding: 'utf8' }); }

function landingAtHead() {
  const shown = git(root, 'show', `HEAD:${LANDING}`);
  assert.equal(shown.status, 0, `cannot read ${LANDING} at HEAD: ${shown.stderr}`);
  return shown.stdout;
}

// ---------------------------------------------------------------- landing page links

function anchors(html) {
  return [...html.matchAll(/<a\b([^>]*)>([\s\S]*?)<\/a>/g)].map((m) => ({
    href: m[1].match(/\bhref=(?:"([^"]*)"|'([^']*)'|([^\s>]+))/)?.slice(1).find((v) => v !== undefined) ?? null,
    text: m[2].replace(/<[^>]*>/g, '').trim(),
  }));
}

function docsSection(html) {
  const m = html.match(/<section\b[^>]*\bid=["']?docs["']?[^>]*>([\s\S]*?)<\/section>/);
  assert.ok(m, `${LANDING} has no "Docs" section (id="docs")`);
  return anchors(m[1]);
}

function navDocsLink(html) {
  const nav = html.match(/<nav\b[^>]*>([\s\S]*?)<\/nav>/);
  assert.ok(nav, `${LANDING} has no <nav>`);
  const link = anchors(nav[1]).find((a) => a.text === 'Docs');
  assert.ok(link, 'the nav has no "Docs" link');
  return link;
}

// The path a link opens on the published site, where the landing page is /rogue/.
const publishedPath = (href) => new URL(href, SITE_URL);

const now = readFileSync(join(root, LANDING), 'utf8');
const then = landingAtHead();
const guideTexts = docsSection(then).filter((a) => !STAYS.includes(a.text)).map((a) => a.text);
const guideLinks = () => docsSection(now).filter((a) => !STAYS.includes(a.text));

describe('landing page Docs links', () => {
  test('the Docs section had guide links at HEAD to check against', () => {
    assert.ok(guideTexts.length > 0);
  });

  test('every guide link in the Docs section is still there', () => {
    const texts = guideLinks().map((a) => a.text);
    for (const text of guideTexts) { assert.ok(texts.includes(text), `the Docs section lost "${text}"`); }
  });

  test('every guide link in the Docs section opens a /rogue/docs/ page, not GitHub', () => {
    for (const { text, href } of guideLinks()) {
      assert.ok(href, `"${text}" has no href`);
      const url = publishedPath(href);
      assert.equal(url.origin, new URL(SITE_URL).origin, `"${text}" leaves the site: ${href}`);
      assert.ok(url.pathname.startsWith(DOCS_BASE), `"${text}" does not open a ${DOCS_BASE} page: ${href}`);
    }
  });

  test('the NuGet and Code links stay as they were', () => {
    for (const text of STAYS) {
      const before = docsSection(then).find((a) => a.text === text);
      const after = docsSection(now).find((a) => a.text === text);
      assert.ok(after, `the Docs section lost "${text}"`);
      assert.equal(after.href, before.href, `"${text}" changed its target`);
    }
  });

  test('the nav "Docs" link opens a /rogue/docs/ page', () => {
    const { href } = navDocsLink(now);
    assert.ok(href, 'the nav "Docs" link has no href');
    const url = publishedPath(href);
    assert.equal(url.origin, new URL(SITE_URL).origin, `the nav "Docs" link leaves the site: ${href}`);
    assert.ok(url.pathname.startsWith(DOCS_BASE), `the nav "Docs" link does not open a ${DOCS_BASE} page: ${href}`);
  });
});

// ---------------------------------------------------------------- the built pages

function copyWorkingTree() {
  const tmp = mkdtempSync(join(tmpdir(), 'rogue-landing-docs-'));
  const files = git(root, 'ls-files', '-co', '--exclude-standard', '-z').stdout.split('\0').filter(Boolean);
  for (const f of files) {
    const from = join(root, f);
    if (!existsSync(from) || lstatSync(from).isDirectory()) { continue; }
    mkdirSync(dirname(join(tmp, f)), { recursive: true });
    cpSync(from, join(tmp, f));
  }
  symlinkSync(join(docsSite, 'node_modules'), join(tmp, 'docs-site', 'node_modules'));
  // screenshot.sh finds the repo root with git, so the copy is a repository of its own.
  git(tmp, 'init', '-q');
  return tmp;
}

// The built file GitHub Pages serves for a /rogue/... path, or null.
function servedFile(pages, pathname) {
  const rel = decodeURIComponent(pathname.slice('/rogue/'.length));
  const candidates = rel === '' || rel.endsWith('/') ? [`${rel}index.html`] : [rel, `${rel}.html`, `${rel}/index.html`];
  return candidates.map((c) => join(pages, c)).find((p) => existsSync(p) && !lstatSync(p).isDirectory()) ?? null;
}

// A stand-in for `npx`: for a Playwright screenshot call it logs the call, fetches the page and
// its first stylesheet link (any rel listing `stylesheet`) from the script's own server, and writes a PNG whose IHDR width is the
// viewport width, plus FAKE_EXTRA for docs pages.
const FAKE_NPX = `#!/usr/bin/env node
const { appendFileSync, writeFileSync } = require('node:fs');
const { execFileSync } = require('node:child_process');
const args = process.argv.slice(2);
if (!args.includes('screenshot')) { process.exit(0); }
const opt = (name) => {
  const i = args.findIndex((a) => a === '--' + name || a.startsWith('--' + name + '='));
  if (i < 0) { return null; }
  return args[i].includes('=') ? args[i].slice(args[i].indexOf('=') + 1) : args[i + 1];
};
const u = args.findIndex((a) => /^https?:\\/\\//.test(a));
const url = args[u];
const file = args[u + 1];
const get = (target) => {
  try { return execFileSync('curl', ['-fsS', target], { encoding: 'utf8' }); } catch { return null; }
};
const html = get(url);
// VitePress writes rel="preload stylesheet", so any rel that lists the stylesheet token counts.
const sheet = html && (html.match(/<link\\b[^>]*>/g) || [])
  .find((tag) => /\\brel="[^"]*\\bstylesheet\\b[^"]*"/.test(tag) && /\\bhref="[^"]+"/.test(tag));
const css = sheet && sheet.match(/\\bhref="([^"]+)"/);
const cssOk = css ? get(new URL(css[1], url).href) !== null : null;
const title = html && (html.match(/<title>([^<]*)<\\/title>/) || [])[1];
const viewport = opt('viewport-size') || '';
const width = Number(viewport.split(',')[0]);
const isDocs = /\\/docs\\//.test(new URL(url).pathname);
const drawn = width + (isDocs ? Number(process.env.FAKE_EXTRA || 0) : 0);
appendFileSync(process.env.FAKE_LOG, JSON.stringify({ url, file, width, scheme: opt('color-scheme'), title, cssOk }) + '\\n');
const png = Buffer.alloc(33);
Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]).copy(png, 0);
png.writeUInt32BE(13, 8);
png.write('IHDR', 12, 'ascii');
png.writeUInt32BE(drawn, 16);
png.writeUInt32BE(900, 20);
writeFileSync(file, png);
`;

function runScreenshots(tmp, extra) {
  const bin = mkdtempSync(join(tmpdir(), 'rogue-fake-npx-'));
  writeFileSync(join(bin, 'npx'), FAKE_NPX);
  chmodSync(join(bin, 'npx'), 0o755);
  const out = mkdtempSync(join(tmpdir(), 'rogue-shots-'));
  const log = join(bin, 'calls.jsonl');
  writeFileSync(log, '');
  const env = { ...process.env, PATH: `${bin}:${process.env.PATH}`, FAKE_LOG: log, FAKE_EXTRA: String(extra) };
  const run = spawnSync('bash', [SCREENSHOT, out], { cwd: tmp, env, encoding: 'utf8', timeout: 1_500_000 });
  const calls = readFileSync(log, 'utf8').split('\n').filter(Boolean).map((l) => JSON.parse(l));
  rmSync(bin, { recursive: true, force: true });
  return { run, calls, out };
}

const isGettingStarted = (c) => /\/docs\/(?:index(?:\.html)?)?$/.test(new URL(c.url).pathname);
const isISender = (c) => /\/docs\/api\/skathio-rogue-isender(?:\.html)?$/.test(new URL(c.url).pathname);
const isLanding = (c) => !/\/docs\//.test(new URL(c.url).pathname);

describe('built pages and the docs route screenshots', () => {
  const state = { tmp: null, build: null, fits: null, wide: null };

  before(() => {
    state.tmp = copyWorkingTree();
    state.build = spawnSync('bash', ['docs-site/build.sh'], { cwd: state.tmp, encoding: 'utf8', timeout: 1_500_000 });
    if (state.build.status === 0) {
      state.fits = runScreenshots(state.tmp, 0);
      state.wide = runScreenshots(state.tmp, 120);
    }
  });

  after(() => {
    for (const dir of [state.tmp, state.fits?.out, state.wide?.out]) {
      if (dir) { rmSync(dir, { recursive: true, force: true }); }
    }
  });

  function built() {
    assert.equal(state.build.status, 0, `docs-site/build.sh failed:\n${state.build.stdout}\n${state.build.stderr}`);
    return join(state.tmp, 'artifacts', 'pages');
  }

  test('every Docs section guide link and the nav Docs link open a built docs page', () => {
    const pages = built();
    const tmpLanding = readFileSync(join(state.tmp, LANDING), 'utf8');
    const links = [...docsSection(tmpLanding).filter((a) => !STAYS.includes(a.text)), navDocsLink(tmpLanding)];
    for (const { text, href } of links) {
      const url = publishedPath(href ?? '');
      assert.ok(url.pathname.startsWith(DOCS_BASE), `"${text}" does not open a ${DOCS_BASE} page: ${href}`);
      assert.ok(servedFile(pages, url.pathname), `"${text}" opens ${url.pathname}, which the build did not produce`);
    }
  });

  function shots(which) {
    built();
    const s = state[which];
    assert.ok(s, 'the screenshot script did not run');
    return s;
  }

  test('the landing shots are still taken at both widths in both themes', () => {
    const { run, out } = shots('fits');
    for (const name of ['phone', 'desktop']) {
      for (const scheme of SCHEMES) {
        const png = join(out, `site-${name}-${scheme}.png`);
        assert.ok(existsSync(png), `${png} is missing:\n${run.stdout}\n${run.stderr}`);
      }
    }
  });

  for (const [page, match, title] of [['Getting started', isGettingStarted, /Getting started/], ['ISender', isISender, /ISender/]]) {
    test(`it shoots the ${page} docs page at 390 and 1440 px in light and dark`, () => {
      const { run, calls, out } = shots('fits');
      const pageCalls = calls.filter(match);
      for (const width of WIDTHS) {
        for (const scheme of SCHEMES) {
          const call = pageCalls.find((c) => c.width === width && c.scheme === scheme);
          assert.ok(call, `no ${page} shot at ${width} px in ${scheme}:\n${run.stdout}\n${run.stderr}`);
          assert.match(call.title ?? '', title, `the ${page} shot did not load the ${page} page: ${call.url}`);
          assert.equal(call.cssOk, true, `the ${page} page's stylesheet does not load from ${call.url}`);
          assert.ok(call.file.startsWith(out), `the ${page} shot is not written to the output folder: ${call.file}`);
          assert.ok(existsSync(call.file), `${call.file} was not written`);
        }
      }
      const files = pageCalls.map((c) => c.file);
      assert.equal(new Set(files).size, WIDTHS.length * SCHEMES.length, `the ${page} shots overwrite each other: ${files}`);
    });
  }

  test('docs shots that fit their viewport raise no width warning', () => {
    const { run, calls } = shots('fits');
    assert.ok(calls.some(isLanding), 'no landing shot was taken');
    assert.equal(run.status, 0, `exit ${run.status}:\n${run.stdout}\n${run.stderr}`);
    assert.doesNotMatch(run.stderr, /WARN/);
  });

  test('a docs shot wider than its viewport gets the same width warning as the landing shots', () => {
    const { run, calls } = shots('wide');
    const docsCalls = calls.filter((c) => isGettingStarted(c) || isISender(c));
    assert.equal(docsCalls.length, 2 * WIDTHS.length * SCHEMES.length, 'expected eight docs shots');
    for (const c of docsCalls) {
      const line = run.stderr.split('\n').find((l) => l.startsWith('WARN:') && l.includes(c.file));
      assert.ok(line, `no width warning for ${c.file}:\n${run.stderr}`);
      assert.match(line, new RegExp(`${c.width + 120}px wide for a ${c.width}px viewport`));
    }
    assert.ok(!calls.filter(isLanding).some((c) => run.stderr.includes(c.file)), 'a landing shot that fits was warned about');
    assert.equal(run.status, 2, `a width warning must exit 2, as for the landing shots: exit ${run.status}`);
  });
});

describe('visual-test skill', () => {
  test('SKILL.md covers the docs route: Getting started and the ISender API page', () => {
    const skill = readFileSync(join(root, SKILL), 'utf8');
    assert.match(skill, /\/rogue\/docs\/|docs route/, 'SKILL.md does not mention the docs route');
    assert.match(skill, /Getting started/);
    assert.match(skill, /ISender/);
  });
});
