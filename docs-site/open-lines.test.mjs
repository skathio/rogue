// Lines of the request that a second check of the work found open: the docs tests run in CI,
// no doc comment on a public member cites a planning note under any id form, heading
// permalinks are labelled with the heading as it reads, and every cref in the API projects
// resolves, so a reference to one of our own members is a link.
//
// The build cases copy the working tree (tracked and untracked files, nothing ignored) into a
// temporary folder and run docs-site/build.sh there, so they never race another build.
import { test, before, after } from 'node:test'
import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { cpSync, existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, symlinkSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'

const docsSite = import.meta.dirname
const root = join(docsSite, '..')

function git(...args) {
  return spawnSync('git', ['-C', root, ...args], { encoding: 'utf8' })
}

const sourceFiles = () =>
  git('ls-files', '-co', '--exclude-standard', '-z', '--', 'src').stdout.split('\0').filter((f) => f.endsWith('.cs'))

// ---------------------------------------------------------------- ci.yml

test('the ci.yml docs job runs the docs tests the way the request writes the command', () => {
  const ci = readFileSync(join(root, '.github/workflows/ci.yml'), 'utf8')
  const job = ci.match(/^ {2}docs:\n(?:(?: {4}.*)?\n)+/m)?.[0] ?? ''
  assert.match(job, /^ {8}run: docs-site\/build\.sh$/m, 'the docs job was not found')
  assert.match(job, /^ {8}run: node --test docs-site\/$/m)
  assert.ok(job.indexOf('run: npm ci') < job.indexOf('run: node --test docs-site/'), 'the tests need the installed toolchain')
})

// ---------------------------------------------------------------- planning ids

// Planning-note ids in the forms the first sweep did not look for. The prefixes are assembled
// from parts so this file does not itself carry one.
const PLANNING_ID = new RegExp(`\\b(?:${['A' + 'C', 'F' + 'R'].join('|')})-[A-Z0-9]|\\b(?:post-)?D[0-9]+[a-z]?\\b|\\bPhase [0-9]`)

// The /// blocks that document a public member: the block sits right above a declaration that
// starts with `public` (attributes may come between), in a file whose own type is public.
function publicDocBlocks(file) {
  const lines = readFileSync(join(root, file), 'utf8').split('\n')
  if (!lines.some((l) => /^public\b/.test(l))) {
    return []
  }
  const blocks = []
  let start = -1
  lines.forEach((line, i) => {
    const doc = line.trimStart().startsWith('///')
    if (doc && start < 0) {
      start = i
    } else if (!doc && start >= 0) {
      const declaration = lines.slice(i).find((l) => l.trim() !== '' && !l.trimStart().startsWith('['))
      if (/^\s*public\b/.test(declaration ?? '')) {
        blocks.push({ at: `${file}:${start + 1}`, text: lines.slice(start, i).join('\n') })
      }
      start = -1
    }
  })
  return blocks
}

test('the planning-id pattern finds each form and leaves prose alone', () => {
  for (const hit of ['the post-D' + '5 contract', 'the A' + 'C-F gate', 'the F' + 'R-25 wrap', 'D' + '3 carve-out', 'Phase ' + '2']) {
    assert.match(hit, PLANNING_ID)
  }
  for (const prose of ['ROGM006', 'a 3D point', 'DI container', 'net10.0', 'phase two']) {
    assert.doesNotMatch(prose, PLANNING_ID)
  }
})

test('no doc comment on a public member cites a planning note', () => {
  const blocks = sourceFiles().flatMap(publicDocBlocks)
  assert.ok(blocks.length > 100, `only ${blocks.length} public doc comments found, so the scan would pass vacuously`)
  const hits = blocks.filter((b) => PLANNING_ID.test(b.text)).map((b) => `${b.at}\n${b.text}`)
  assert.deepEqual(hits, [])
})

// ---------------------------------------------------------------- the built API pages

const state = { tmp: null, build: null }

function copyWorkingTree() {
  const tmp = mkdtempSync(join(tmpdir(), 'rogue-open-lines-'))
  const files = git('ls-files', '-co', '--exclude-standard', '-z').stdout.split('\0').filter(Boolean)
  for (const f of files) {
    const from = join(root, f)
    if (!existsSync(from) || lstatSync(from).isDirectory()) {
      continue
    }
    mkdirSync(dirname(join(tmp, f)), { recursive: true })
    cpSync(from, join(tmp, f))
  }
  symlinkSync(join(docsSite, 'node_modules'), join(tmp, 'docs-site', 'node_modules'))
  return tmp
}

before(() => {
  state.tmp = copyWorkingTree()
  state.build = spawnSync('bash', [join(state.tmp, 'docs-site', 'build.sh')], { cwd: state.tmp, encoding: 'utf8', timeout: 1_500_000 })
})

after(() => {
  if (state.tmp) {
    rmSync(state.tmp, { recursive: true, force: true })
  }
})

function apiDir() {
  const dir = join(state.tmp, 'artifacts', 'pages', 'docs', 'api')
  assert.ok(existsSync(dir), `no built API pages; build output:\n${state.build?.stdout}\n${state.build?.stderr}`)
  return dir
}

const apiPage = (name) => readFileSync(join(apiDir(), name), 'utf8')

function decode(s) {
  return s.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&#39;/g, "'").replace(/&amp;/g, '&')
}

const permalinkLabels = (html) => [...html.matchAll(/class="header-anchor"[^>]*aria-label="([^"]*)"/g)].map((m) => decode(m[1]))

test('a heading permalink is labelled with the heading as it reads, generics included', () => {
  const labels = permalinkLabels(apiPage('skathio-rogue-isender.html'))
  assert.ok(labels.includes('Permalink to "Interface ISender"'), labels.join('\n'))
  assert.ok(labels.includes('Permalink to "CreateStream<TItem>(IStreamQuery<TItem>, CancellationToken)"'), labels.join('\n'))
  assert.ok(permalinkLabels(apiPage('skathio-rogue-icommand-1.html')).includes('Permalink to "Interface ICommand<TResponse>"'))
})

test('no API heading permalink label carries a Markdown backslash', () => {
  const pages = readdirSync(apiDir()).filter((f) => f.endsWith('.html'))
  const labels = pages.flatMap((f) => permalinkLabels(apiPage(f)))
  assert.ok(labels.length > pages.length, 'too few permalink labels were found')
  assert.deepEqual(labels.filter((l) => l.includes('\\')), [])
})

test('a guide heading permalink is labelled without its Markdown code marks', () => {
  const labels = permalinkLabels(readFileSync(join(apiDir(), '..', 'jobs.html'), 'utf8'))
  assert.ok(labels.length > 0)
  assert.deepEqual(labels.filter((l) => l.includes('`')), [])
})

test('docfx resolves every cref in the API projects', () => {
  apiDir()
  const unresolved = (state.build.stdout + state.build.stderr).split('\n').filter((l) => l.includes('InvalidCref'))
  assert.deepEqual(unresolved, [])
})

test('a reference to an option another of our types declares is a link to that type', () => {
  const html = apiPage('skathio-rogue-jobs-postgres-postgresjobstore.html')
  assert.match(html, /unless <a href="[^"]*skathio-rogue-jobs-databasejobsoptions[^"]*">DatabaseJobsOptions\.AutoMigrate<\/a> is off/)
  assert.match(html, /calls it every <a href="[^"]*skathio-rogue-jobs-databasejobsoptions[^"]*">DatabaseJobsOptions\.PruneInterval<\/a>/)
})

test('a reference to a member of a type outside our packages is inline code', () => {
  assert.match(apiPage('skathio-rogue-roguetelemetry.html'), /; <code>typeof\(TRequest\)\.Name<\/code> tags the activity/)
})
