// Rules for publishing the docs with the landing page: one build script, run from the repo
// root, assembles a single Pages folder (the site/ files at its root, the VitePress output
// under docs/, nothing colliding); site.yml uploads that folder with its triggers unchanged;
// and a CI job on every pull request runs the full docs build, so a dead link or a build
// error fails the pull request.
//
// The assembly cases copy the working tree (tracked and untracked files, nothing ignored: a
// clean checkout of this branch) into a temporary folder and run docs-site/build.sh there
// from its root, so they never race another build. The folder they check is the one
// site.yml uploads. The workflow cases read the YAML as text, by indentation.
import { test, describe, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import {
  cpSync, existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, symlinkSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const docsSite = dirname(fileURLToPath(import.meta.url));
const root = join(docsSite, '..');
const SITE_YML = '.github/workflows/site.yml';
const CI_YML = '.github/workflows/ci.yml';
const PUBLISH_YML = '.github/workflows/publish.yml';
const VERSION = /\bv\d+(?:\.\d+)*\b/;

function git(...args) { return spawnSync('git', ['-C', root, ...args], { encoding: 'utf8' }); }

function read(rel) { return readFileSync(join(root, rel), 'utf8'); }

function atHead(rel) {
  const shown = git('show', `HEAD:${rel}`);
  assert.equal(shown.status, 0, `cannot read ${rel} at HEAD: ${shown.stderr}`);
  return shown.stdout;
}

// ---------------------------------------------------------------- YAML by indentation

const indentOf = (line) => line.match(/^ */)[0].length;
const isBlank = (line) => line.trim() === '' || line.trim().startsWith('#');

// The lines nested under the first line matching `head`, which is included.
function blockAt(lines, start) {
  const out = [lines[start]];
  const depth = indentOf(lines[start]);
  for (let i = start + 1; i < lines.length; i++) {
    if (!isBlank(lines[i]) && indentOf(lines[i]) <= depth) { break; }
    out.push(lines[i]);
  }
  return out;
}

function block(text, head) {
  const lines = text.split('\n');
  const start = lines.findIndex((l) => head.test(l));
  return start < 0 ? null : blockAt(lines, start);
}

// Job name to its lines, for the jobs under the top-level `jobs:` key.
function jobsOf(text) {
  const jobs = new Map();
  const lines = block(text, /^jobs:\s*$/) ?? [];
  for (let i = 1; i < lines.length; i++) {
    const m = lines[i].match(/^ {2}([\w-]+):\s*$/);
    if (m) { jobs.set(m[1], blockAt(lines, i)); }
  }
  return jobs;
}

// Each step of a job as its text, comments dropped.
function stepsOf(job) {
  const start = job.findIndex((l) => /^\s+steps:\s*$/.test(l));
  if (start < 0) { return []; }
  const itemIndent = indentOf(job[start]) + 2;
  const isItem = (line) => /^\s*- /.test(line) && indentOf(line) === itemIndent;
  const steps = [];
  for (const line of blockAt(job, start).slice(1).filter((l) => !isBlank(l))) {
    if (isItem(line)) { steps.push(''); }
    if (steps.length) { steps[steps.length - 1] += `${line}\n`; }
  }
  return steps;
}

const meaningful = (lines) => (lines ?? []).filter((l) => !isBlank(l)).map((l) => l.replace(/\s+#.*$/, '').trimEnd());
const usesOf = (step) => step.match(/uses:\s*(\S+)/)?.[1] ?? null;
const inDocsSite = (step) => /working-directory:\s*['"]?\.?\/?docs-site['"]?\s*$/m.test(step)
  || /--prefix[ =]['"]?\.?\/?docs-site\b/.test(step) || /\bcd\s+['"]?\.?\/?docs-site\b/.test(step);
const runsNpmCi = (step) => /\bnpm\s+ci\b/.test(step) && inDocsSite(step);
const runsDocsBuild = (step) => /\bdocs-site\/build\.sh\b/.test(step)
  || (inDocsSite(step) && (/\bnpm\s+(?:[^\n]*\s)?run\s+build\b/.test(step) || /\bbuild\.sh\b/.test(step)));

function indexWhere(steps, pred, what) {
  const i = steps.findIndex(pred);
  assert.ok(i >= 0, `no step ${what}`);
  return i;
}

// The full docs build in a job: Node 24 and the .NET SDK set up by pinned actions, npm ci
// against the lockfile in docs-site/, then the build script, which generates the API with
// docfx, cleans it up and runs the VitePress build.
function assertRunsFullDocsBuild(steps) {
  const node = indexWhere(steps, (s) => /^actions\/setup-node@/.test(usesOf(s) ?? ''), 'uses actions/setup-node');
  assert.match(steps[node], /node-version:\s*['"]?24(?:\.[\dx*]+)*['"]?\s*$/m, 'setup-node must install Node 24');
  const dotnet = indexWhere(steps, (s) => /^actions\/setup-dotnet@/.test(usesOf(s) ?? ''), 'uses actions/setup-dotnet');
  const ci = indexWhere(steps, runsNpmCi, "runs 'npm ci' in docs-site/");
  const build = indexWhere(steps, runsDocsBuild, 'runs the docs build script');
  assert.ok(node < ci && ci < build, 'Node is set up and npm ci runs before the docs build');
  assert.ok(dotnet < build, 'the .NET SDK is set up before the docs build (docfx needs it)');
  assert.ok(!steps.some((s) => /\bnpm\s+(?:install|i)\b/.test(s)), 'installs use npm ci, not npm install');
  return build;
}

// Third-party actions are pinned by full commit SHA, with the version in a comment on the line or
// just above it, as site.yml and ci.yml already write them. hashira is the one exception: every
// skathio repo tracks its moving major tag, `v3`, whose v* tags hashira protects from being
// deleted or moved by anyone but its admins.
function assertThirdPartyPinned(job) {
  job.forEach((line, i) => {
    const uses = line.match(/^\s*-?\s*uses:\s*(\S+)/)?.[1];
    if (!uses || uses.startsWith('./')) { return; }
    if (uses.startsWith('skathio/hashira/')) {
      assert.match(uses, /@v3$/, `${uses} must track hashira's v3 tag`);
      return;
    }
    assert.match(uses, /@[0-9a-f]{40}$/, `${uses} must be pinned by full commit SHA`);
    const comment = `${line.match(/\s#(.*)$/)?.[1] ?? ''} ${job[i - 1]?.trim().startsWith('#') ? job[i - 1] : ''}`;
    assert.match(comment, VERSION, `${uses} needs a version comment`);
  });
}

// A failing build must fail the job: no continue-on-error, no swallowed exit status.
function assertFailureFails(job, step) {
  assert.ok(!job.some((l) => /^\s*continue-on-error:\s*(?!false)/.test(l)), 'the job must not continue on error');
  assert.doesNotMatch(step, /\|\|\s*(?:true|:|exit 0)\b|;\s*true\s*$|set \+e/m, 'the build step must not swallow a failure');
}

// ---------------------------------------------------------------- site.yml

describe('site.yml', () => {
  const now = read(SITE_YML);
  const then = atHead(SITE_YML);
  const build = jobsOf(now).get('build');

  test('the deploy keeps its current triggers: a release or a manual run, never a push', () => {
    assert.deepEqual(meaningful(block(now, /^on:\s*$/)), meaningful(block(then, /^on:\s*$/)));
    assert.doesNotMatch(now, /^\s{2}(?:push|pull_request|pull_request_target|schedule|release):/m);
  });

  test('a manual run still waits for the production approval at gate', () => {
    assert.deepEqual(meaningful(jobsOf(now).get('gate')), meaningful(jobsOf(then).get('gate')));
    assert.match(jobsOf(now).get('gate').join('\n'), /environment:\s*production/);
  });

  test('the deploy job and its pin are unchanged', () => {
    assert.deepEqual(meaningful(jobsOf(now).get('deploy')), meaningful(jobsOf(then).get('deploy')));
  });

  test('publish.yml still deploys the site with each release, without a second approval', () => {
    const job = (t) => meaningful(jobsOf(t).get('site'));
    assert.deepEqual(job(read(PUBLISH_YML)), job(atHead(PUBLISH_YML)));
  });

  test('the build job runs the full docs build, then uploads its folder instead of site/', () => {
    assert.ok(build, 'site.yml has no build job');
    assert.match(build.join('\n'), /^\s+needs:\s*gate\s*$/m, 'build still waits for gate');
    const steps = stepsOf(build);
    const built = assertRunsFullDocsBuild(steps);
    const upload = indexWhere(steps, (s) => /pages-upload@/.test(usesOf(s) ?? ''), 'uploads with pages-upload');
    assert.ok(built < upload, 'the docs are built before the upload');
    const path = uploadPath();
    assert.ok(path, 'pages-upload has no path');
    assert.notEqual(resolve(root, path), resolve(root, 'site'), 'the upload must be the assembled folder, not site/');
    assertFailureFails(build, steps[built]);
  });

  test('hashira rides its v3 tag and the other third-party actions stay pinned by SHA', () => {
    assertThirdPartyPinned(build);
    const hashira = [...now.matchAll(/skathio\/hashira\/[^@\s]+@(\S+)/g)].map((m) => m[1]);
    assert.equal(hashira.length, 2, 'site.yml uploads and deploys with hashira');
    assert.deepEqual([...new Set(hashira)], ['v3'], 'every hashira ref in site.yml is v3');
  });
});

// The folder site.yml uploads, relative to the repo root.
function uploadPath() {
  const steps = stepsOf(jobsOf(read(SITE_YML)).get('build') ?? []);
  const upload = steps.find((s) => /pages-upload@/.test(usesOf(s) ?? ''));
  const raw = upload?.match(/^\s+path:\s*(.+?)\s*$/m)?.[1];
  return raw ? raw.replace(/^['"]|['"]$/g, '').replace(/^\$\{\{\s*github\.workspace\s*\}\}\/?/, '') : null;
}

// ---------------------------------------------------------------- ci.yml

describe('ci.yml docs job', () => {
  const text = read(CI_YML);
  const docsJobs = [...jobsOf(text)].filter(([, job]) => stepsOf(job).some(runsDocsBuild));

  test('a job runs the full docs build: API generation, cleanup and VitePress build', () => {
    assert.equal(docsJobs.length, 1, 'expected one ci.yml job that runs the docs build');
    assertRunsFullDocsBuild(stepsOf(docsJobs[0][1]));
  });

  test('it runs on every pull request', () => {
    const on = meaningful(block(text, /^on:\s*$/)).join('\n');
    assert.match(on, /^\s+pull_request:/m);
    assert.doesNotMatch(on, /paths(?:-ignore)?:/, 'the pull_request trigger must not filter by path');
    assert.ok(docsJobs.length === 1, 'no docs job');
    assert.ok(!docsJobs[0][1].some((l) => /^ {4}if:/.test(l)), 'the docs job must not be skipped by a condition');
  });

  test('a dead link or a build error fails the job', () => {
    assert.ok(docsJobs.length === 1, 'no docs job');
    const [, job] = docsJobs[0];
    assertFailureFails(job, stepsOf(job).find(runsDocsBuild));
  });

  test('its third-party actions are pinned by full commit SHA', () => {
    assert.ok(docsJobs.length === 1, 'no docs job');
    assertThirdPartyPinned(docsJobs[0][1]);
  });
});

// ---------------------------------------------------------------- the assembled folder

function* walk(dir) {
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    const p = join(dir, e.name);
    if (e.isDirectory()) { yield* walk(p); } else { yield p; }
  }
}

function copyWorkingTree() {
  const tmp = mkdtempSync(join(tmpdir(), 'rogue-pages-'));
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

describe('the assembled Pages folder', () => {
  const state = { tmp: null, build: null };

  before(() => {
    state.tmp = copyWorkingTree();
    state.build = spawnSync('bash', ['docs-site/build.sh'], { cwd: state.tmp, encoding: 'utf8', timeout: 1_500_000 });
  });

  after(() => { if (state.tmp) { rmSync(state.tmp, { recursive: true, force: true }); } });

  function pages() {
    assert.equal(state.build.status, 0, `docs-site/build.sh failed:\n${state.build.stdout}\n${state.build.stderr}`);
    const path = uploadPath();
    assert.ok(path, 'site.yml names no upload folder');
    const dir = resolve(state.tmp, path);
    assert.ok(existsSync(dir), `docs-site/build.sh did not assemble ${path}, the folder site.yml uploads`);
    return dir;
  }

  function docsPage(dir, rel) {
    const file = join(dir, 'docs', rel);
    assert.ok(existsSync(file), `docs/${rel} is missing from the assembled folder`);
    return readFileSync(file, 'utf8');
  }

  test('docs-site/build.sh, run from the repo root of a clean checkout, exits 0', () => {
    assert.equal(state.build.status, 0, `exit ${state.build.status}:\n${state.build.stdout}\n${state.build.stderr}`);
  });

  test('every site/ file sits at the root, byte for byte', () => {
    const dir = pages();
    for (const f of walk(join(state.tmp, 'site'))) {
      const rel = relative(join(state.tmp, 'site'), f);
      assert.ok(existsSync(join(dir, rel)), `${rel} is missing from the root`);
      assert.deepEqual(readFileSync(join(dir, rel)), readFileSync(f), `${rel} at the root is not the site/ file`);
    }
  });

  test('the root holds the site/ files and docs/, nothing else', () => {
    const dir = pages();
    assert.ok(!existsSync(join(state.tmp, 'site', 'docs')), 'site/ has its own docs entry, which would collide');
    const expected = [...readdirSync(join(state.tmp, 'site')), 'docs'].sort();
    assert.deepEqual(readdirSync(dir).sort(), expected);
  });

  test('the VitePress output is under docs/: Getting started and the ISender API page', () => {
    const dir = pages();
    const index = docsPage(dir, 'index.html');
    assert.match(index, /\/rogue\/docs\/assets\//, 'docs/index.html is not the VitePress build');
    assert.ok(existsSync(join(dir, 'docs', 'api', 'skathio-rogue-isender.html')), 'docs/api/skathio-rogue-isender.html is missing');
    assert.ok(existsSync(join(dir, 'docs', 'jobs.html')), 'docs/jobs.html is missing');
  });

  test('every /rogue/docs/ asset the docs pages load is in the folder', () => {
    const dir = pages();
    const pagesHtml = ['index.html', join('api', 'skathio-rogue-isender.html')];
    for (const page of pagesHtml) {
      const html = docsPage(dir, page);
      const refs = [...html.matchAll(/(?:src|href)="\/rogue\/docs\/((?:assets\/)[^"#?]+)"/g)].map((m) => m[1]);
      assert.ok(refs.length > 0, `${page} loads no /rogue/docs/assets/ file`);
      for (const ref of refs) {
        assert.ok(existsSync(join(dir, 'docs', ref.split('/').join(sep))), `${page} loads /rogue/docs/${ref}, which is missing`);
      }
    }
  });

  test('the landing page at the root is the site/ page, not a docs page', () => {
    const dir = pages();
    const landing = readFileSync(join(dir, 'index.html'), 'utf8');
    assert.match(landing, /<title>Rogue<\/title>/);
    assert.doesNotMatch(landing, /\/rogue\/docs\/assets\//);
  });
});
