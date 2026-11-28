// Cases the hashira v3.4.0 adoption left open, found by checking it against the request:
// - clean-api.mjs --sidebar must fail with a clear message, and write no sidebar, when pages.json
//   or toc.yml is not of the shape it reads (a build fails rather than fall back), instead of a
//   stack trace or a sidebar made of the letters of a string;
// - a toc.yml that names no namespace would leave every API page out of the sidebar, so it fails;
// - pre-build.sh stops before it restores anything when DOCS_STAGE is not set;
// - publish.yml says what happens to the site deploy when the approval comes after the prebuilt
//   site's artifact has expired.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'

const root = resolve(import.meta.dirname, '..', '..')
const script = join(import.meta.dirname, 'clean-api.mjs')
const read = (rel) => readFileSync(join(root, rel), 'utf8')

const TOC = '- name: SkathIO.Rogue\n  href: SkathIO.Rogue.md\n'

// Runs clean-api.mjs --sidebar on one namespace page with `toc` as toc.yml and `pages` as the
// text of pages.json. Returns the run and whether a sidebar file was written.
function runSidebar({ toc = TOC, pages = '{}' }) {
  const dir = mkdtempSync(join(tmpdir(), 'clean-api-gaps-'))
  try {
    mkdirSync(join(dir, 'in'))
    writeFileSync(join(dir, 'in', 'SkathIO.Rogue.md'), '# Namespace SkathIO.Rogue\n')
    writeFileSync(join(dir, 'in', 'toc.yml'), toc)
    writeFileSync(join(dir, 'pages.json'), pages)
    const args = [script, join(dir, 'in'), join(dir, 'out'), '--sidebar', join(dir, '.sidebar.json'), join(dir, 'pages.json')]
    const result = spawnSync(process.execPath, args, { encoding: 'utf8', timeout: 60_000 })
    return { result, wrote: existsSync(join(dir, '.sidebar.json')) }
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
}

function assertClearFailure({ result, wrote }, message) {
  assert.notEqual(result.status, 0, `it passed: ${result.stdout}`)
  assert.match(result.stderr, message)
  assert.doesNotMatch(result.stderr, /\n\s+at /, 'the error is a stack trace')
  assert.equal(wrote, false, 'a sidebar was written')
}

test('--sidebar on the smallest good input writes the one namespace group', () => {
  const { result, wrote } = runSidebar({})
  assert.equal(result.status, 0, result.stderr)
  assert.ok(wrote)
})

for (const [what, pages] of [['a string', '{"sidebar":"abc"}'], ['an object', '{"sidebar":{"file":"a.md"}}'], ['a number', '{"sidebar":3}']]) {
  test(`--sidebar fails and names pages.json when its sidebar is ${what}, not a list`, () => {
    assertClearFailure(runSidebar({ pages }), /^clean-api: .*pages\.json.* sidebar must be a list/)
  })
}

for (const [what, pages] of [['null', 'null'], ['a list', '[]'], ['a string', '"x"']]) {
  test(`--sidebar fails and names pages.json when the file holds ${what}, not an object`, () => {
    assertClearFailure(runSidebar({ pages }), /^clean-api: .*pages\.json must hold an object/)
  })
}

test('--sidebar fails when toc.yml names no namespace, which would leave the API pages out of the sidebar', () => {
  for (const toc of ['', '### YamlMime:TableOfContent\n']) {
    assertClearFailure(runSidebar({ toc }), /^clean-api: toc\.yml .*no namespace/)
  }
})

test('--sidebar fails and names the value when toc.yml holds a quoted name it cannot read', () => {
  assertClearFailure(runSidebar({ toc: '- name: "SkathIO\n  href: SkathIO.Rogue.md\n' }), /^clean-api: toc\.yml .*"SkathIO/)
})

test('pre-build.sh stops with a message, before any restore, when DOCS_STAGE is not set or empty', () => {
  for (const stage of [undefined, '']) {
    const { DOCS_STAGE: _, ...env } = process.env
    const run = spawnSync('bash', ['tools/docs-api/pre-build.sh'], {
      cwd: root, env: stage === undefined ? env : { ...env, DOCS_STAGE: stage }, encoding: 'utf8', timeout: 60_000,
    })
    assert.notEqual(run.status, 0)
    assert.match(run.stderr, /pre-build: DOCS_STAGE is not set/)
    assert.equal(run.stdout, '', 'a build step ran before the check')
  }
})

test('publish.yml says the prebuilt site expires, and how to deploy when the approval comes later', () => {
  const comments = read('.github/workflows/publish.yml').split('\n').filter((l) => l.trim().startsWith('#')).join(' ').replace(/\s*#\s*/g, ' ')
  assert.match(comments, /kept for (?:one|1) day/)
  assert.match(comments, /run site\.yml by hand/)
})

// Neither main nor this branch makes the phone landing wider than its viewport any more (the
// tables scroll inside their own box), so a width warning is never an expected one.
test('the visual-test skill no longer calls a 647px-wide phone page a known baseline', () => {
  const skill = read('.claude/skills/visual-test/SKILL.md')
  assert.doesNotMatch(skill, /647px/)
  assert.match(skill.replace(/\s+/g, ' '), /No width warning is expected/)
})

// The release-only path that publish.yml's comment names must also reach the people who run a
// release: CLAUDE.md's release bullet and the release-prep skill.
test('CLAUDE.md and the release-prep skill say the prebuilt site expires in one day, and the recovery', () => {
  for (const file of ['CLAUDE.md', '.claude/skills/release-prep/SKILL.md']) {
    const text = read(file).replace(/\s+/g, ' ')
    assert.match(text, /kept for (?:one|1) day/, `${file} does not give the one-day limit`)
    assert.match(text, /run `?site\.yml`? by hand/, `${file} does not give the recovery`)
    assert.match(text, /approved in (?:the )?`?production`?/, `${file} does not say the hand run is approved in production`)
  }
})

test('CLAUDE.md no longer calls a 647px-wide phone page a known issue', () => {
  assert.doesNotMatch(read('CLAUDE.md'), /647px/)
})

// A label is cut to docs-build's longest sidebar label; the cut must not split a UTF-16 surrogate
// pair, or the label holds a lone surrogate.
test('--sidebar cuts a long label without splitting a surrogate pair', () => {
  const name = `${'A'.repeat(58)}\u{1D49C}AAA`
  const dir = mkdtempSync(join(tmpdir(), 'clean-api-gaps-'))
  try {
    mkdirSync(join(dir, 'in'))
    writeFileSync(join(dir, 'in', 'SkathIO.Rogue.md'), '# Namespace SkathIO.Rogue\n')
    writeFileSync(join(dir, 'in', 'toc.yml'), `- name: ${name}\n  href: SkathIO.Rogue.md\n`)
    writeFileSync(join(dir, 'pages.json'), '{}')
    const args = [script, join(dir, 'in'), join(dir, 'out'), '--sidebar', join(dir, '.sidebar.json'), join(dir, 'pages.json')]
    const result = spawnSync(process.execPath, args, { encoding: 'utf8', timeout: 60_000 })
    assert.equal(result.status, 0, result.stderr)
    const [group] = JSON.parse(readFileSync(join(dir, '.sidebar.json'), 'utf8'))
    assert.ok(group.label.isWellFormed(), `label holds a lone surrogate: ${JSON.stringify(group.label)}`)
    assert.ok(group.label.length <= 60, `label is ${group.label.length} long`)
    assert.ok(group.label.endsWith('…'))
    assert.ok(group.label.startsWith('A'.repeat(58)))
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})
