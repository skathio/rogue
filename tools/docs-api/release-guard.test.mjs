// Guards on `released: true`, which deploys the docs with no second approval. site.yml's `check`
// job admits it only from publish.yml run from main, compared exactly (a prefix match would admit
// publish.yml run from any branch), and publish.yml's `site` job, which passes it, runs only on
// main, as the homepage-ping job does.
//
// The check step's script is read from site.yml and run with bash, with its env set the way the
// runner would set it, so the case tests the shipped script, not a copy.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'

const root = resolve(import.meta.dirname, '..', '..')
const REPO = 'skathio/rogue'
const read = (rel) => readFileSync(join(root, rel), 'utf8')

const indentOf = (line) => line.match(/^ */)[0].length

// The lines of the block under the first line matching `header`, to the next line indented no
// deeper than the header (blank lines kept).
function blockUnder (lines, header) {
  const start = lines.findIndex((line) => header.test(line))
  assert.notEqual(start, -1, `no line matches ${header}`)
  const depth = indentOf(lines[start])
  const body = []
  for (const line of lines.slice(start + 1)) {
    if (line.trim() !== '' && indentOf(line) <= depth) {
      break
    }
    body.push(line)
  }
  return body
}

function jobBlock (file, job) {
  const jobs = blockUnder(read(file).split('\n'), /^jobs:\s*$/)
  return blockUnder(jobs, new RegExp(`^  ${job}:\\s*$`))
}

// The check job's env (with ${{ github.repository }} resolved) and run script.
function checkStep () {
  const job = jobBlock('.github/workflows/site.yml', 'check')
  const env = {}
  for (const line of blockUnder(job, /^\s+env:\s*$/)) {
    const pair = line.match(/^\s+([A-Z_]+):\s*(.*)$/)
    if (pair) {
      env[pair[1]] = pair[2].replace(/\$\{\{\s*github\.repository\s*\}\}/g, REPO)
    }
  }
  const run = blockUnder(job, /^\s+run:\s*\|\s*$/)
  const cut = Math.min(...run.filter((l) => l.trim() !== '').map(indentOf))
  return { env, script: run.map((l) => l.slice(cut)).join('\n') }
}

function runCheck (released, workflowRef) {
  const { env: { RELEASED: _r, WORKFLOW_REF: _w, ...env }, script } = checkStep()
  assert.ok(!Object.values(env).some((v) => v.includes('${{')), `unresolved expression in ${JSON.stringify(env)}`)
  return spawnSync('bash', ['-e', '-c', script], {
    env: { PATH: process.env.PATH, ...env, RELEASED: released, WORKFLOW_REF: workflowRef },
    encoding: 'utf8',
  })
}

test('site.yml check refuses released from publish.yml on a branch other than main', () => {
  const result = runCheck('true', `${REPO}/.github/workflows/publish.yml@refs/heads/feat/x`)
  assert.equal(result.status, 1, result.stdout + result.stderr)
})

test('site.yml check refuses released from a workflow whose name only starts like publish.yml', () => {
  const result = runCheck('true', `${REPO}/.github/workflows/publish.yml@refs/heads/main-evil`)
  assert.equal(result.status, 1, result.stdout + result.stderr)
})

test('site.yml check refuses released from a hand run of site.yml', () => {
  const result = runCheck('true', `${REPO}/.github/workflows/site.yml@refs/heads/main`)
  assert.equal(result.status, 1, result.stdout + result.stderr)
})

test('site.yml check admits released from publish.yml on main', () => {
  const result = runCheck('true', `${REPO}/.github/workflows/publish.yml@refs/heads/main`)
  assert.equal(result.status, 0, result.stdout + result.stderr)
})

test('site.yml check admits a run without released from any ref', () => {
  const result = runCheck('false', `${REPO}/.github/workflows/site.yml@refs/heads/feat/x`)
  assert.equal(result.status, 0, result.stdout + result.stderr)
})

test('publish.yml site job, which passes released, runs only on main', () => {
  const job = jobBlock('.github/workflows/publish.yml', 'site')
  const condition = job.find((line) => /^\s+if:/.test(line))
  assert.ok(condition, 'publish.yml site job has no if')
  assert.match(condition, /github\.ref == 'refs\/heads\/main'/)
  assert.ok(job.some((line) => /^\s+released:\s*true\s*$/.test(line)), 'site job no longer passes released: true')
})
