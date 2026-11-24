// Rules for the generated API reference under /rogue/docs/api/: docfx metadata (Markdown
// output, net10.0) over the 12 projects that ship a PublicAPI.Shipped.txt, the cleanup
// script, visible H1 titles, generic names with their angle brackets, links between API
// pages and inline code for outside types, the namespace and kind sidebar, the outline
// levels, the local search, and nothing generated committed.
//
// The cleanup cases drive docs-site/clean-api.mjs as a command, `<docfx-md-dir> <out-dir>`,
// the shape of the prototype it ports. The build cases copy the working tree (tracked and
// untracked files, nothing ignored: a clean checkout of this branch) into a temporary
// folder and run docs-site/build.sh there from its root, so they never race another build.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import {
  cpSync, existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, symlinkSync, writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const docsSite = dirname(fileURLToPath(import.meta.url));
const root = join(docsSite, '..');
const BASE = '/rogue/docs/';
const KINDS = { Class: 'Classes', Struct: 'Structs', Interface: 'Interfaces', Enum: 'Enums', Delegate: 'Delegates' };
const KIND_ORDER = ['Classes', 'Structs', 'Interfaces', 'Enums', 'Delegates'];
const state = { tmp: null, build: null, out: null, before: null };

function decode(s) {
  return s.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&#39;/g, "'").replace(/&amp;/g, '&');
}

function text(fragment) { return decode(fragment.replace(/<[^>]*>/g, '')).replace(/​/g, '').trim(); }

function git(...args) { return spawnSync('git', ['-C', root, ...args], { encoding: 'utf8' }); }

// ---------------------------------------------------------------- cleanup script

function runClean(files) {
  const dir = mkdtempSync(join(tmpdir(), 'clean-api-'));
  const src = join(dir, 'in');
  const dest = join(dir, 'out');
  mkdirSync(src);
  for (const [name, body] of Object.entries(files)) { writeFileSync(join(src, name), body); }
  const run = spawnSync(process.execPath, [join(docsSite, 'clean-api.mjs'), src, dest], { encoding: 'utf8', timeout: 60_000 });
  const pages = {};
  if (existsSync(dest)) {
    for (const f of readdirSync(dest).filter((n) => n.endsWith('.md'))) { pages[f] = readFileSync(join(dest, f), 'utf8'); }
  }
  const sidebarFile = existsSync(dest) ? readdirSync(dest).find((n) => n === 'sidebar.json') : null;
  const sidebar = sidebarFile ? JSON.parse(readFileSync(join(dest, sidebarFile), 'utf8')) : null;
  rmSync(dir, { recursive: true, force: true });
  return { run, pages, sidebar };
}

const xref = (href) => `<xref href="${href}" data-throw-if-not-resolved="false"></xref>`;

const FIXTURE = {
  'toc.yml': [
    '### YamlMime:TableOfContent',
    '- name: SkathIO.Rogue',
    '  href: SkathIO.Rogue.md',
    '  items:',
    '  - name: Classes',
    '  - name: RequestExceptionHandlerState<TResponse>',
    '    href: SkathIO.Rogue.RequestExceptionHandlerState-1.md',
    '  - name: Interfaces',
    '  - name: ICommand<TResponse>',
    '    href: SkathIO.Rogue.ICommand-1.md',
    '  - name: ISender',
    '    href: SkathIO.Rogue.ISender.md',
    '',
  ].join('\n'),
  'SkathIO.Rogue.md': [
    '# <a id="SkathIO_Rogue"></a> Namespace SkathIO.Rogue',
    '',
    '### Interfaces',
    '',
    ' [ISender](SkathIO.Rogue.ISender.md)',
    '',
    'Sends commands.',
    '',
    ' [ICommand<TResponse\\>](SkathIO.Rogue.ICommand\\-1.md)',
    '',
    'A command.',
    '',
  ].join('\n'),
  'SkathIO.Rogue.ISender.md': [
    '# <a id="SkathIO_Rogue_ISender"></a> Interface ISender',
    '',
    'Namespace: [SkathIO.Rogue](SkathIO.Rogue.md)  ',
    'Assembly: SkathIO.Rogue.Abstractions.dll  ',
    '',
    `Sends a ${xref('SkathIO.Rogue.ICommand%601')} returning <code class="typeparamref">TResponse</code>.`,
    '',
    '<p>',
    '<b>Resolve this from a scope.</b> Resolving from the root <code>IServiceProvider</code>',
    `throws ${xref('System.InvalidOperationException')}.`,
    '</p>',
    '',
    `Gone before ${xref('')} after.`,
    '',
    '## Methods',
    '',
    '### <a id="SkathIO_Rogue_ISender_Send_SkathIO_Rogue_ICommand_System_Threading_CancellationToken_"></a> Send\\(ICommand, CancellationToken\\)',
    '',
    '#### Parameters',
    '',
    '`cancellationToken` [CancellationToken](https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken)',
    '',
  ].join('\n'),
  'SkathIO.Rogue.ICommand-1.md': [
    '# <a id="SkathIO_Rogue_ICommand_1"></a> Interface ICommand<TResponse\\>',
    '',
    'Namespace: [SkathIO.Rogue](SkathIO.Rogue.md)  ',
    '',
    `Sent through ${xref('SkathIO.Rogue.ISender')}, see`,
    `${xref('SkathIO.Rogue.ISender.Send(SkathIO.Rogue.ICommand%2cSystem.Threading.CancellationToken)')} and`,
    `${xref('SkathIO.Rogue.RequestExceptionHandlerState%601.Handled')}.`,
    '',
  ].join('\n'),
  'SkathIO.Rogue.RequestExceptionHandlerState-1.md': [
    '# <a id="SkathIO_Rogue_RequestExceptionHandlerState_1"></a> Class RequestExceptionHandlerState<TResponse\\>',
    '',
    'Namespace: [SkathIO.Rogue](SkathIO.Rogue.md)  ',
    '',
    'State passed to exception handlers.',
    '',
  ].join('\n'),
};

// The title as a reader sees it: whether `<` is stored raw or as &lt; is the builder's choice.
function frontMatterTitle(page) {
  const fm = page.match(/^---\r?\n([\s\S]*?)\r?\n---/);
  const line = fm?.[1].split(/\r?\n/).find((l) => /^title:/.test(l));
  if (!line) { return null; }
  const raw = line.replace(/^title:\s*/, '').trim();
  if (raw.startsWith('"')) { return decode(JSON.parse(raw)); }
  if (raw.startsWith("'")) { return decode(raw.slice(1, -1).replace(/''/g, "'")); }
  return decode(raw);
}

const cleaned = { result: null };
function clean() {
  cleaned.result ??= runClean(FIXTURE);
  const { run } = cleaned.result;
  assert.equal(run.status, 0, `clean-api.mjs failed:\n${run.stdout}\n${run.stderr}`);
  return cleaned.result;
}

function pageTitled(title) {
  const { pages } = clean();
  const name = Object.keys(pages).find((f) => frontMatterTitle(pages[f]) === title);
  assert.ok(name, `no cleaned page has the title ${title}; titles: ${Object.values(pages).map(frontMatterTitle).join(', ')}`);
  return { name, body: pages[name] };
}

const linkTo = (file) => new RegExp(`\\]\\((?:\\./)?${file.replace(/[.-]/g, '\\$&')}(?:#[^)]*)?\\)`);

test('cleanup writes slug file names', () => {
  const { pages } = clean();
  assert.ok(pages['skathio-rogue-isender.md'], `no skathio-rogue-isender.md in ${Object.keys(pages).join(', ')}`);
  assert.equal(Object.keys(pages).length, 4);
  for (const f of Object.keys(pages)) { assert.match(f, /^[a-z0-9-]+\.md$/); }
});

test('cleanup gives each page a title field and one H1', () => {
  const { pages } = clean();
  assert.equal(frontMatterTitle(pages['skathio-rogue-isender.md']), 'Interface ISender');
  pageTitled('Interface ICommand<TResponse>');
  pageTitled('Namespace SkathIO.Rogue');
  for (const [f, body] of Object.entries(pages)) {
    const h1s = body.replace(/^---[\s\S]*?\n---/, '').replace(/```[\s\S]*?```/g, '').split('\n').filter((l) => /^# \S/.test(l));
    assert.equal(h1s.length, 1, `${f} has ${h1s.length} H1 lines`);
  }
});

test('cleanup turns an xref to one of our types into a link to its page', () => {
  const generic = pageTitled('Interface ICommand<TResponse>');
  assert.match(generic.body, linkTo('skathio-rogue-isender.md'));
  assert.match(clean().pages['skathio-rogue-isender.md'], linkTo(generic.name));
});

test('cleanup turns a member xref into a link to its type page', () => {
  const generic = pageTitled('Interface ICommand<TResponse>');
  const handlerState = pageTitled('Class RequestExceptionHandlerState<TResponse>');
  const tail = generic.body.slice(generic.body.indexOf(' see'));
  assert.match(tail, linkTo('skathio-rogue-isender.md'));
  assert.match(tail, linkTo(handlerState.name));
});

test('cleanup turns an xref to an outside type into inline code', () => {
  const page = clean().pages['skathio-rogue-isender.md'];
  assert.match(page, /`[^`]*InvalidOperationException`/);
  assert.doesNotMatch(page, /\[[^\]]*InvalidOperationException[^\]]*\]\(/);
});

test('cleanup turns an xref with an empty href into inline code', () => {
  const page = clean().pages['skathio-rogue-isender.md'];
  assert.match(page, /Gone before `[^`]*` after\./);
  assert.doesNotMatch(page, /\]\(\)/);
});

test('cleanup turns a reference to an outside type into inline code, not a link', () => {
  const page = clean().pages['skathio-rogue-isender.md'];
  assert.match(page, /`CancellationToken`/);
  assert.doesNotMatch(page, /\]\(https:\/\/learn\.microsoft\.com\/dotnet\/api\//);
});

test('cleanup leaves no xref element', () => {
  for (const [f, body] of Object.entries(clean().pages)) { assert.doesNotMatch(body, /<\/?xref\b/, f); }
});

test('cleanup turns a typeparamref into inline code', () => {
  const page = clean().pages['skathio-rogue-isender.md'];
  assert.match(page, /returning `TResponse`/);
  assert.doesNotMatch(page, /typeparamref/);
});

test('cleanup turns a <p> block with <b> and <code> into Markdown', () => {
  const page = clean().pages['skathio-rogue-isender.md'];
  assert.doesNotMatch(page, /<\/?(?:p|b|code)\b/);
  assert.match(page, /(\*\*|__)Resolve this from a scope\.\1/);
  assert.match(page, /`IServiceProvider`/);
});

test('cleanup points links between docfx pages at the slug files it wrote', () => {
  const { pages } = clean();
  for (const [f, body] of Object.entries(pages)) {
    for (const [, href] of body.matchAll(/\]\(([^)\s]+)\)/g)) {
      if (/^[a-z]+:/.test(href)) { continue; }
      const file = href.replace(/^\.\//, '').replace(/#.*$/, '');
      assert.ok(pages[file], `${f} links to ${href}, which was not written`);
    }
  }
});

function strings(value, found = []) {
  if (typeof value === 'string') { found.push(value); } else if (value && typeof value === 'object') {
    for (const v of Object.values(value)) { strings(v, found); }
  }
  return found;
}

test('cleanup writes sidebar.json with generic labels escaped', () => {
  const all = strings(clean().sidebar);
  assert.ok(all.length > 0, 'no sidebar.json was written');
  assert.ok(all.some((s) => /ICommand&lt;TResponse(?:>|&gt;)/.test(s)), 'the generic label is not escaped');
  assert.ok(!all.some((s) => /ICommand<|RequestExceptionHandlerState</.test(s)), 'a label holds a raw <');
});

test('cleanup lists the sidebar by namespace, then kind, in toc order', () => {
  const all = strings(clean().sidebar);
  const at = (label) => all.findIndex((s) => s.includes(label));
  const order = ['SkathIO.Rogue', 'Classes', 'RequestExceptionHandlerState&lt;', 'Interfaces', 'ICommand&lt;', 'ISender'].map(at);
  for (const i of order) { assert.ok(i >= 0, `missing from sidebar.json: ${JSON.stringify(all)}`); }
  assert.deepEqual([...order].sort((a, b) => a - b), order);
});

test('cleanup fails with a clear error on a file with no H1', () => {
  const { run } = runClean({
    'toc.yml': '- name: SkathIO.Rogue\n  href: SkathIO.Rogue.Broken.md\n',
    'SkathIO.Rogue.Broken.md': 'Namespace: SkathIO.Rogue\n\n## Methods\n',
  });
  assert.notEqual(run.status, 0, 'clean-api.mjs accepted a page with no H1');
  assert.match(run.stderr, /SkathIO\.Rogue\.Broken\.md|skathio-rogue-broken/);
  assert.match(run.stderr, /H1|heading/i);
});

// ---------------------------------------------------------------- the docs build

// Every file under dir, never following a symbolic link, minus the folders skip names.
function* walk(dir, skip = []) {
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    const p = join(dir, e.name);
    if (e.isSymbolicLink() || skip.includes(e.name)) { continue; }
    if (e.isDirectory()) { yield* walk(p, skip); } else { yield p; }
  }
}

function listTree(dir) {
  return new Set([...walk(dir)].map((p) => relative(dir, p)).filter((p) => !p.startsWith(`artifacts${sep}`)));
}

function copyWorkingTree() {
  const tmp = mkdtempSync(join(tmpdir(), 'rogue-api-'));
  const files = git('ls-files', '-co', '--exclude-standard', '-z').stdout.split('\0').filter(Boolean);
  for (const f of files) {
    const from = join(root, f);
    if (!existsSync(from) || lstatSync(from).isDirectory()) { continue; }
    mkdirSync(dirname(join(tmp, f)), { recursive: true });
    cpSync(from, join(tmp, f));
  }
  symlinkSync(join(docsSite, 'node_modules'), join(tmp, 'docs-site', 'node_modules'));
  return tmp;
}

// The built folder: the one holding api/skathio-rogue-isender.html.
function findOut(dir) {
  const page = join('api', 'skathio-rogue-isender.html');
  const hit = existsSync(dir) ? [...walk(dir)].find((p) => p.endsWith(`${sep}${page}`)) : null;
  return hit ? dirname(dirname(hit)) : null;
}

before(() => {
  state.tmp = copyWorkingTree();
  state.before = listTree(state.tmp);
  state.build = spawnSync('bash', [join(state.tmp, 'docs-site', 'build.sh')], { cwd: state.tmp, encoding: 'utf8', timeout: 1_500_000 });
  state.out = findOut(join(state.tmp, 'artifacts'));
});

after(() => { if (state.tmp) { rmSync(state.tmp, { recursive: true, force: true }); } });

function out() {
  assert.ok(state.out, `no built API pages found; build output:\n${state.build?.stdout}\n${state.build?.stderr}`);
  return state.out;
}

function html(rel) { return readFileSync(join(out(), rel), 'utf8'); }

const apiPages = () => readdirSync(join(out(), 'api')).filter((f) => f.endsWith('.html')).map((f) => `api/${f}`);

function headings(page) {
  return [...html(page).matchAll(/<h([1-6])\b[^>]*>([\s\S]*?)<\/h\1>/g)]
    .map((m) => ({ level: Number(m[1]), text: text(m[2].replace(/<a\b[^>]*class="header-anchor"[\s\S]*?<\/a>/g, '')) }));
}

function h1(page) { return headings(page).find((h) => h.level === 1)?.text ?? null; }

function publicTypes() {
  const projects = readdirSync(join(root, 'src')).filter((p) => existsSync(join(root, 'src', p, 'PublicAPI.Shipped.txt')));
  return projects.map((project) => {
    const read = (f) => (existsSync(join(root, 'src', project, f)) ? readFileSync(join(root, 'src', project, f), 'utf8').split('\n') : []);
    const typeLines = (lines) => lines.map((l) => l.trim()).filter((l) => l && !l.startsWith('#') && !l.includes('->'));
    const removed = new Set(typeLines(read('PublicAPI.Unshipped.txt')).filter((l) => l.startsWith('*REMOVED*')).map((l) => l.slice(9)));
    const added = typeLines(read('PublicAPI.Unshipped.txt')).filter((l) => !l.startsWith('*REMOVED*'));
    return { project, types: [...typeLines(read('PublicAPI.Shipped.txt')), ...added].filter((t) => !removed.has(t)) };
  });
}

function lastSegment(name) {
  let depth = 0;
  for (let i = name.length - 1; i >= 0; i--) {
    if (name[i] === '>') { depth++; } else if (name[i] === '<') { depth--; } else if (name[i] === '.' && depth === 0) { return name.slice(i + 1); }
  }
  return name;
}

test('the docs build exits 0 from a clean copy and prints no dead-link warning', () => {
  assert.ok(state.build, 'docs-site/build.sh did not run');
  const log = `${state.build.stdout}\n${state.build.stderr}`;
  assert.equal(state.build.status, 0, `build.sh failed:\n${log}`);
  assert.doesNotMatch(log, /dead link/i);
});

test('the API reference is built under /rogue/docs/api/ with the ISender page', () => {
  assert.ok(existsSync(join(out(), 'api', 'skathio-rogue-isender.html')));
  assert.equal(h1('api/skathio-rogue-isender.html'), 'Interface ISender');
});

test('docfx is a local dotnet tool at 2.81.0', () => {
  const manifest = JSON.parse(readFileSync(join(root, '.config', 'dotnet-tools.json'), 'utf8'));
  assert.equal(manifest.tools?.docfx?.version, '2.81.0');
});

function docfxConfig() {
  const candidates = [join(docsSite, 'docfx.json'), join(root, 'docfx.json')];
  const file = candidates.find(existsSync);
  assert.ok(file, 'no docfx.json in docs-site/');
  return JSON.parse(readFileSync(file, 'utf8'));
}

test('docfx metadata writes Markdown for net10.0', () => {
  const entries = docfxConfig().metadata;
  assert.ok(Array.isArray(entries) && entries.length > 0, 'docfx.json has no metadata section');
  for (const e of entries) {
    assert.equal(e.outputFormat, 'markdown');
    assert.equal(e.properties?.TargetFramework, 'net10.0');
  }
});

// Types hidden from IntelliSense with [EditorBrowsable(Never)] are left to the builder:
// the request neither asks for their pages nor rules them out.
const HIDDEN_TYPE = /\[EditorBrowsable\(EditorBrowsableState\.Never\)\]\s*(?:\[[^\]]*\]\s*)*(?:(?:public|static|sealed|abstract|partial|readonly)\s+)*(?:class|struct|interface|enum|record(?:\s+struct|\s+class)?)\s+(\w+)/g;

function hiddenTypes(project) {
  const sources = [...walk(join(root, 'src', project), ['bin', 'obj'])].filter((p) => p.endsWith('.cs'));
  return new Set(sources.flatMap((p) => [...readFileSync(p, 'utf8').matchAll(HIDDEN_TYPE)].map((m) => m[1])));
}

test('the API covers every public type of the 12 projects with a PublicAPI.Shipped.txt', () => {
  const projects = publicTypes();
  assert.equal(projects.length, 12);
  const titles = apiPages().map(h1).filter(Boolean);
  for (const { project, types } of projects) {
    assert.ok(types.length > 0, `${project} lists no public type`);
    const hidden = hiddenTypes(project);
    for (const t of types.filter((x) => !hidden.has(lastSegment(x).replace(/<.*$/, '')))) {
      const name = lastSegment(t);
      assert.ok(titles.some((h) => h.endsWith(` ${name}`) || h.endsWith(`.${name}`)), `${project}: no API page for ${t}`);
    }
  }
});

test('every API page has a visible H1 that is also its title', () => {
  const pages = apiPages();
  assert.ok(pages.length > 12);
  for (const page of pages) {
    const heading = h1(page);
    assert.ok(heading, `${page} has no H1`);
    const title = text(html(page).match(/<title>([\s\S]*?)<\/title>/)?.[1] ?? '');
    assert.ok(title.startsWith(heading), `${page}: <title> "${title}" does not start with "${heading}"`);
  }
});

test('generic names keep their angle brackets in API headings', () => {
  const titles = apiPages().map(h1);
  assert.ok(titles.includes('Interface ICommand<TResponse>'), 'no H1 "Interface ICommand<TResponse>"');
  const isender = headings('api/skathio-rogue-isender.html');
  assert.ok(isender.some((h) => [2, 3].includes(h.level) && h.text === 'CreateStream<TItem>(IStreamQuery<TItem>, CancellationToken)'),
    `ISender headings: ${isender.map((h) => h.text).join(' | ')}`);
});

test('the outline lists headings at levels 2 and 3, and every method heading is at one of them', async () => {
  const { resolveConfig } = await import('vitepress');
  const config = await resolveConfig(join(state.tmp, 'docs-site'), 'build', 'production');
  const outline = config.site.themeConfig.outline;
  assert.deepEqual(Array.isArray(outline) ? outline : outline?.level, [2, 3]);
  for (const page of apiPages()) {
    for (const h of headings(page).filter((x) => x.text.includes('('))) {
      assert.ok([2, 3].includes(h.level), `${page}: "${h.text}" is an h${h.level}`);
    }
  }
});

test('cross-references between API pages are links that resolve, and no xref is left', () => {
  let internal = 0;
  for (const page of apiPages()) {
    const body = html(page);
    assert.doesNotMatch(body, /<\/?xref\b|&lt;xref\b/, page);
    for (const [, href] of body.matchAll(/<a\b[^>]*\bhref="([^"]*)"/g)) {
      const url = new URL(decode(href), `http://docs.test${BASE}${page}`);
      if (url.origin !== 'http://docs.test' || !url.pathname.startsWith(`${BASE}api/`)) { continue; }
      internal++;
      assert.ok(existsSync(join(out(), decodeURIComponent(url.pathname.slice(BASE.length)))), `${page} links to missing ${url.pathname}`);
    }
  }
  assert.ok(internal > 0, 'no API page links to another');
});

test('references to types outside our packages show as inline code, not links', () => {
  for (const page of apiPages()) {
    assert.doesNotMatch(html(page), /href="https:\/\/learn\.microsoft\.com\/dotnet\/api\//, page);
  }
});

function sidebarLeaves(items, ancestors = [], found = []) {
  for (const item of items ?? []) {
    if (item.items) { sidebarLeaves(item.items, [item, ...ancestors], found); }
    if (item.link && !item.items) { found.push({ item, ancestors }); }
  }
  return found;
}

test('the API sidebar is grouped by namespace, then Classes, Structs, Interfaces, Enums and Delegates', async () => {
  const { resolveConfig } = await import('vitepress');
  const config = await resolveConfig(join(state.tmp, 'docs-site'), 'build', 'production');
  const sb = config.site.themeConfig.sidebar;
  const leaves = sidebarLeaves(Array.isArray(sb) ? sb : Object.values(sb).flat());
  const fullNames = new Set(publicTypes().flatMap((p) => p.types));
  const norm = (link) => link.replace(/^\//, '').replace(/\.(md|html)$/, '');
  for (const page of apiPages()) {
    const [kind, ...rest] = h1(page).split(' ');
    if (!KINDS[kind]) { continue; }
    const display = rest.join(' ');
    const hits = leaves.filter((l) => norm(l.item.link) === page.replace(/\.html$/, ''));
    assert.equal(hits.length, 1, `${page} is listed ${hits.length} times in the sidebar`);
    const [{ item, ancestors }] = hits;
    assert.match(ancestors[0]?.text ?? '', new RegExp(`\\b${KINDS[kind]}$`), `${page} is not under ${KINDS[kind]}`);
    const ns = ancestors[1]?.text?.match(/SkathIO(?:\.\w+)*/)?.[0];
    assert.ok(ns && fullNames.has(`${ns}.${display}`), `${page} sits under "${ancestors[1]?.text}", not its namespace`);
    assert.ok(!item.text.includes('<'), `the sidebar label "${item.text}" holds a raw <`);
    assert.equal(decode(item.text), display);
    const kindsHere = (ancestors[1].items ?? []).map((g) => KIND_ORDER.findIndex((k) => new RegExp(`\\b${k}$`).test(g.text ?? ''))).filter((i) => i >= 0);
    assert.deepEqual([...kindsHere].sort((a, b) => a - b), kindsHere, `kind groups out of order under ${ns}`);
  }
});

async function searchIndex() {
  const chunks = join(out(), 'assets', 'chunks');
  const file = readdirSync(chunks).find((f) => f.startsWith('@localSearchIndex'));
  assert.ok(file, 'no local search index was built');
  const source = readFileSync(join(chunks, file), 'utf8').replace(/export\s*\{\s*(\w+)\s+as\s+default\s*\}\s*;?\s*$/, '$1');
  const { default: MiniSearch } = await import('minisearch');
  return MiniSearch.loadJSON(vm.runInNewContext(source), {
    fields: ['title', 'titles', 'text'],
    storeFields: ['title', 'titles'],
    searchOptions: { fuzzy: 0.2, prefix: true, boost: { title: 4, text: 2, titles: 1 } },
  });
}

test('searching "ISender" lists "Interface ISender" first', async () => {
  const results = (await searchIndex()).search('ISender');
  assert.ok(results.length > 0, 'no search result for ISender');
  assert.equal(results[0].title, 'Interface ISender');
  assert.match(results[0].id, /\/api\/[^/]+\.html#/);
});

// The search box shows the first 16 results. Each page is searched by its name, without
// the kind word and the generic parameters a reader would not type.
test('every API page is found by the docs search under its name', async () => {
  const index = await searchIndex();
  for (const page of apiPages()) {
    const name = h1(page).split(' ').slice(1).join(' ').replace(/<.*$/, '');
    const hits = index.search(name).slice(0, 16).map((r) => r.id.split('#')[0]);
    assert.ok(hits.includes(`${BASE}${page}`), `searching "${name}" does not list ${page}`);
  }
});

test('the search covers guides as well as the API', async () => {
  const ids = (await searchIndex()).search('jobs').map((r) => r.id);
  assert.ok(ids.some((id) => !id.includes('/api/')), 'no guide in the search results');
  assert.ok((await searchIndex()).search('ISender').some((r) => r.id.includes('/api/')));
});

test('no generated API page is committed', () => {
  const tracked = git('ls-files', '-z').stdout.split('\0');
  const generated = tracked.filter((f) => /(^|\/)api\/[^/]+\.md$|skathio-rogue-[\w-]*\.md$|^SkathIO\.Rogue[\w.-]*\.md$|(^|\/)sidebar\.json$|(^|\/)toc\.yml$/i.test(f));
  assert.deepEqual(generated, []);
});

test('everything the build writes outside artifacts/ is gitignored', () => {
  out();
  const created = [...listTree(state.tmp)].filter((p) => !state.before.has(p));
  if (created.length === 0) { return; }
  const check = spawnSync('git', ['-C', root, 'check-ignore', '--no-index', '--stdin'], { input: created.join('\n'), encoding: 'utf8' });
  const ignored = new Set(check.stdout.split('\n').filter(Boolean));
  assert.deepEqual(created.filter((p) => !ignored.has(p)), []);
});
