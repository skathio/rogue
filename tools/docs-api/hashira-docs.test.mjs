// Rules for building and deploying the docs with hashira's shared Pages flow: site.yml calls
// pages-site.yml@v3 with .NET 10 and a docs_pre_build_command that runs docfx and clean-api.mjs
// into $DOCS_STAGE/api; ci.yml and publish.yml build the docs with the same docs-build action and
// the same command; the API tooling lives in tools/docs-api/ and the old docs folder is gone;
// .github/pages.json keeps /rogue/docs/ and /rogue/docs/api/ where they are; and the workflows
// keep the repo's conventions (deny-all permissions, hashira at the moving v3 tag, every other
// action pinned by SHA, shell commands written in the workflow file).
//
// The workflow cases read the YAML as text, by indentation. One case runs the pre-build command
// from site.yml the way docs-build does (bash -eo pipefail from the repo root, DOCS_STAGE set to
// an empty folder) and checks what it writes, so it restores the solution and runs docfx.
import { test, describe } from 'node:test'
import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync, statSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, relative, resolve, sep } from 'node:path'

const root = resolve(import.meta.dirname, '..', '..')
const THIS_FILE = relative(root, import.meta.filename).split(sep).join('/')
const SITE_YML = '.github/workflows/site.yml'
const CI_YML = '.github/workflows/ci.yml'
const PUBLISH_YML = '.github/workflows/publish.yml'
const PAGES_JSON = '.github/pages.json'
const PAGES_SITE = 'skathio/hashira/.github/workflows/pages-site.yml@v3'
const DOCS_BUILD = 'skathio/hashira/.github/actions/docs-build@v3'
const OLD_DOCS_DIR = ['docs', 'site'].join('-')
// hashira's staged-file rule: only Markdown pages and static files may sit in the docs stage.
const STATIC_FILE = /\.(?:md|png|jpe?g|gif|webp|avif|svg|ico|bmp|woff2?|ttf|otf|pdf|txt|csv)$/i
const PAGES_FIELDS = ['name', 'description', 'base', 'home_page', 'sidebar', 'unpublished', 'nav',
  'home_link', 'accent', 'logo', 'theme_css']
const HREF = /^(?:https:\/\/|http:\/\/|\/|\.\/|\.\.\/|#)/

const read = (rel) => readFileSync(join(root, rel), 'utf8')

// ---------------------------------------------------------------- YAML by indentation

const indentOf = (line) => line.match(/^ */)[0].length
const isBlank = (line) => line.trim() === '' || line.trim().startsWith('#')

// The line at `start` and every line nested under it.
function blockAt(lines, start) {
  const out = [lines[start]]
  const depth = indentOf(lines[start]) + (lines[start].trimStart().startsWith('- ') ? 1 : 0)
  for (let i = start + 1; i < lines.length; i++) {
    if (!isBlank(lines[i]) && indentOf(lines[i]) <= depth) {
      break
    }
    out.push(lines[i])
  }
  return out
}

// Job id to its lines, for the jobs under the top-level `jobs:` key.
function jobs(text) {
  const lines = text.split('\n')
  const start = lines.findIndex((l) => /^jobs:\s*$/.test(l))
  assert.ok(start >= 0, 'the workflow has no top-level jobs: key')
  const out = {}
  for (const line of blockAt(lines, start).slice(1)) {
    const head = line.match(/^ {2}([A-Za-z0-9_-]+):/)
    if (head) {
      out[head[1]] = blockAt(lines, lines.indexOf(line, start))
    }
  }
  return out
}

// The block under the first `key:` line in `lines` at exactly `indent` spaces (any when null).
function keyBlock(lines, key, indent = null) {
  const at = lines.findIndex((l) => {
    const m = l.match(/^( *)(?:- )?([A-Za-z0-9_-]+):(?:\s|$)/)
    return m && m[2] === key && (indent === null || m[1].length === indent)
  })
  return at < 0 ? null : blockAt(lines, at)
}

// The scalar value of a `key:` block: plain or quoted, or a `|` / `>` block scalar.
function scalar(block) {
  if (!block) {
    return null
  }
  const raw = block[0].replace(/^\s*(?:- )?[A-Za-z0-9_-]+:\s*/, '')
  if (/^[|>][-+]?\s*(?:#.*)?$/.test(raw)) {
    const body = block.slice(1)
    const depth = Math.min(...body.filter((l) => l.trim()).map(indentOf))
    const kept = body.map((l) => l.slice(depth)).join('\n').replace(/\n+$/, '')
    return raw.startsWith('>') ? kept.replace(/\n(?=\S)/g, ' ') : kept
  }
  const quoted = raw.match(/^(['"])(.*)\1\s*(?:#.*)?$/)
  return quoted ? quoted[2] : raw.replace(/\s+#.*$/, '').trim()
}

// Each step of a job, as its lines.
function steps(job) {
  const block = keyBlock(job, 'steps')
  if (!block) {
    return []
  }
  const lines = block.slice(1)
  const first = lines.find((l) => /^\s*- /.test(l))
  const depth = first ? indentOf(first) : -1
  return lines.flatMap((l, i) => (/^\s*- /.test(l) && indentOf(l) === depth ? [blockAt(lines, i)] : []))
}

const usesOf = (lines) => scalar(keyBlock(lines, 'uses'))
const norm = (text) => (text ?? '').replace(/\s+/g, ' ').trim()

// Key to value of a permissions block (`{}` gives an empty object).
function permissions(lines, indent) {
  const block = keyBlock(lines, 'permissions', indent)
  if (!block) {
    return null
  }
  if (/:\s*\{\s*\}\s*(?:#.*)?$/.test(block[0])) {
    return {}
  }
  const out = {}
  for (const line of block.slice(1).filter((l) => !isBlank(l))) {
    const m = line.match(/^\s*([a-z-]+):\s*([a-z]+)/)
    out[m[1]] = m[2]
  }
  return out
}

// The job in site.yml that calls pages-site.yml.
function siteCaller() {
  const all = jobs(read(SITE_YML))
  const found = Object.entries(all).filter(([, job]) => scalar(keyBlock(job, 'uses', 4)) === PAGES_SITE)
  assert.equal(found.length, 1, `site.yml must have one job with uses: ${PAGES_SITE}`)
  return found[0][1]
}

const withOf = (lines) => keyBlock(lines, 'with') ?? []
const sitePreBuild = () => scalar(keyBlock(withOf(siteCaller()), 'docs_pre_build_command'))

// The docs-build step of a job, which must be the only one.
function docsBuildStep(file, jobId) {
  const job = jobs(read(file))[jobId]
  assert.ok(job, `${file} has no ${jobId} job`)
  const found = steps(job).filter((s) => usesOf(s) === DOCS_BUILD)
  assert.equal(found.length, 1, `${file} ${jobId} must have one step with uses: ${DOCS_BUILD}`)
  return { job, step: found[0], all: steps(job) }
}

function pagesConfig() {
  assert.ok(existsSync(join(root, PAGES_JSON)), `${PAGES_JSON} must exist`)
  return JSON.parse(read(PAGES_JSON))
}

const isUnpublished = (rel, list) => list.some((u) => rel === u || rel.startsWith(`${u.replace(/\/$/, '')}/`))

function allFiles(dir, base = dir) {
  return readdirSync(dir, { withFileTypes: true }).flatMap((e) => (e.isDirectory()
    ? allFiles(join(dir, e.name), base)
    : [relative(base, join(dir, e.name)).split(sep).join('/')]))
}

// ---------------------------------------------------------------- site.yml

describe('site.yml deploys through hashira pages-site.yml@v3', () => {
  test('one job calls skathio/hashira/.github/workflows/pages-site.yml@v3', () => {
    assert.ok(siteCaller().length > 0)
  })

  test('it passes dotnet_version 10.0.x', () => {
    assert.equal(scalar(keyBlock(withOf(siteCaller()), 'dotnet_version')), '10.0.x')
  })

  test('its docs_pre_build_command runs docfx metadata on tools/docs-api/docfx.json', () => {
    const cmd = sitePreBuild()
    assert.ok(cmd, 'site.yml must pass docs_pre_build_command')
    assert.match(cmd, /docfx metadata\s+(?:\.\/)?tools\/docs-api\/docfx\.json/)
  })

  test('its docs_pre_build_command runs tools/docs-api/clean-api.mjs into $DOCS_STAGE/api', () => {
    const cmd = sitePreBuild() ?? ''
    assert.match(cmd, /tools\/docs-api\/clean-api\.mjs/)
    assert.match(cmd, /\$\{?DOCS_STAGE\}?"?\/api\b/)
  })

  test('it forwards inputs.released, so a release run passes no second approval', () => {
    assert.match(scalar(keyBlock(withOf(siteCaller()), 'released')) ?? '', /^\$\{\{\s*inputs\.released\s*\}\}$/)
  })

  test('the workflow denies all permissions at the top', () => {
    assert.deepEqual(permissions(read(SITE_YML).split('\n'), 0), {})
  })

  test('the caller job grants exactly contents: read, pages: write and id-token: write', () => {
    assert.deepEqual(permissions(siteCaller(), 4), { contents: 'read', pages: 'write', 'id-token': 'write' })
  })

  test('every job declares its own permissions', () => {
    for (const [id, job] of Object.entries(jobs(read(SITE_YML)))) {
      assert.ok(permissions(job, 4) !== null, `site.yml job ${id} declares no permissions`)
    }
  })

  test('the build, upload and deploy are left to pages-site.yml', () => {
    const text = read(SITE_YML)
    for (const gone of ['actions/pages-upload', 'actions/pages-deploy', 'npm ci', 'build.sh']) {
      assert.ok(!text.split('\n').some((l) => !isBlank(l) && l.includes(gone)), `site.yml still runs ${gone}`)
    }
  })
})

// ---------------------------------------------------------------- the same docs build in CI and release

describe('ci.yml and publish.yml build the docs with the same docs-build path', () => {
  const cases = [[CI_YML, 'docs'], [PUBLISH_YML, 'docs']]

  for (const [file, jobId] of cases) {
    test(`${file} ${jobId} uses ${DOCS_BUILD} with site.yml's pre-build command`, () => {
      const { step } = docsBuildStep(file, jobId)
      const cmd = scalar(keyBlock(withOf(step), 'pre_build_command'))
      assert.ok(cmd, `${file} ${jobId} passes no pre_build_command`)
      assert.equal(norm(cmd), norm(sitePreBuild()))
    })

    test(`${file} ${jobId} sets up Node and .NET 10.0.x before docs-build`, () => {
      const { step, all } = docsBuildStep(file, jobId)
      const before = all.slice(0, all.indexOf(step))
      assert.ok(before.some((s) => (usesOf(s) ?? '').startsWith('actions/setup-node@')), 'no setup-node before docs-build')
      const dotnet = before.find((s) => (usesOf(s) ?? '').startsWith('actions/setup-dotnet@'))
      assert.ok(dotnet, 'no setup-dotnet before docs-build')
      assert.equal(scalar(keyBlock(withOf(dotnet), 'dotnet-version')), '10.0.x')
    })

    test(`${file} ${jobId} no longer installs or runs its own docs toolchain`, () => {
      const { job } = docsBuildStep(file, jobId)
      const live = job.filter((l) => !isBlank(l)).join('\n')
      assert.ok(!live.includes('npm ci'), `${file} ${jobId} still runs npm ci`)
      assert.ok(!live.includes('build.sh'), `${file} ${jobId} still runs build.sh`)
    })

    test(`${file} ${jobId} holds only contents: read`, () => {
      const { job } = docsBuildStep(file, jobId)
      assert.deepEqual(permissions(job, 4), { contents: 'read' })
    })
  }

  test('publish.yml pushes only after its docs check passed', () => {
    const needs = scalar(keyBlock(jobs(read(PUBLISH_YML)).publish, 'needs', 4)) ?? ''
    assert.ok(/\bdocs\b/.test(needs), `publish needs ${needs}`)
  })

  test('released: true is passed only by publish.yml site, after the approved publish job', () => {
    const all = jobs(read(PUBLISH_YML))
    const passing = Object.entries(all).filter(([, job]) => job.some((l) => /^\s*released:\s*true\b/.test(l)))
    assert.deepEqual(passing.map(([id]) => id), ['site'])
    assert.match(scalar(keyBlock(all.site, 'needs', 4)) ?? '', /\bpublish\b/)
    assert.equal(scalar(keyBlock(all.publish, 'environment', 4)), 'production')
    for (const file of [SITE_YML, CI_YML]) {
      assert.ok(!read(file).split('\n').some((l) => /^\s*released:\s*true\b/.test(l)), `${file} passes released: true`)
    }
  })

  test('the pre-build commands are written in the workflow file, with no ${{ }} expression', () => {
    const cmds = [sitePreBuild(), ...cases.map(([f, j]) => scalar(keyBlock(withOf(docsBuildStep(f, j).step), 'pre_build_command')))]
    for (const cmd of cmds) {
      assert.ok(cmd && !cmd.includes('${{'), `pre-build command takes an expression: ${cmd}`)
    }
  })
})

// ---------------------------------------------------------------- action pins

describe('workflow pins', () => {
  const files = [SITE_YML, CI_YML, PUBLISH_YML]
  const usesLines = (file) => read(file).split('\n').filter((l) => /^\s*(?:- )?uses:\s*\S/.test(l))

  test('every skathio/hashira uses: tracks @v3 with the moving-tag comment', () => {
    for (const file of files) {
      for (const line of usesLines(file).filter((l) => l.includes('skathio/hashira/'))) {
        assert.match(line, /uses:\s*skathio\/hashira\/\S+@v3\s+#.*moving major tag/, `${file}: ${line.trim()}`)
      }
    }
  })

  test('every checkout of skathio/hashira takes ref v3', () => {
    for (const file of files) {
      for (const step of Object.values(jobs(read(file))).flatMap(steps)) {
        const repo = scalar(keyBlock(withOf(step), 'repository'))
        if (repo === 'skathio/hashira') {
          assert.equal(scalar(keyBlock(withOf(step), 'ref')), 'v3', `${file}: hashira checkout`)
        }
      }
    }
  })

  test('no workflow passes library_ref, which v3 does not have', () => {
    for (const file of files) {
      assert.ok(!read(file).split('\n').some((l) => /^\s*library_ref:/.test(l)), `${file} passes library_ref`)
    }
  })

  test('every other third-party action is pinned by a full commit SHA', () => {
    for (const file of files) {
      for (const line of usesLines(file)) {
        const ref = line.replace(/^\s*(?:- )?uses:\s*/, '').split(/\s/)[0]
        if (ref.startsWith('./') || ref.startsWith('skathio/hashira/')) {
          continue
        }
        assert.match(ref, /^[\w.-]+\/[\w./-]+@[0-9a-f]{40}$/, `${file}: ${ref}`)
      }
    }
  })
})

// ---------------------------------------------------------------- tools/docs-api

describe('the API tooling lives in tools/docs-api and the old docs folder is gone', () => {
  test('clean-api.mjs, docfx.json and clean-api.test.mjs are in tools/docs-api', () => {
    for (const file of ['clean-api.mjs', 'docfx.json', 'clean-api.test.mjs']) {
      assert.ok(existsSync(join(root, 'tools/docs-api', file)), `tools/docs-api/${file} is missing`)
    }
  })

  test(`${OLD_DOCS_DIR}/ is removed`, () => {
    assert.ok(!existsSync(join(root, OLD_DOCS_DIR)), `${OLD_DOCS_DIR}/ still exists`)
  })

  test(`no file in the repo refers to ${OLD_DOCS_DIR} (the changelog's history aside)`, () => {
    const found = spawnSync('git', ['-C', root, 'grep', '-n', '--untracked', '-I', '-F', '-e', OLD_DOCS_DIR, '--',
      '.', ':!CHANGELOG.md', `:!${THIS_FILE}`], { encoding: 'utf8' })
    assert.equal(found.status, 1, `still referred to:\n${found.stdout}${found.stderr}`)
  })

  test('docfx.json reads the twelve packages from the repo src folder', () => {
    const config = JSON.parse(read('tools/docs-api/docfx.json'))
    const sources = config.metadata.flatMap((m) => m.src)
    const projects = sources.flatMap((s) => s.files.map((f) => resolve(root, 'tools/docs-api', s.src, f)))
    assert.equal(projects.length, 12)
    for (const project of projects) {
      assert.ok(project.startsWith(join(root, 'src') + sep), `${project} is not under src/`)
      assert.ok(existsSync(project), `${project} does not exist`)
    }
  })
})

// ---------------------------------------------------------------- .github/pages.json

describe('.github/pages.json keeps the docs URLs and passes hashira checks', () => {
  test('it is valid JSON of at most 64 KB with only the fields hashira accepts', () => {
    assert.ok(statSync(join(root, PAGES_JSON)).size <= 64 * 1024)
    const config = pagesConfig()
    for (const key of Object.keys(config)) {
      assert.ok(PAGES_FIELDS.includes(key), `unknown pages.json field ${key}`)
    }
  })

  test('the docs stay at /rogue/docs/', () => {
    const { base } = pagesConfig()
    assert.ok(base === undefined || base === '/rogue/docs/', `base is ${base}`)
  })

  test('getting-started.md stays the docs index, with no docs/index.md beside it', () => {
    const { home_page: home } = pagesConfig()
    assert.ok(home === undefined || home === 'getting-started.md', `home_page is ${home}`)
    assert.ok(!existsSync(join(root, 'docs/index.md')), 'docs/index.md would clash with the docs index')
  })

  test('every guide published today stays published; governance.md and roadmap.md stay unpublished', () => {
    const unpublished = pagesConfig().unpublished ?? []
    const guides = readdirSync(join(root, 'docs')).filter((f) => f.endsWith('.md'))
    for (const guide of guides) {
      const hidden = ['governance.md', 'roadmap.md'].includes(guide)
      assert.equal(isUnpublished(guide, unpublished), hidden, `docs/${guide}`)
    }
  })

  test('sidebar entries exist, are published and are within hashira limits', () => {
    const { sidebar = [], unpublished = [] } = pagesConfig()
    assert.ok(sidebar.length <= 100)
    for (const { file, label, ...rest } of sidebar) {
      assert.deepEqual(Object.keys(rest), [])
      assert.ok(existsSync(join(root, 'docs', file)), `sidebar file docs/${file} is missing`)
      assert.ok(!isUnpublished(file, unpublished), `sidebar file ${file} is unpublished`)
      assert.ok(label.length >= 1 && label.length <= 60, `sidebar label ${label}`)
    }
  })

  test('name, description, nav, home_link and accent are within hashira limits', () => {
    const config = pagesConfig()
    assert.ok(config.name === undefined || (config.name.length >= 1 && config.name.length <= 40))
    assert.ok(config.description === undefined || config.description.length <= 200)
    assert.ok(config.accent === undefined || /^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$/.test(config.accent))
    const links = [...(config.nav ?? []), ...(config.home_link ? [config.home_link] : [])]
    assert.ok((config.nav ?? []).length <= 8)
    for (const { label, href, ...rest } of links) {
      assert.deepEqual(Object.keys(rest), [])
      assert.ok(label.length >= 1 && label.length <= 30, `link label ${label}`)
      assert.match(href, HREF)
    }
  })
})

// ---------------------------------------------------------------- the pre-build, run

describe('the pre-build writes the API reference into the docs stage', { timeout: 15 * 60 * 1000 }, () => {
  test('it writes api/*.md pages that are published at /rogue/docs/api/ and no file hashira refuses', () => {
    const cmd = sitePreBuild()
    assert.ok(cmd, 'site.yml has no docs_pre_build_command')
    const stage = mkdtempSync(join(tmpdir(), 'rogue-docs-stage-'))
    try {
      const run = spawnSync('bash', ['-eo', 'pipefail', '-c', cmd], {
        cwd: root, env: { ...process.env, DOCS_STAGE: stage }, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024,
      })
      assert.equal(run.status, 0, `pre-build failed:\n${run.stdout.slice(-4000)}\n${run.stderr.slice(-4000)}`)
      const unpublished = pagesConfig().unpublished ?? []
      const files = allFiles(stage)
      for (const page of ['api/skathio-rogue.md', 'api/skathio-rogue-isender.md']) {
        assert.ok(files.includes(page), `${page} was not written`)
        assert.ok(!isUnpublished(page, unpublished), `${page} is unpublished`)
      }
      const refused = files.filter((f) => !STATIC_FILE.test(f) && !isUnpublished(f, unpublished))
      assert.deepEqual(refused, [], 'hashira refuses non-static files in the docs stage')
    } finally {
      rmSync(stage, { recursive: true, force: true })
    }
  })
})
