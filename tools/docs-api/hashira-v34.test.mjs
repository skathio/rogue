// Rules for adopting hashira v3.4.0's Pages setup, which v3 now points at:
// - pages-site.yml refuses `released: true` unless the run already holds an approved `production`
//   review, so site.yml's own github.workflow_ref guard and release-guard.test.mjs are gone, and
//   only publish.yml passes `released: true`, after its approved job;
// - publish.yml builds the Pages site once with a `deploy: false` pages-site call that the tagging
//   job needs, and its released call deploys that artifact (`prebuilt: true`, `artifact_name`);
// - the API reference gets a grouped sidebar again (pages.json groups or folder sections, or a
//   $DOCS_STAGE/.sidebar.json the pre-build writes), and every guide stays in the sidebar;
// - pages.json's accent_dark replaces .github/pages-theme.css, with the brand colours the docs and
//   the landing show today;
// - build-pages.sh builds through docs-build's lib/cli.js, and hashira-docs.test.mjs checks
//   pages.json with `cli.js --check` instead of a copy of hashira's limits;
// - the repo's conventions hold for the new jobs: hashira at @v3, the `ci` job's id, the same
//   pre-build command in site.yml, ci.yml and publish.yml.
//
// hashira's own checks are run from a hashira checkout: HASHIRA_DIR when set, else the v3 tag,
// cloned into a temporary folder. The workflows are read as text, by indentation. One case runs
// site.yml's pre-build command (docfx), so it takes a few minutes.
import { test, describe, after } from 'node:test'
import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs'
import { createRequire } from 'node:module'
import { tmpdir } from 'node:os'
import { join, relative, resolve, sep } from 'node:path'

const root = resolve(import.meta.dirname, '..', '..')
const THIS_FILE = relative(root, import.meta.filename).split(sep).join('/')
const SITE_YML = '.github/workflows/site.yml'
const CI_YML = '.github/workflows/ci.yml'
const PUBLISH_YML = '.github/workflows/publish.yml'
const PAGES_JSON = '.github/pages.json'
const LANDING = 'site/index.html'
const PAGES_SITE = 'skathio/hashira/.github/workflows/pages-site.yml@v3'
const LOCAL_SITE = './.github/workflows/site.yml'
const DOCS_BUILD = 'skathio/hashira/.github/actions/docs-build@v3'
const NUGET_CI = 'skathio/hashira/.github/workflows/nuget-package-ci.yml@v3'
const THEME_CSS = ['pages', 'theme.css'].join('-')
// The brand colours the docs show today (light, then dark), which accent and accent_dark keep.
const LIGHT_BRAND = '#6d28d9'
const DARK_BRAND = '#a78bfa'
// The guides in the docs sidebar today; each stays listed.
const GUIDES = ['getting-started.md', 'behaviors.md', 'jobs.md', 'messaging.md', 'messaging-rabbitmq.md',
  'outbox.md', 'validation.md', 'api-reference.md', 'migration-guide.md', 'benchmarks.md']

const read = (rel) => readFileSync(join(root, rel), 'utf8')
const pagesConfig = () => JSON.parse(read(PAGES_JSON))
const norm = (text) => (text ?? '').replace(/\s+/g, ' ').trim()

// ---------------------------------------------------------------- hashira checkout

let cloned = null
function hashiraDir() {
  if (process.env.HASHIRA_DIR) {
    return resolve(process.env.HASHIRA_DIR)
  }
  if (!cloned) {
    cloned = mkdtempSync(join(tmpdir(), 'rogue-hashira-'))
    const run = spawnSync('git', ['clone', '--quiet', '--depth', '1', '--branch', 'v3',
      'https://github.com/skathio/hashira.git', cloned], { encoding: 'utf8' })
    assert.equal(run.status, 0, `cloning hashira v3 failed: ${run.stderr}`)
  }
  return cloned
}
after(() => cloned && rmSync(cloned, { recursive: true, force: true }))

const docsBuildLib = (file) => join(hashiraDir(), '.github/actions/docs-build/lib', file)
const hashiraModule = (file) => createRequire(import.meta.url)(docsBuildLib(file))

// ---------------------------------------------------------------- YAML by indentation

const indentOf = (line) => line.match(/^ */)[0].length
const isBlank = (line) => line.trim() === '' || line.trim().startsWith('#')

function blockAt(lines, start) {
  const depth = indentOf(lines[start]) + (lines[start].trimStart().startsWith('- ') ? 1 : 0)
  const end = lines.findIndex((l, i) => i > start && !isBlank(l) && indentOf(l) <= depth)
  return lines.slice(start, end < 0 ? lines.length : end)
}

function keyBlock(lines, key, indent = null) {
  const at = lines.findIndex((l) => {
    const m = l.match(/^( *)(?:- )?([A-Za-z0-9_-]+):(?:\s|$)/)
    return m && m[2] === key && (indent === null || m[1].length === indent)
  })
  return at < 0 ? null : blockAt(lines, at)
}

function scalar(block) {
  if (!block) {
    return undefined
  }
  const raw = block[0].replace(/^\s*(?:- )?[A-Za-z0-9_-]+:\s*/, '')
  if (/^[|>][-+]?\s*(?:#.*)?$/.test(raw)) {
    const body = block.slice(1)
    const depth = Math.min(...body.filter((l) => l.trim()).map(indentOf))
    return body.map((l) => l.slice(depth)).join('\n').replace(/\n+$/, '')
  }
  const quoted = raw.match(/^(['"])(.*)\1\s*(?:#.*)?$/)
  return quoted ? quoted[2] : raw.replace(/\s+#.*$/, '').trim()
}

function jobs(file) {
  const lines = read(file).split('\n')
  const start = lines.findIndex((l) => /^jobs:\s*$/.test(l))
  assert.ok(start >= 0, `${file} has no top-level jobs: key`)
  const out = {}
  blockAt(lines, start).forEach((line, i) => {
    const head = line.match(/^ {2}([A-Za-z0-9_-]+):/)
    if (head) {
      out[head[1]] = blockAt(lines, start + i)
    }
  })
  return out
}

const withOf = (lines) => keyBlock(lines, 'with') ?? []
const withValue = (lines, key) => scalar(keyBlock(withOf(lines), key))
const usesOf = (lines) => scalar(keyBlock(lines, 'uses'))
const jobUses = (job) => scalar(keyBlock(job, 'uses', 4))
const needsOf = (job) => {
  const block = keyBlock(job, 'needs', 4)
  return block ? block.join(' ').replace(/^\s*needs:/, '').match(/[A-Za-z0-9_-]+/g) ?? [] : []
}
const stepsOf = (job) => {
  const block = keyBlock(job, 'steps')
  const lines = block ? block.slice(1) : []
  const first = lines.find((l) => /^\s*- /.test(l))
  return lines.flatMap((l, i) => (first && /^\s*- /.test(l) && indentOf(l) === indentOf(first) ? [blockAt(lines, i)] : []))
}

function permissionsOf(job) {
  const block = keyBlock(job, 'permissions', 4)
  if (!block || /:\s*\{\s*\}/.test(block[0])) {
    return {}
  }
  return Object.fromEntries(block.slice(1).filter((l) => !isBlank(l)).map((l) => l.trim().split(/:\s*/)))
}

// site.yml's job that calls pages-site.yml.
function siteCaller() {
  const found = Object.values(jobs(SITE_YML)).filter((job) => jobUses(job) === PAGES_SITE)
  assert.equal(found.length, 1, `site.yml must have one job with uses: ${PAGES_SITE}`)
  return found[0]
}

// The pages-site.yml input `key` a publish.yml job passes: its own `with` when it calls
// pages-site.yml, or site.yml's caller's `with` with each `${{ inputs.x }}` taken from the job.
function pagesInput(job, key) {
  if (jobUses(job) === PAGES_SITE) {
    return withValue(job, key)
  }
  assert.equal(jobUses(job), LOCAL_SITE, 'not a call of pages-site.yml or site.yml')
  const value = withValue(siteCaller(), key)
  const forwarded = value?.match(/^\$\{\{\s*inputs\.([A-Za-z0-9_-]+)\s*\}\}$/)
  return forwarded ? withValue(job, forwarded[1]) : value
}

const pagesCalls = () => Object.entries(jobs(PUBLISH_YML)).filter(([, job]) => [PAGES_SITE, LOCAL_SITE].includes(jobUses(job)))

function onePagesCall(predicate, what) {
  const found = pagesCalls().filter(([, job]) => predicate(job))
  assert.equal(found.length, 1, `publish.yml must have one ${what} call, found ${found.map(([id]) => id).join(', ') || 'none'}`)
  return found[0]
}

const buildOnlyCall = () => onePagesCall((job) => pagesInput(job, 'deploy') === 'false', 'deploy: false pages-site')
const releasedCall = () => onePagesCall((job) => pagesInput(job, 'released') === 'true', 'released: true pages-site')

// ---------------------------------------------------------------- the released guard

describe('pages-site.yml owns the released guard', () => {
  test('site.yml no longer compares github.workflow_ref', () => {
    const live = read(SITE_YML).split('\n').filter((l) => !isBlank(l))
    assert.ok(!live.some((l) => l.includes('workflow_ref')), 'site.yml still checks github.workflow_ref')
  })

  test('site.yml still forwards inputs.released to pages-site.yml', () => {
    assert.match(withValue(siteCaller(), 'released') ?? '', /^\$\{\{\s*inputs\.released\s*\}\}$/)
  })

  test('tools/docs-api/release-guard.test.mjs is removed', () => {
    assert.ok(!existsSync(join(root, 'tools/docs-api/release-guard.test.mjs')))
  })

  test('only publish.yml passes released: true', () => {
    for (const file of [SITE_YML, CI_YML]) {
      assert.ok(!read(file).split('\n').some((l) => /^\s*released:\s*true\b/.test(l)), `${file} passes released: true`)
    }
  })

  test('the released call needs the job that passed the production approval', () => {
    const all = jobs(PUBLISH_YML)
    const [, job] = releasedCall()
    const approved = needsOf(job).filter((id) => all[id] && scalar(keyBlock(all[id], 'environment', 4)) === 'production')
    assert.ok(approved.length > 0, `the released call needs ${needsOf(job).join(', ')}, none in production`)
  })
})

// ---------------------------------------------------------------- publish.yml builds once

describe('publish.yml builds the site once, before the tag, and deploys that build', () => {
  test('no publish.yml job runs docs-build on its own any more', () => {
    const steps = Object.values(jobs(PUBLISH_YML)).flatMap(stepsOf)
    assert.ok(!steps.some((s) => usesOf(s) === DOCS_BUILD), `publish.yml still uses ${DOCS_BUILD}`)
  })

  test('a deploy: false call builds with site.yml\'s pre-build command and .NET 10.0.x', () => {
    const [, job] = buildOnlyCall()
    assert.equal(norm(pagesInput(job, 'docs_pre_build_command')), norm(withValue(siteCaller(), 'docs_pre_build_command')))
    assert.equal(pagesInput(job, 'dotnet_version'), '10.0.x')
  })

  test('the job that tags the release (nuget-push) needs the deploy: false call', () => {
    const [buildId] = buildOnlyCall()
    const tagging = Object.entries(jobs(PUBLISH_YML))
      .filter(([, job]) => stepsOf(job).some((s) => (usesOf(s) ?? '').startsWith('skathio/hashira/.github/actions/nuget-push@')))
    assert.equal(tagging.length, 1, 'publish.yml must have one job that runs nuget-push')
    assert.ok(needsOf(tagging[0][1]).includes(buildId), `${tagging[0][0]} needs ${needsOf(tagging[0][1]).join(', ')}`)
  })

  test('the released call passes prebuilt: true', () => {
    const [, job] = releasedCall()
    assert.equal(pagesInput(job, 'prebuilt'), 'true')
  })

  test('the released call names the artifact the deploy: false call uploads', () => {
    const [, released] = releasedCall()
    const [, built] = buildOnlyCall()
    const name = pagesInput(released, 'artifact_name')
    assert.ok(name, 'the released call passes no artifact_name')
    assert.equal(name, pagesInput(built, 'artifact_name') ?? 'github-pages')
  })
})

// ---------------------------------------------------------------- conventions in the workflows

describe('the workflows keep the repo conventions', () => {
  const files = [SITE_YML, CI_YML, PUBLISH_YML]

  test('every skathio/hashira uses: tracks @v3, every hashira checkout takes ref v3, no library_ref', () => {
    for (const file of files) {
      const lines = read(file).split('\n')
      for (const line of lines.filter((l) => /^\s*(?:- )?uses:\s*skathio\/hashira\//.test(l))) {
        assert.match(line, /uses:\s*skathio\/hashira\/\S+@v3(?:\s|$)/, `${file}: ${line.trim()}`)
      }
      for (const step of Object.values(jobs(file)).flatMap(stepsOf)) {
        if (withValue(step, 'repository') === 'skathio/hashira') {
          assert.equal(withValue(step, 'ref'), 'v3', `${file}: hashira checkout`)
        }
      }
      assert.ok(!lines.some((l) => /^\s*library_ref:/.test(l)), `${file} passes library_ref`)
    }
  })

  test('the ci job keeps its id, calls nuget-package-ci.yml@v3 and holds no id-token or pages grant', () => {
    const ci = jobs(CI_YML).ci
    assert.ok(ci, 'ci.yml has no job with the id ci')
    assert.equal(jobUses(ci), NUGET_CI)
    const grants = permissionsOf(ci)
    assert.equal(grants['id-token'], undefined)
    assert.equal(grants.pages, undefined)
  })

  test('every pre-build command in ci.yml and publish.yml equals site.yml\'s', () => {
    const site = norm(withValue(siteCaller(), 'docs_pre_build_command'))
    assert.ok(site, 'site.yml passes no docs_pre_build_command')
    for (const file of [CI_YML, PUBLISH_YML]) {
      const lines = read(file).split('\n')
      const cmds = lines.flatMap((l, i) => (/^\s*(?:docs_)?pre_build_command:/.test(l) ? [scalar(blockAt(lines, i))] : []))
      for (const cmd of cmds) {
        assert.equal(norm(cmd), site, `${file} runs another pre-build command`)
      }
    }
  })
})

// ---------------------------------------------------------------- the accent pair

describe('accent_dark replaces the theme CSS with the same brand colours', () => {
  const css = (name) => read(LANDING).match(new RegExp(`${name}\\s*:\\s*(#[0-9a-fA-F]{3,6})\\b`))?.[1]

  test(`.github/${THEME_CSS} is removed and pages.json names no theme_css`, () => {
    assert.ok(!existsSync(join(root, '.github', THEME_CSS)), `.github/${THEME_CSS} still exists`)
    assert.equal(pagesConfig().theme_css, undefined)
  })

  test(`pages.json keeps ${LIGHT_BRAND} in light mode and ${DARK_BRAND} in dark mode`, () => {
    const { accent, accent_dark: dark } = pagesConfig()
    assert.equal(accent?.toLowerCase(), LIGHT_BRAND)
    assert.equal(dark?.toLowerCase(), DARK_BRAND)
  })

  test('the landing sets the same pair as --site-accent and --site-accent-dark', () => {
    const { accent, accent_dark: dark } = pagesConfig()
    assert.equal(css('--site-accent')?.toLowerCase(), accent?.toLowerCase())
    assert.equal(css('--site-accent-dark')?.toLowerCase(), dark?.toLowerCase())
  })

  test(`no file in the repo refers to ${THEME_CSS} (the changelog aside)`, () => {
    const found = spawnSync('git', ['-C', root, 'grep', '-n', '--untracked', '-I', '-F', '-e', THEME_CSS, '--',
      '.', ':!CHANGELOG.md', `:!${THIS_FILE}`], { encoding: 'utf8' })
    assert.equal(found.status, 1, `still referred to:\n${found.stdout}${found.stderr}`)
  })

  test('the changelog no longer says the API pages are in a flat sidebar', () => {
    const unreleased = read('CHANGELOG.md').split(/^## \[Unreleased\]\s*$/m)[1]?.split(/^## \[/m)[0] ?? ''
    assert.ok(!/flat\s+sidebar/.test(unreleased), 'the [Unreleased] section still describes a flat sidebar')
  })
})

// ---------------------------------------------------------------- hashira's own checks

describe('the docs are checked and built with hashira\'s docs-build cli.js', { timeout: 5 * 60 * 1000 }, () => {
  test('cli.js --check passes on .github/pages.json and docs/, with no contrast warning', () => {
    const run = spawnSync(process.execPath, [docsBuildLib('cli.js'), '--check'], { cwd: root, encoding: 'utf8' })
    assert.equal(run.status, 0, `cli.js --check failed:\n${run.stdout}${run.stderr}`)
    assert.ok(!/contrast/i.test(run.stdout + run.stderr), `cli.js --check warns:\n${run.stdout}`)
  })

  test('build-pages.sh builds through cli.js --out and --pre-build, not prepare.js or VitePress by path', () => {
    const live = read('tools/docs-api/build-pages.sh').split('\n').filter((l) => !isBlank(l)).join('\n')
    assert.match(live, /docs-build\/lib\/cli\.js|\/lib\/cli\.js/)
    assert.match(live, /--out\b/)
    assert.match(live, /--pre-build\b/)
    assert.ok(!/lib\/prepare\.js/.test(live), 'build-pages.sh still runs lib/prepare.js')
    assert.ok(!/vitepress\.js/.test(live), 'build-pages.sh still runs VitePress by path')
  })

  test('hashira-docs.test.mjs checks pages.json with cli.js --check, not a copy of hashira\'s limits', () => {
    const text = read('tools/docs-api/hashira-docs.test.mjs')
    assert.match(text, /cli\.js/)
    assert.match(text, /--check/)
    for (const copied of ['PAGES_FIELDS', 'STATIC_FILE', '64 * 1024']) {
      assert.ok(!text.includes(copied), `hashira-docs.test.mjs still copies ${copied}`)
    }
  })
})

// ---------------------------------------------------------------- the grouped API sidebar, built

function stageFiles(dir, base = dir) {
  return readdirSync(dir, { withFileTypes: true }).flatMap((e) => (e.isDirectory()
    ? stageFiles(join(dir, e.name), base)
    : [relative(base, join(dir, e.name)).split(sep).join('/')]))
}

// The page files and folders the sidebar entries hold inside a group or as a folder section.
function grouped(list, inGroup = false) {
  return list.flatMap((entry) => {
    if (entry.items) {
      return grouped(entry.items, true)
    }
    if (entry.dir) {
      return [{ dir: entry.dir.replace(/\/?$/, '/') }]
    }
    return inGroup ? [{ file: entry.file }] : []
  })
}

describe('the API reference has a grouped sidebar', { timeout: 15 * 60 * 1000 }, () => {
  let stage = null
  const built = () => {
    if (!stage) {
      stage = mkdtempSync(join(tmpdir(), 'rogue-docs-stage-'))
      const cmd = withValue(siteCaller(), 'docs_pre_build_command')
      const run = spawnSync('bash', ['-eo', 'pipefail', '-c', cmd], {
        cwd: root, env: { ...process.env, DOCS_STAGE: stage }, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024,
      })
      assert.equal(run.status, 0, `pre-build failed:\n${run.stdout.slice(-4000)}\n${run.stderr.slice(-4000)}`)
    }
    return stage
  }
  // The sidebar the build uses: a generated one, checked by hashira, or pages.json's.
  const sidebar = () => hashiraModule('pages-config.js').loadGeneratedSidebar(built()) ?? pagesConfig().sidebar ?? []
  after(() => stage && rmSync(stage, { recursive: true, force: true }))

  test('every API page sits in a sidebar group or folder section', () => {
    const pages = stageFiles(built()).filter((f) => /^api\/.+\.md$/.test(f))
    assert.ok(pages.includes('api/skathio-rogue-isender.md'), 'the pre-build wrote no API pages')
    const held = grouped(sidebar())
    const loose = pages.filter((p) => !held.some((h) => h.file === p || (h.dir && p.startsWith(h.dir))))
    assert.deepEqual(loose, [], 'API pages outside any group or folder section')
  })

  test('every page a generated sidebar names was written to the stage', () => {
    const { sidebarFiles } = hashiraModule('pages-config.js')
    const named = sidebarFiles(sidebar()).filter((f) => !GUIDES.includes(f))
    const files = stageFiles(built())
    assert.deepEqual(named.filter((f) => !files.includes(f) && !existsSync(join(root, 'docs', f))), [])
  })

  test('every guide in the sidebar today stays in it', () => {
    const { sidebarFiles } = hashiraModule('pages-config.js')
    const named = sidebarFiles(sidebar())
    assert.deepEqual(GUIDES.filter((g) => !named.includes(g)), [])
  })

  test('the stage holds only files hashira publishes', () => {
    const { checkStagedFiles } = hashiraModule('site-config.js')
    const { GENERATED_SIDEBAR } = hashiraModule('pages-config.js')
    assert.doesNotThrow(() => checkStagedFiles(stageFiles(built()).filter((f) => f !== GENERATED_SIDEBAR), 'DOCS_STAGE/'))
  })
})
