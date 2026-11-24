// Rules for the VitePress guides site served at /rogue/docs/: the repo's docs/*.md
// are built in place (no edits, no front matter), published pages and titles,
// link handling, the dead-link check, local search, the landing page's palette,
// font and logo, the two-way theme sync, and the links back to the landing page.
//
// The tests drive the public entry point, docs-site/build.sh run from the repo
// root, and then read the built HTML. The dead-link and outbound-link cases also
// rebuild the staged sources with the VitePress CLI into a temporary folder.
import { test, before } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import {
  appendFileSync, existsSync, mkdtempSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const docsSite = dirname(fileURLToPath(import.meta.url));
const root = join(docsSite, '..');
const docsDir = join(root, 'docs');
const BASE = '/rogue/docs/';
const ORIGIN = 'http://docs.test';
const GITHUB = 'https://github.com/skathio/rogue/blob/main/';
const EXCLUDED = ['governance.md', 'roadmap.md'];
const VP_KEY = 'vitepress-theme-appearance';

const published = readdirSync(docsDir).filter((f) => f.endsWith('.md') && !EXCLUDED.includes(f)).sort();
const state = { build: null, out: null, docsBefore: null };

function snapshot(dir) {
  const files = {};
  const walk = (d) => {
    for (const e of readdirSync(d, { withFileTypes: true })) {
      const p = join(d, e.name);
      if (e.isDirectory()) { walk(p); } else { files[relative(dir, p)] = createHash('sha256').update(readFileSync(p)).digest('hex'); }
    }
  };
  walk(dir);
  return files;
}

// Built output folders: they hold getting-started's page and an assets/ folder,
// and were written by this run of the build.
function isFreshBuild(dir, names, since) {
  const page = ['getting-started.html', 'index.html'].find((n) => names.includes(n));
  return Boolean(page) && names.includes('assets') && statSync(join(dir, page)).mtimeMs >= since;
}

function findBuiltDirs(dir, since, depth = 0, found = []) {
  if (depth > 6 || !existsSync(dir)) { return found; }
  const entries = readdirSync(dir, { withFileTypes: true });
  if (isFreshBuild(dir, entries.map((e) => e.name), since)) { found.push(dir); }
  const subdirs = entries.filter((e) => e.isDirectory() && e.name !== 'node_modules' && !e.name.startsWith('.git'));
  for (const e of subdirs) { findBuiltDirs(join(dir, e.name), since, depth + 1, found); }
  return found;
}

before(() => {
  state.docsBefore = snapshot(docsDir);
  const since = Date.now() - 1000;
  state.build = spawnSync('bash', [join(docsSite, 'build.sh')], { cwd: root, encoding: 'utf8', timeout: 900_000 });
  const dirs = [join(root, 'artifacts'), docsSite].flatMap((d) => findBuiltDirs(d, since));
  state.out = dirs.find((d) => existsSync(join(d, 'jobs.html'))) ?? null;
});

function out() {
  assert.ok(state.out, `no built docs folder found; build output:\n${state.build?.stdout}\n${state.build?.stderr}`);
  return state.out;
}

// Maps a URL path under the docs base to a built file, as a static host serves it.
function urlToFile(pathname) {
  if (!pathname.startsWith(BASE)) { return null; }
  let rel = decodeURIComponent(pathname.slice(BASE.length));
  if (rel === '' || rel.endsWith('/')) { rel += 'index.html'; } else if (!/\.[a-z0-9]+$/i.test(rel)) { rel += '.html'; }
  return rel;
}

function builtPageFor(md) {
  const name = md.replace(/\.md$/, '');
  const candidates = name === 'getting-started' ? [`${name}.html`, 'index.html'] : [`${name}.html`];
  return candidates.find((c) => existsSync(join(out(), c))) ?? null;
}

function html(rel) { return readFileSync(join(out(), rel), 'utf8'); }

function decode(s) {
  return s.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&#39;/g, "'").replace(/&amp;/g, '&');
}

function text(fragment) { return decode(fragment.replace(/<[^>]*>/g, '')).replace(/​/g, '').trim(); }

function sourceH1(md) {
  const line = readFileSync(join(docsDir, md), 'utf8').split('\n').find((l) => l.startsWith('# '));
  return line.slice(2).replace(/\[([^\]]*)\]\([^)]*\)/g, '$1').replace(/[`*]/g, '').trim();
}

function builtH1(page) {
  const m = html(page).match(/<h1\b[^>]*>([\s\S]*?)<\/h1>/);
  return m ? text(m[1].replace(/<a\b[^>]*class="header-anchor"[\s\S]*?<\/a>/g, '')) : null;
}

function anchors(page) {
  return [...html(page).matchAll(/<a\b([^>]*)>([\s\S]*?)<\/a>/g)].map((m) => ({
    href: decode(m[1].match(/\bhref="([^"]*)"/)?.[1] ?? ''), attrs: m[1], inner: m[2],
  }));
}

// Each href of a built page, resolved as the browser resolves it.
function resolvedLinks(page) {
  const pageUrl = new URL(BASE + page, ORIGIN);
  return anchors(page).filter((a) => a.href).map((a) => {
    const u = new URL(a.href, pageUrl);
    return { url: u, file: u.origin === ORIGIN ? urlToFile(u.pathname) : null, hash: decodeURIComponent(u.hash.slice(1)) };
  });
}

function sourceLinks(md) {
  return [...readFileSync(join(docsDir, md), 'utf8').matchAll(/\]\(([^)\s]+)\)/g)].map((m) => m[1]);
}

test('the docs build exits 0 and prints no dead-link warning', () => {
  assert.ok(state.build, 'docs-site/build.sh did not run');
  const log = `${state.build.stdout}\n${state.build.stderr}`;
  assert.equal(state.build.status, 0, `build.sh failed:\n${log}`);
  assert.doesNotMatch(log, /dead link/i);
});

test('the build leaves docs/ unchanged and adds nothing inside it', () => {
  out();
  assert.deepEqual(snapshot(docsDir), state.docsBefore);
  assert.equal(existsSync(join(docsDir, '.vitepress')), false);
});

test('no docs/*.md page carries front matter', () => {
  for (const md of readdirSync(docsDir).filter((f) => f.endsWith('.md'))) {
    assert.doesNotMatch(readFileSync(join(docsDir, md), 'utf8'), /^﻿?---\s*\n/, `${md} has front matter`);
  }
});

function git(...args) {
  const run = spawnSync('git', args, { cwd: root, encoding: 'utf8' });
  assert.equal(run.status, 0, `git ${args.join(' ')} failed:\n${run.stderr}`);
  return run.stdout.trim();
}

test('the branch neither edits nor adds a file under docs/, and copies no docs page elsewhere', () => {
  const base = git('merge-base', 'HEAD', 'main');
  assert.equal(git('diff', '--name-only', base, '--', 'docs/'), '', 'docs/ differs from main');
  assert.equal(git('ls-files', '--others', '--exclude-standard', '--', 'docs/'), '', 'new files in docs/');
  const sources = new Set(readdirSync(docsDir).filter((f) => f.endsWith('.md')).map((f) => readFileSync(join(docsDir, f), 'utf8')));
  const added = git('diff', '--name-only', '--diff-filter=A', base).split('\n')
    .concat(git('ls-files', '--others', '--exclude-standard').split('\n'))
    .filter((f) => f.endsWith('.md') && !f.startsWith('docs/') && existsSync(join(root, f)));
  for (const f of added) { assert.equal(sources.has(readFileSync(join(root, f), 'utf8')), false, `${f} is a copy of a docs page`); }
});

test('every docs/*.md page except governance.md and roadmap.md is published', () => {
  for (const md of published) { assert.ok(builtPageFor(md), `${md} has no built page`); }
  for (const md of EXCLUDED) {
    const name = md.replace(/\.md$/, '');
    assert.equal(existsSync(join(out(), `${name}.html`)), false, `${md} was published`);
    assert.equal(existsSync(join(out(), name, 'index.html')), false, `${md} was published`);
  }
});

test('/rogue/docs/ opens Getting started', () => {
  assert.ok(existsSync(join(out(), 'index.html')), 'no index.html at /rogue/docs/');
  const index = html('index.html');
  const isPage = builtH1('index.html') === sourceH1('getting-started.md');
  const redirect = index.match(/http-equiv="refresh"[^>]*content="[^"]*url=([^"]+)"/i)?.[1]
    ?? index.match(/location\.(?:replace\(|href\s*=\s*)['"]([^'"]+)['"]/)?.[1];
  const target = redirect ? urlToFile(new URL(decode(redirect), new URL(BASE, ORIGIN)).pathname) : null;
  assert.ok(isPage || target === builtPageFor('getting-started.md'), 'index.html neither is nor redirects to Getting started');
});

test('each page title comes from its # H1', () => {
  for (const md of published) {
    const page = builtPageFor(md);
    const h1 = sourceH1(md);
    const title = decode(html(page).match(/<title>([^<]*)<\/title>/)?.[1] ?? '');
    assert.ok(title.startsWith(h1), `${md}: <title> "${title}" does not start with "${h1}"`);
    assert.equal(builtH1(page), h1, `${md}: rendered H1`);
  }
});

// Source links from one published page to another: [file, anchor] pairs.
function crossPageLinks(md) {
  return sourceLinks(md)
    .map((href) => href.match(/^(?:\.\/)?([\w-]+\.md)(?:#(.*))?$/))
    .filter((m) => m && published.includes(m[1]))
    .map((m) => [m[1], m[2] ?? '']);
}

test('relative links between published pages resolve to the built page and anchor', () => {
  let checked = 0;
  for (const md of published) {
    const links = resolvedLinks(builtPageFor(md));
    for (const [file, hash] of crossPageLinks(md)) {
      const target = builtPageFor(file);
      assert.ok(links.some((l) => l.file === target && l.hash === hash), `${md}: ${file}#${hash} does not link to ${target}#${hash}`);
      assert.ok(!hash || html(target).includes(`id="${hash}"`), `${target} has no #${hash}`);
      checked++;
    }
  }
  assert.ok(checked > 0, 'no cross-page links were checked');
});

test('getting-started links jobs.md#stores to the built jobs page', () => {
  const page = builtPageFor('getting-started.md');
  const hrefs = anchors(page).map((a) => a.href);
  assert.ok(hrefs.some((h) => h === './jobs.html#stores' || h === './jobs#stores'), 'no ./jobs.html#stores link');
});

function expectedGithubUrl(href) {
  const m = href.match(/^(?:\.\/)?(\.\.\/[^#]*|(?:governance|roadmap)\.md)(#.*)?$/);
  if (!m) { return null; }
  const path = m[1].startsWith('../') ? m[1].slice(3) : `docs/${m[1]}`;
  return GITHUB + path + (m[2] ?? '');
}

test('links that leave docs/ or reach an excluded page go to GitHub with the anchor kept', () => {
  let checked = 0;
  for (const md of published) {
    const hrefs = anchors(builtPageFor(md)).map((a) => a.href);
    for (const href of sourceLinks(md)) {
      const want = expectedGithubUrl(href);
      if (!want) { continue; }
      assert.ok(hrefs.includes(want), `${md}: ${href} is not rewritten to ${want}`);
      checked++;
    }
  }
  assert.ok(checked > 0, 'no outbound links were checked');
  const gs = anchors(builtPageFor('getting-started.md')).map((a) => a.href);
  assert.ok(gs.includes(`${GITHUB}samples/minimal-api`));
});

// Builds the staged sources with one staged page changed, then puts the page back.
async function buildWithStagedChange(extra) {
  const { resolveConfig } = await import('vitepress');
  const config = await resolveConfig(docsSite, 'build', 'production');
  const staged = join(config.srcDir, 'jobs.md');
  const original = readFileSync(staged, 'utf8');
  const tmp = mkdtempSync(join(tmpdir(), 'rogue-docs-'));
  try {
    appendFileSync(staged, `\n\n${extra}\n`);
    const cli = join(docsSite, 'node_modules', 'vitepress', 'bin', 'vitepress.js');
    const run = spawnSync(process.execPath, [cli, 'build', docsSite, '--outDir', tmp], { cwd: root, encoding: 'utf8', timeout: 600_000 });
    const jobs = existsSync(join(tmp, 'jobs.html')) ? readFileSync(join(tmp, 'jobs.html'), 'utf8') : '';
    return { run, jobs };
  } finally {
    writeFileSync(staged, original);
    rmSync(tmp, { recursive: true, force: true });
  }
}

test('the dead-link check is on: ignoreDeadLinks is not set', async () => {
  out();
  const { resolveConfig } = await import('vitepress');
  const config = await resolveConfig(docsSite, 'build', 'production');
  assert.equal(config.userConfig.ignoreDeadLinks, undefined);
});

test('a link to a missing page in a staged doc fails the build', async () => {
  out();
  const { run } = await buildWithStagedChange('[x](missing.md)');
  assert.notEqual(run.status, 0, `the build passed with a dead link:\n${run.stdout}\n${run.stderr}`);
});

test('a staged ../CHANGELOG.md link and an excluded-page link build and go to GitHub', async () => {
  out();
  const { run, jobs } = await buildWithStagedChange('[c](../CHANGELOG.md#unreleased) [r](roadmap.md) [g](./governance.md#dependency-policy)');
  assert.equal(run.status, 0, `${run.stdout}\n${run.stderr}`);
  for (const want of [`${GITHUB}CHANGELOG.md#unreleased`, `${GITHUB}docs/roadmap.md`, `${GITHUB}docs/governance.md#dependency-policy`]) {
    assert.ok(jobs.includes(`href="${want}"`), `missing ${want}`);
  }
});

test('the site is served under /rogue/docs/ and loads nothing from another origin', () => {
  for (const md of published) {
    const head = html(builtPageFor(md)).match(/<head>([\s\S]*?)<\/head>/)[1];
    const urls = [...head.matchAll(/<(?:script|link)\b[^>]*\b(?:src|href)="([^"]*)"/g)].map((m) => m[1]);
    assert.ok(urls.length > 0);
    for (const u of urls) { assert.ok(u.startsWith(BASE), `${md}: ${u} is not under ${BASE}`); }
  }
});

test('local search indexes every published guide and no excluded page', () => {
  const chunks = join(out(), 'assets', 'chunks');
  const index = readdirSync(chunks).find((f) => f.startsWith('@localSearchIndex'));
  assert.ok(index, 'no local search index was built');
  const body = readFileSync(join(chunks, index), 'utf8');
  const files = new Set([...body.matchAll(/(\/rogue\/docs\/[^"#\\]*)#/g)].map((m) => urlToFile(m[1])));
  for (const md of published) { assert.ok(files.has(builtPageFor(md)), `${md} is not in the search index`); }
  for (const md of EXCLUDED) { assert.equal(files.has(md.replace(/\.md$/, '.html')), false, `${md} is in the search index`); }
});

function cssRules() {
  const assets = join(out(), 'assets');
  const css = readdirSync(assets).filter((f) => f.endsWith('.css')).map((f) => readFileSync(join(assets, f), 'utf8')).join('\n');
  return [...css.toLowerCase().matchAll(/([^{}]+)\{([^{}]*)\}/g)].map((m) => ({ selector: m[1].trim(), body: m[2] }));
}

function siteToken(scheme, name) {
  const page = readFileSync(join(root, 'site', 'index.html'), 'utf8');
  const block = scheme === 'light' ? page.match(/:root\{([^}]*)\}/)[1] : page.match(/:root\[data-theme=dark\]\{([^}]*)\}/)[1];
  return block.match(new RegExp(`--${name}:([^;]+)`))[1].trim().toLowerCase();
}

function colorForms(value) {
  const short = value.match(/^#([0-9a-f])([0-9a-f])([0-9a-f])$/);
  const long = value.match(/^#([0-9a-f])\1([0-9a-f])\2([0-9a-f])\3$/);
  const forms = [value];
  if (short) { forms.push(`#${short[1]}${short[1]}${short[2]}${short[2]}${short[3]}${short[3]}`); }
  if (long) { forms.push(`#${long[1]}${long[2]}${long[3]}`); }
  if (value === '#fff' || value === '#ffffff') { forms.push('white'); }
  return forms;
}

test('the docs use the landing palette in light and dark', () => {
  const rules = cssRules();
  const light = rules.filter((r) => r.selector.includes(':root') && !r.selector.replace(/:not\(\.dark\)/g, '').includes('.dark'));
  const dark = rules.filter((r) => /\.dark\b/.test(r.selector.replace(/:not\(\.dark\)/g, '')));
  assert.equal(siteToken('light', 'accent'), '#6d28d9');
  assert.equal(siteToken('dark', 'accent'), '#a78bfa');
  assert.equal(siteToken('light', 'canvas'), '#f7f6f2');
  assert.equal(siteToken('dark', 'canvas'), '#101216');
  for (const [scheme, set] of [['light', light], ['dark', dark]]) {
    for (const token of ['accent', 'canvas', 'surface', 'line']) {
      const forms = colorForms(siteToken(scheme, token));
      assert.ok(set.some((r) => forms.some((f) => new RegExp(`:\\s*${f}(?![0-9a-f])`).test(r.body))), `${scheme} ${token} ${forms[0]} is not used`);
    }
  }
});

test('the docs use the landing system font stack', () => {
  const norm = (s) => s.toLowerCase().replace(/["'\s]/g, '');
  const stack = readFileSync(join(root, 'site', 'index.html'), 'utf8').match(/--sans:([^;]+);/)[1];
  assert.ok(cssRules().some((r) => norm(r.body).includes(norm(stack))), `font stack ${stack} is not used`);
});

test('the docs logo is the landing mark.svg and links to /rogue/', () => {
  const mark = readFileSync(join(root, 'site', 'mark.svg'));
  const page = builtPageFor('getting-started.md');
  const pageUrl = new URL(BASE + page, ORIGIN);
  const logos = [...html(page).matchAll(/<img\b[^>]*class="[^"]*\blogo\b[^"]*"[^>]*>/g)].map((m) => m[0]);
  assert.ok(logos.length > 0, 'no logo image');
  for (const img of logos) {
    const file = urlToFile(new URL(decode(img.match(/\bsrc="([^"]*)"/)[1]), pageUrl).pathname);
    assert.ok(file && readFileSync(join(out(), file)).equals(mark), `${img} is not site/mark.svg`);
  }
  const link = anchors(page).find((a) => /<img\b[^>]*class="[^"]*\blogo\b/.test(a.inner));
  assert.ok(link, 'the logo is not a link');
  assert.equal(new URL(link.href, pageUrl).pathname, '/rogue/');
});

test('a nav item "← Rogue" goes to /rogue/', () => {
  const page = builtPageFor('getting-started.md');
  const item = anchors(page).find((a) => text(a.inner) === '← Rogue');
  assert.ok(item, 'no "← Rogue" nav item');
  assert.equal(new URL(item.href, new URL(BASE + page, ORIGIN)).pathname, '/rogue/');
});

// Runs the page's inline head scripts against a stub browser, as they run before
// first paint, so the class on <html> at that point is what the visitor sees first.
function loadHead(storage, prefersDark) {
  const head = html(builtPageFor('getting-started.md')).match(/<head>([\s\S]*?)<\/head>/)[1];
  const scripts = [...head.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/g)].filter((m) => !/\bsrc=/.test(m[1])).map((m) => m[2]);
  const classes = new Set();
  const observers = [];
  const listeners = [];
  const store = new Map(Object.entries(storage));
  const localStorage = {
    getItem: (k) => (store.has(k) ? store.get(k) : null),
    setItem: (k, v) => { store.set(k, String(v)); },
    removeItem: (k) => { store.delete(k); },
  };
  const docEl = {
    classList: {
      add: (c) => classes.add(c), remove: (c) => classes.delete(c), contains: (c) => classes.has(c),
      toggle: (c, force) => { const on = force ?? !classes.has(c); if (on) { classes.add(c); } else { classes.delete(c); } return on; },
    },
    setAttribute() {}, removeAttribute() {}, getAttribute: () => null, dataset: {}, style: {},
  };
  const on = (type, fn) => { if (type === 'DOMContentLoaded' || type === 'load') { listeners.push(fn); } };
  const sandbox = {
    localStorage, navigator: { platform: 'Linux' }, document: { documentElement: docEl, addEventListener: on, readyState: 'loading' },
    matchMedia: () => ({ matches: prefersDark, addEventListener() {}, addListener() {} }),
    addEventListener: on,
    MutationObserver: class { constructor(cb) { this.cb = cb; } observe(target) { observers.push({ cb: this.cb, target }); } disconnect() {} },
  };
  sandbox.window = sandbox;
  vm.createContext(sandbox);
  for (const s of scripts) { vm.runInContext(s, sandbox); }
  const darkAtFirstPaint = classes.has('dark');
  for (const fn of listeners) { fn(); }
  const toggle = (dark) => {
    localStorage.setItem(VP_KEY, dark ? 'dark' : 'light');
    docEl.classList.toggle('dark', dark);
    for (const o of observers.filter((x) => x.target === docEl)) { o.cb([{ type: 'attributes', attributeName: 'class', target: docEl }]); }
  };
  return { darkAtFirstPaint, toggle, stored: (k) => localStorage.getItem(k) };
}

test('a stored landing theme of dark opens the docs dark before first paint', () => {
  assert.equal(loadHead({ theme: 'dark' }, false).darkAtFirstPaint, true);
  assert.equal(loadHead({ theme: 'dark', [VP_KEY]: 'light' }, false).darkAtFirstPaint, true);
});

test('a stored landing theme of light opens the docs light before first paint', () => {
  assert.equal(loadHead({ theme: 'light' }, true).darkAtFirstPaint, false);
  assert.equal(loadHead({ theme: 'light', [VP_KEY]: 'dark' }, true).darkAtFirstPaint, false);
});

test('a missing landing theme means auto', () => {
  assert.equal(loadHead({}, true).darkAtFirstPaint, true);
  assert.equal(loadHead({}, false).darkAtFirstPaint, false);
  assert.equal(loadHead({ [VP_KEY]: 'dark' }, false).darkAtFirstPaint, false);
  assert.equal(loadHead({ [VP_KEY]: 'light' }, true).darkAtFirstPaint, true);
});

test('an unknown landing theme means auto', () => {
  for (const value of ['auto', 'sepia', '']) {
    assert.equal(loadHead({ theme: value }, true).darkAtFirstPaint, true, `theme=${JSON.stringify(value)}, system dark`);
    assert.equal(loadHead({ theme: value, [VP_KEY]: 'dark' }, false).darkAtFirstPaint, false, `theme=${JSON.stringify(value)}, system light`);
  }
});

test('toggling in the docs writes light or dark back to the landing theme key', () => {
  const page = loadHead({ theme: 'dark' }, true);
  page.toggle(false);
  assert.equal(page.stored('theme'), 'light');
  page.toggle(true);
  assert.equal(page.stored('theme'), 'dark');
  const fresh = loadHead({}, false);
  fresh.toggle(true);
  assert.equal(fresh.stored('theme'), 'dark');
});
