// Cases found when the work was checked against the request line by line: the test command
// as the request writes it, the cleanup command's answers to bad input, and a failed build
// leaving no Pages folder from an earlier run.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { chmodSync, cpSync, existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

const docsSite = import.meta.dirname
const script = join(docsSite, 'clean-api.mjs')

function inTemp(prefix, body) {
  const dir = mkdtempSync(join(tmpdir(), prefix))
  try {
    return body(dir)
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
}

// A runner started from inside a test would otherwise report as that test's child.
function cleanEnv(extra) {
  const { NODE_TEST_CONTEXT: _, ...env } = process.env
  return { ...env, ...extra }
}

// `node --test <folder>/` on a folder that holds docs-site's package.json and test entry
// plus one test file, so the suite does not start itself again.
function runFolder(testBody) {
  return inTemp('rogue-node-test-', (dir) => {
    const folder = join(dir, 'docs-site')
    mkdirSync(folder)
    cpSync(join(docsSite, 'package.json'), join(folder, 'package.json'))
    if (existsSync(join(docsSite, 'run-tests.mjs'))) {
      cpSync(join(docsSite, 'run-tests.mjs'), join(folder, 'run-tests.mjs'))
    }
    writeFileSync(join(folder, 'sample.test.mjs'), `import { test } from 'node:test'\ntest('sample case', () => { ${testBody} })\n`)
    return spawnSync(process.execPath, ['--test', 'docs-site/'], { cwd: dir, env: cleanEnv(), encoding: 'utf8', timeout: 60_000 })
  })
}

test('`node --test docs-site/` from the repo root runs the test files and passes', () => {
  const run = runFolder('')
  assert.equal(run.status, 0, run.stdout + run.stderr)
  assert.match(run.stdout, /sample case/)
})

test('`node --test docs-site/` fails when a test fails', () => {
  const run = runFolder("throw new Error('broken')")
  assert.notEqual(run.status, 0)
  assert.match(run.stdout, /sample case/)
})

const PAGE = '# <a id="SkathIO_Rogue_A"></a> Class A\n'
const TOC = '### YamlMime:TableOfContent\n- name: SkathIO.Rogue\n  items:\n  - name: Classes\n  - name: A\n    href: SkathIO.Rogue.A.md\n'

// Runs the cleanup on a folder made by `setup`, which returns the two arguments.
function clean(setup) {
  return inTemp('clean-api-gaps-', (dir) => {
    const run = spawnSync(process.execPath, [script, ...setup(dir)], { encoding: 'utf8', timeout: 60_000 })
    return { run, sidebar: existsSync(join(dir, 'out', 'sidebar.json')) }
  })
}

function assertClearFailure({ run, sidebar }, message) {
  assert.notEqual(run.status, 0)
  assert.match(run.stderr, message)
  assert.match(run.stderr, /^clean-api: /)
  assert.doesNotMatch(run.stderr, /\n\s+at /, 'the error is a stack trace')
  assert.equal(sidebar, false)
}

test('one argument fails with the usage', () => {
  assertClearFailure(clean((dir) => [join(dir, 'in')]), /usage: node clean-api\.mjs <docfx-md-dir> <out-dir>/)
})

test('a docfx folder that does not exist fails and names it', () => {
  assertClearFailure(clean((dir) => [join(dir, 'in'), join(dir, 'out')]), /cannot read the docfx output folder .*in/)
})

test('a file given as the docfx folder fails and names it', () => {
  const result = clean((dir) => {
    writeFileSync(join(dir, 'in'), PAGE)
    return [join(dir, 'in'), join(dir, 'out')]
  })
  assertClearFailure(result, /cannot read the docfx output folder .*in/)
})

test('a docfx folder with no pages fails instead of writing an empty API', () => {
  const result = clean((dir) => {
    mkdirSync(join(dir, 'in'))
    writeFileSync(join(dir, 'in', 'toc.yml'), '### YamlMime:TableOfContent\n')
    return [join(dir, 'in'), join(dir, 'out')]
  })
  assertClearFailure(result, /holds no docfx Markdown pages/)
})

test('a docfx folder with no toc.yml fails and names the file', () => {
  const result = clean((dir) => {
    mkdirSync(join(dir, 'in'))
    writeFileSync(join(dir, 'in', 'SkathIO.Rogue.A.md'), PAGE)
    return [join(dir, 'in'), join(dir, 'out')]
  })
  assertClearFailure(result, /cannot read docfx's toc\.yml/)
})

test('an output path that is a file fails and names it', () => {
  const result = clean((dir) => {
    mkdirSync(join(dir, 'in'))
    writeFileSync(join(dir, 'in', 'SkathIO.Rogue.A.md'), PAGE)
    writeFileSync(join(dir, 'in', 'toc.yml'), TOC)
    writeFileSync(join(dir, 'out'), '')
    return [join(dir, 'in'), join(dir, 'out')]
  })
  assert.notEqual(result.run.status, 0)
  assert.match(result.run.stderr, /^clean-api: cannot create the output folder .*out/)
  assert.doesNotMatch(result.run.stderr, /\n\s+at /)
})

test('one page and its toc entry is the smallest input that works', () => {
  const result = clean((dir) => {
    mkdirSync(join(dir, 'in'))
    writeFileSync(join(dir, 'in', 'SkathIO.Rogue.A.md'), PAGE)
    writeFileSync(join(dir, 'in', 'toc.yml'), TOC)
    return [join(dir, 'in'), join(dir, 'out')]
  })
  assert.equal(result.run.status, 0, result.run.stderr)
  assert.equal(result.sidebar, true)
})

test('a build that fails leaves no Pages folder from an earlier run', () => {
  inTemp('rogue-stale-pages-', (dir) => {
    for (const folder of ['docs-site/node_modules/vitepress', 'docs', 'site', 'artifacts/pages/docs', 'bin']) {
      mkdirSync(join(dir, folder), { recursive: true })
    }
    cpSync(join(docsSite, 'build.sh'), join(dir, 'docs-site', 'build.sh'))
    writeFileSync(join(dir, 'docs', 'getting-started.md'), '# Getting started\n')
    writeFileSync(join(dir, 'site', 'mark.svg'), '<svg xmlns="http://www.w3.org/2000/svg"/>\n')
    writeFileSync(join(dir, 'artifacts', 'pages', 'docs', 'index.html'), 'the earlier build\n')
    // The API generation is the first step that can fail: a dotnet that always does.
    writeFileSync(join(dir, 'bin', 'dotnet'), '#!/bin/sh\nexit 1\n')
    chmodSync(join(dir, 'bin', 'dotnet'), 0o755)
    const env = cleanEnv({ PATH: `${join(dir, 'bin')}:${process.env.PATH}` })
    const run = spawnSync('bash', ['docs-site/build.sh'], { cwd: dir, env, encoding: 'utf8', timeout: 60_000 })
    assert.notEqual(run.status, 0)
    assert.equal(existsSync(join(dir, 'artifacts', 'pages')), false, 'the earlier Pages folder is still there')
  })
})
