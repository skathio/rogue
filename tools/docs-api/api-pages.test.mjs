// The API reference the docs pre-build writes: site.yml's docs_pre_build_command is run once the
// way hashira's docs-build runs it (bash -eo pipefail from the repo root, DOCS_STAGE set to an
// empty folder), and the Markdown pages it leaves under $DOCS_STAGE/api are checked: every public
// type of the 12 packages has a page, generic names keep their angle brackets, method headings sit
// at the levels the docs outline shows, links between pages resolve, types outside our packages
// are inline code, and docfx resolves every cref. Nothing generated is committed.
import { test, before, after } from 'node:test'
import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'

const root = resolve(import.meta.dirname, '..', '..')
const state = { stage: null, run: null }

// site.yml's docs_pre_build_command: the `|` block under that key.
function preBuildCommand() {
  const lines = readFileSync(join(root, '.github/workflows/site.yml'), 'utf8').split('\n')
  const at = lines.findIndex((l) => /^\s*docs_pre_build_command:\s*\|\s*$/.test(l))
  assert.ok(at >= 0, 'site.yml has no docs_pre_build_command block')
  const depth = lines[at].match(/^ */)[0].length
  const end = lines.findIndex((l, i) => i > at && l.trim() !== '' && l.match(/^ */)[0].length <= depth)
  return lines.slice(at + 1, end < 0 ? undefined : end).join('\n')
}

before(() => {
  state.stage = mkdtempSync(join(tmpdir(), 'rogue-api-pages-'))
  state.run = spawnSync('bash', ['-eo', 'pipefail', '-c', preBuildCommand()], {
    cwd: root, env: { ...process.env, DOCS_STAGE: state.stage }, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024, timeout: 1_500_000,
  })
})

after(() => {
  if (state.stage) {
    rmSync(state.stage, { recursive: true, force: true })
  }
})

function apiDir() {
  const dir = join(state.stage, 'api')
  assert.equal(state.run.status, 0, `the pre-build failed:\n${state.run.stdout.slice(-4000)}\n${state.run.stderr.slice(-4000)}`)
  assert.ok(existsSync(dir), 'the pre-build wrote no api/ folder')
  return dir
}

const apiPages = () => readdirSync(apiDir()).filter((f) => f.endsWith('.md'))
const page = (name) => readFileSync(join(apiDir(), name), 'utf8')
const unescapeMd = (text) => text.replace(/\\([!-/:-@[-`{-~])/g, '$1')
const prose = (body) => body.replace(/^```[\s\S]*?^```$/gm, '')

// The ATX headings outside fenced code, as a reader sees them.
function headings(name) {
  return [...prose(page(name)).matchAll(/^(#{1,6}) (.+)$/gm)].map((m) => ({ level: m[1].length, text: unescapeMd(m[2].trim()) }))
}

const h1 = (name) => headings(name).find((h) => h.level === 1)?.text ?? null

// The last dot-separated segment of a type name, ignoring dots inside generic arguments.
function lastSegment(name) {
  let depth = 0
  for (let i = name.length - 1; i >= 0; i--) {
    if (name[i] === '>') {
      depth++
    } else if (name[i] === '<') {
      depth--
    } else if (name[i] === '.' && depth === 0) {
      return name.slice(i + 1)
    }
  }
  return name
}

// The public types each package declares, from its PublicAPI files.
function publicTypes() {
  const projects = readdirSync(join(root, 'src')).filter((p) => existsSync(join(root, 'src', p, 'PublicAPI.Shipped.txt')))
  return projects.map((project) => {
    const read = (f) => (existsSync(join(root, 'src', project, f)) ? readFileSync(join(root, 'src', project, f), 'utf8').split('\n') : [])
    const typeLines = (lines) => lines.map((l) => l.trim()).filter((l) => l && !l.startsWith('#') && !l.includes('->'))
    const unshipped = typeLines(read('PublicAPI.Unshipped.txt'))
    const removed = new Set(unshipped.filter((l) => l.startsWith('*REMOVED*')).map((l) => l.slice(9)))
    const added = unshipped.filter((l) => !l.startsWith('*REMOVED*'))
    return { project, types: [...typeLines(read('PublicAPI.Shipped.txt')), ...added].filter((t) => !removed.has(t)) }
  })
}

// Types hidden from IntelliSense with [EditorBrowsable(Never)] may or may not get a page.
const HIDDEN_TYPE = /\[EditorBrowsable\(EditorBrowsableState\.Never\)\]\s*(?:\[[^\]]*\]\s*)*(?:(?:public|static|sealed|abstract|partial|readonly)\s+)*(?:class|struct|interface|enum|record(?:\s+struct|\s+class)?)\s+(\w+)/g

function hiddenTypes(project) {
  const listed = spawnSync('git', ['-C', root, 'ls-files', '-co', '--exclude-standard', '-z', '--', `src/${project}`], { encoding: 'utf8' })
  const sources = listed.stdout.split('\0').filter((f) => f.endsWith('.cs'))
  return new Set(sources.flatMap((f) => [...readFileSync(join(root, f), 'utf8').matchAll(HIDDEN_TYPE)].map((m) => m[1])))
}

test('docfx is a local dotnet tool at 2.81.0 and writes Markdown for net10.0', () => {
  const manifest = JSON.parse(readFileSync(join(root, '.config', 'dotnet-tools.json'), 'utf8'))
  assert.equal(manifest.tools?.docfx?.version, '2.81.0')
  const entries = JSON.parse(readFileSync(join(import.meta.dirname, 'docfx.json'), 'utf8')).metadata
  assert.ok(entries.length > 0, 'docfx.json has no metadata section')
  for (const e of entries) {
    assert.equal(e.outputFormat, 'markdown')
    assert.equal(e.properties?.TargetFramework, 'net10.0')
  }
})

test('the pre-build writes only Markdown pages under api/, starting with the SkathIO.Rogue namespace and ISender', () => {
  assert.deepEqual(readdirSync(state.stage), ['api'])
  assert.deepEqual(readdirSync(apiDir()).filter((f) => !f.endsWith('.md')), [])
  assert.equal(h1('skathio-rogue.md'), 'Namespace SkathIO.Rogue')
  assert.equal(h1('skathio-rogue-isender.md'), 'Interface ISender')
})

test('every public type of the 12 packages with a PublicAPI.Shipped.txt has a page', () => {
  const projects = publicTypes()
  assert.equal(projects.length, 12)
  const titles = apiPages().map(h1).filter(Boolean)
  for (const { project, types } of projects) {
    assert.ok(types.length > 0, `${project} lists no public type`)
    const hidden = hiddenTypes(project)
    for (const t of types.filter((x) => !hidden.has(lastSegment(x).replace(/<.*$/, '')))) {
      const name = lastSegment(t)
      assert.ok(titles.some((h) => h.endsWith(` ${name}`) || h.endsWith(`.${name}`)), `${project}: no API page for ${t}`)
    }
  }
})

test('every page opens with one H1 and no front matter, and generic names keep their angle brackets', () => {
  for (const name of apiPages()) {
    assert.match(page(name), /^# \S/, `${name} does not open with its H1`)
    assert.equal(headings(name).filter((h) => h.level === 1).length, 1, `${name} has more than one H1`)
    // docs-build labels the sidebar with the H1 as written, escapes included.
    assert.doesNotMatch(page(name).split('\n')[0], /\\/, `${name}: the H1 holds a Markdown escape`)
  }
  assert.ok(apiPages().map(h1).includes('Interface ICommand<TResponse>'), 'no H1 "Interface ICommand<TResponse>"')
  const isender = headings('skathio-rogue-isender.md')
  assert.ok(isender.some((h) => h.text === 'CreateStream<TItem>(IStreamQuery<TItem>, CancellationToken)'),
    `ISender headings: ${isender.map((h) => h.text).join(' | ')}`)
})

// hashira's docs outline lists headings at levels 2 and 3.
test('every method heading is at level 2 or 3, so the outline lists it', () => {
  for (const name of apiPages()) {
    for (const h of headings(name).filter((x) => x.text.includes('('))) {
      assert.ok([2, 3].includes(h.level), `${name}: "${h.text}" is an h${h.level}`)
    }
  }
})

test('links between API pages resolve to pages written, and no xref or learn.microsoft.com link is left', () => {
  const written = new Set(apiPages())
  let internal = 0
  for (const name of apiPages()) {
    const body = prose(page(name))
    assert.doesNotMatch(body, /<\/?xref\b/, name)
    assert.doesNotMatch(body, /\]\(https:\/\/learn\.microsoft\.com\//, name)
    for (const [, href] of body.matchAll(/\]\((\.\/[^)\s]*)\)/g)) {
      internal++
      assert.ok(written.has(href.slice(2).split('#')[0]), `${name} links to ${href}, which was not written`)
    }
  }
  assert.ok(internal > 0, 'no API page links to another')
})

test('docfx resolves every cref in the API projects', () => {
  apiDir()
  const unresolved = `${state.run.stdout}\n${state.run.stderr}`.split('\n').filter((l) => l.includes('InvalidCref'))
  assert.deepEqual(unresolved, [])
})

test('a reference to an option another of our types declares is a link to that type', () => {
  const body = page('skathio-rogue-jobs-postgres-postgresjobstore.md')
  assert.match(body, /unless \[DatabaseJobsOptions\.AutoMigrate\]\(\.\/skathio-rogue-jobs-databasejobsoptions\.md\) is off/)
  assert.match(body, /calls it every \[DatabaseJobsOptions\.PruneInterval\]\(\.\/skathio-rogue-jobs-databasejobsoptions\.md\)/)
})

test('a reference to a member of a type outside our packages is inline code', () => {
  assert.match(page('skathio-rogue-roguetelemetry.md'), /; `typeof\(TRequest\)\.Name` tags the activity/)
})

test('no generated API page is committed', () => {
  const tracked = spawnSync('git', ['-C', root, 'ls-files', '-z'], { encoding: 'utf8' }).stdout.split('\0')
  const generated = tracked.filter((f) => /(^|\/)api\/[^/]+\.md$|skathio-rogue-[\w-]*\.md$|^SkathIO\.Rogue[\w.-]*\.md$|(^|\/)toc\.yml$/i.test(f))
  assert.deepEqual(generated, [])
})
