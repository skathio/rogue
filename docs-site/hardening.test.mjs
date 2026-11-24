// Defences added after review: sidebar labels taken from a guide's H1 skip fenced code and are
// safe as HTML; a release builds the docs before anything is pushed; the deploy workflow says
// how to recover a deploy that fails after the push; the site.yml build job, which runs the
// docs toolchain, holds no deploy grants; and CLAUDE.md describes the docs site. The workflow
// cases read the YAML as text, by indentation.
import { test, describe } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { h1Label } from './h1-label.mjs'

const docsSite = import.meta.dirname
const root = join(docsSite, '..')
const read = (rel) => readFileSync(join(root, rel), 'utf8')

// The jobs under the top-level `jobs:` key, each as its text.
function jobsOf(text) {
  const jobs = new Map()
  const body = text.split(/^jobs:\s*$/m)[1] ?? ''
  for (const m of body.matchAll(/^ {2}([\w-]+):\s*\n((?:(?: {4}.*)?\n)*)/gm)) {
    jobs.set(m[1], m[2])
  }
  return jobs
}

// ---------------------------------------------------------------- sidebar labels from H1

describe('h1Label', () => {
  test('takes the first # line', () => {
    assert.equal(h1Label('intro\n\n# Background jobs\n\n# Later\n', 'x.md'), 'Background jobs')
  })

  test('skips a # line inside a backtick or tilde fence', () => {
    assert.equal(h1Label('```bash\n# install it\ndotnet add package X\n```\n\n# Real title\n', 'x.md'), 'Real title')
    assert.equal(h1Label('~~~\n# not a heading\n~~~\n# Real title\n', 'x.md'), 'Real title')
  })

  test('a fence closes only on the same character, at least as long', () => {
    assert.equal(h1Label('````\n```\n# inside\n````\n# Real title\n', 'x.md'), 'Real title')
    assert.equal(h1Label('```\n~~~\n# inside\n```\n# Real title\n', 'x.md'), 'Real title')
    assert.equal(h1Label('```\n``` not a close\n# inside\n```\n# Real title\n', 'x.md'), 'Real title')
  })

  test('an indented fence of up to three spaces still counts', () => {
    assert.equal(h1Label('   ```\n# inside\n   ```\n# Real title\n', 'x.md'), 'Real title')
  })

  test('falls back to the file name when there is no H1, or only one in a fence', () => {
    assert.equal(h1Label('no heading here\n## Second level\n', 'notes.md'), 'notes.md')
    assert.equal(h1Label('```\n# inside\n', 'open.md'), 'open.md')
    assert.equal(h1Label('', 'empty.md'), 'empty.md')
  })

  test('drops code and emphasis marks', () => {
    assert.equal(h1Label('# Messaging (`SkathIO.Rogue.Messaging`) **now**\n', 'x.md'), 'Messaging (SkathIO.Rogue.Messaging) now')
  })

  test('writes & and < as entities, because the sidebar renders its text as HTML', () => {
    assert.equal(h1Label('# Send & publish ICommand<TResponse>\n', 'x.md'), 'Send &amp; publish ICommand&lt;TResponse>')
    assert.equal(h1Label('# <img src=x onerror=alert(1)>\n', 'x.md'), '&lt;img src=x onerror=alert(1)>')
    assert.equal(h1Label('# Already &lt; escaped\n', 'x.md'), 'Already &amp;lt; escaped')
  })

  test('reads Windows line endings', () => {
    assert.equal(h1Label('```\r\n# inside\r\n```\r\n# Real title\r\n', 'x.md'), 'Real title')
  })
})

test('config.mts labels unlisted guides with h1Label, not a match of its own', () => {
  const config = read('docs-site/.vitepress/config.mts')
  assert.match(config, /^import \{ h1Label \} from '\.\.\/h1-label\.mjs'$/m)
  assert.match(config, /h1Label\(readFileSync\(repoPath\(`docs\/\$\{md\}`\), 'utf8'\), md\)/)
  assert.doesNotMatch(config, /\.match\(\/\^# /, 'a bare first-# match ignores fences and HTML')
})

// ---------------------------------------------------------------- publish.yml

describe('publish.yml builds the docs before the push', () => {
  const jobs = jobsOf(read('.github/workflows/publish.yml'))
  const docs = jobs.get('docs') ?? ''
  const gate = "if: github.event_name == 'workflow_dispatch' && github.event.inputs.bump != ''"

  test('a docs job runs npm ci in docs-site/ and then docs-site/build.sh', () => {
    assert.ok(docs, 'publish.yml has no docs job')
    const ci = docs.search(/working-directory: docs-site\n\s+run: npm ci$/m)
    const build = docs.search(/^\s+run: docs-site\/build\.sh$/m)
    assert.ok(ci >= 0, "the docs job does not run 'npm ci' in docs-site/")
    assert.ok(build > ci, 'the docs job does not run docs-site/build.sh after npm ci')
    assert.match(docs, /node-version: '24'/)
    assert.match(docs, /uses: actions\/setup-dotnet@/)
    assert.doesNotMatch(docs, /continue-on-error|\|\|\s*true/, 'a failed docs build must fail the job')
  })

  test('the docs job only reads the repo and keeps no checkout credentials', () => {
    const perms = docs.match(/^ {4}permissions:\n((?: {6}.*\n)+)/m)?.[1] ?? ''
    assert.deepEqual(perms.trim().split('\n').map((l) => l.trim()), ['contents: read'])
    assert.match(docs, /persist-credentials: false/)
    assert.doesNotMatch(docs, /environment:|secrets\./)
  })

  test('its third-party actions are pinned by full commit SHA with a version comment', () => {
    const uses = [...docs.matchAll(/uses: (\S+)(.*)$/gm)]
    assert.ok(uses.length >= 3)
    for (const [, action, rest] of uses) {
      assert.match(action, /@[0-9a-f]{40}$/, `${action} is not pinned by SHA`)
      assert.match(rest, /# v\d+/, `${action} has no version comment`)
    }
  })

  test('it runs on the same gate as publish, and publish needs it', () => {
    assert.ok(docs.includes(gate), 'the docs job does not use the release gate')
    const needs = (jobs.get('publish') ?? '').match(/^ {4}needs: (.+)$/m)?.[1] ?? ''
    const list = needs.replace(/^\[|\]$/g, '').split(',').map((s) => s.trim())
    assert.ok(list.includes('docs'), 'publish does not wait for the docs build')
    assert.ok(list.includes('version'), 'publish still needs the resolved version')
  })
})

// The build job runs npm packages, VitePress and docfx, so it must not hold the OIDC token or
// Pages write. Packaging the folder does not need them: upload-pages-artifact v3.0.1 is a tar
// plus actions/upload-artifact, which authenticates with the runner's own token. Only the
// deploy job, which runs nothing but pages-deploy, keeps the two grants.
describe('site.yml grants', () => {
  const jobs = jobsOf(read('.github/workflows/site.yml'))
  const permsOf = (job) => (job.match(/^ {4}permissions:\n((?: {6}.*\n)+)/m)?.[1] ?? '')
    .trim().split('\n').map((l) => l.replace(/\s+#.*$/, '').trim()).filter(Boolean)

  test('the build job only reads the repo', () => {
    const build = jobs.get('build') ?? ''
    assert.ok(build, 'site.yml has no build job')
    assert.deepEqual(permsOf(build), ['contents: read'])
  })

  test('no job that runs the docs toolchain holds id-token or pages write', () => {
    for (const [name, job] of jobs) {
      if (!/npm ci|docs-site\/build\.sh/.test(job)) { continue }
      assert.doesNotMatch(job, /id-token:\s*write|pages:\s*write/, `${name} runs third-party build code with deploy grants`)
    }
  })

  test('the deploy job keeps the grants deploy-pages needs', () => {
    assert.deepEqual(permsOf(jobs.get('deploy') ?? '').sort(), ['id-token: write', 'pages: write'])
  })
})

test("site.yml's header says how to recover a deploy that fails after the push", () => {
  const header = read('.github/workflows/site.yml').split(/^name:/m)[0]
  assert.match(header, /publish\.yml[\s\S]*`docs` job[\s\S]*before the NuGet push/)
  assert.match(header, /fails after the push[\s\S]*re-run this workflow by hand, approved in `production`/)
})

// ---------------------------------------------------------------- CLAUDE.md

describe('CLAUDE.md describes the docs site', () => {
  const claude = read('CLAUDE.md')

  test('the layout names docs-site/', () => {
    assert.match(claude, /^- `docs-site\/`: the VitePress docs served at `\/rogue\/docs\/`/m)
  })

  test('the build command: npm ci in docs-site, then docs-site/build.sh', () => {
    assert.match(claude, /`npm ci` in `docs-site\/`, then\s+`docs-site\/build\.sh` from the repo root/)
  })

  test('the CI list has the Docs build job', () => {
    assert.match(claude, /`Public API`, `Docs build`/)
  })

  test('site.yml builds the site and deploys the assembled folder', () => {
    assert.match(claude, /`site\.yml` builds the site with `docs-site\/build\.sh` and deploys `artifacts\/pages`/)
  })

  test('the visual test covers the docs route', () => {
    assert.match(claude, /landing page, Getting started and the `ISender` API page/)
    assert.match(claude, /touches `site\/`, `docs\/` or `docs-site\/`/)
  })
})
