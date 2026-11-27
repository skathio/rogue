// clean-api.mjs as a command, `<docfx-md-dir> <out-dir>`, on small docfx outputs: slug file
// names, one H1 per page and no front matter, xrefs and docfx links turned into links between the
// pages it writes (or inline code for types outside our packages), the HTML of the /// comments
// turned into Markdown, fenced code left alone, only Markdown pages in the output (hashira's
// docs-build refuses any other file in the docs stage), and clear errors on bad input.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

const script = join(import.meta.dirname, 'clean-api.mjs')

function inTemp(body) {
  const dir = mkdtempSync(join(tmpdir(), 'clean-api-unit-'))
  try {
    return body(dir)
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
}

// Runs the cleanup on a folder made by `setup`, which returns the two arguments. `pages` maps
// each file written to the output folder to its text.
function runWith(setup) {
  return inTemp((dir) => {
    const result = spawnSync(process.execPath, [script, ...setup(dir)], { encoding: 'utf8', timeout: 60_000 })
    const out = join(dir, 'out')
    const isDir = existsSync(out) && !result.stderr.includes('cannot create the output folder')
    const names = isDir ? readdirSync(out) : []
    return { result, pages: Object.fromEntries(names.map((n) => [n, readFileSync(join(out, n), 'utf8')])) }
  })
}

function run(files) {
  return runWith((dir) => {
    mkdirSync(join(dir, 'in'))
    for (const [name, body] of Object.entries(files)) {
      writeFileSync(join(dir, 'in', name), body)
    }
    return [join(dir, 'in'), join(dir, 'out')]
  })
}

function assertClearFailure({ result, pages }, message) {
  assert.notEqual(result.status, 0)
  assert.match(result.stderr, message)
  assert.match(result.stderr, /^clean-api: /)
  assert.doesNotMatch(result.stderr, /\n\s+at /, 'the error is a stack trace')
  assert.deepEqual(Object.keys(pages), [])
}

const xref = (href) => `<xref href="${href}" data-throw-if-not-resolved="false"></xref>`

// A namespace with a generic class, a generic interface and ISender, as docfx writes them.
const FIXTURE = {
  'toc.yml': '### YamlMime:TableOfContent\n- name: SkathIO.Rogue\n  href: SkathIO.Rogue.md\n',
  'SkathIO.Rogue.md': [
    '# <a id="SkathIO_Rogue"></a> Namespace SkathIO.Rogue',
    '',
    '### Interfaces',
    '',
    ' [ISender](SkathIO.Rogue.ISender.md)',
    '',
    ' [ICommand<TResponse\\>](SkathIO.Rogue.ICommand\\-1.md)',
    '',
  ].join('\n'),
  'SkathIO.Rogue.ISender.md': [
    '# <a id="SkathIO_Rogue_ISender"></a> Interface ISender',
    '',
    'Namespace: [SkathIO.Rogue](SkathIO.Rogue.md)  ',
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
    '### <a id="SkathIO_Rogue_ISender_Send_"></a> Send\\(ICommand, CancellationToken\\)',
    '',
    '`cancellationToken` [CancellationToken](https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken)',
    '',
  ].join('\n'),
  'SkathIO.Rogue.ICommand-1.md': [
    '# <a id="SkathIO_Rogue_ICommand_1"></a> Interface ICommand<TResponse\\>',
    '',
    `Sent through ${xref('SkathIO.Rogue.ISender')}, see`,
    `${xref('SkathIO.Rogue.ISender.Send(SkathIO.Rogue.ICommand%2cSystem.Threading.CancellationToken)')} and`,
    `${xref('SkathIO.Rogue.RequestState%601.Handled')}.`,
    '',
  ].join('\n'),
  'SkathIO.Rogue.RequestState-1.md': [
    '# <a id="SkathIO_Rogue_RequestState_1"></a> Class RequestState<TResponse\\>',
    '',
    'A rule type provides',
    '<ul><li><code>IsValid(TValue value)</code>, one per',
    'type;</li><li><code>DefaultCode</code>, the code.</li></ul>',
    '',
    'Runs them <em>all</em>; <a href="https://learn.microsoft.com/dotnet/csharp/language-reference/keywords/null">null</a> means none.',
    'Resolves <code>IQuery&lt;TResponse&gt;</code>.',
    '',
    '[ISender.Send](SkathIO.Rogue.ISender.md\\#SkathIO\\_Rogue\\_ISender\\_Send\\_), [Gone](SkathIO.Rogue.Gone.md)',
    '',
    '```csharp',
    'public class A { /* <code>kept</code> [X](SkathIO.Rogue.ISender.md) */ }',
    '```',
    '',
  ].join('\n'),
}

const cached = {}
function clean() {
  cached.run ??= run(FIXTURE)
  assert.equal(cached.run.result.status, 0, cached.run.result.stderr)
  return cached.run.pages
}

const page = (name) => clean()[name]
const linkTo = (file) => new RegExp(`\\]\\(\\./${file.replace(/[.-]/g, '\\$&')}\\)`)

test('it writes one Markdown page per docfx page, under a lowercase slug, and nothing else', () => {
  assert.deepEqual(Object.keys(clean()).sort(), [
    'skathio-rogue-icommand-1.md', 'skathio-rogue-isender.md', 'skathio-rogue-requeststate-1.md', 'skathio-rogue.md',
  ])
})

test('each page starts with its one H1, without the docfx anchor or front matter', () => {
  for (const [name, body] of Object.entries(clean())) {
    assert.match(body, /^# \S/, `${name} does not start with its H1`)
    const h1s = body.replace(/```[\s\S]*?```/g, '').split('\n').filter((l) => /^# /.test(l))
    assert.equal(h1s.length, 1, `${name} has ${h1s.length} H1 lines`)
    assert.doesNotMatch(body, /<a id=/, name)
  }
  // docs-build labels the sidebar with the H1 as written, so it carries no `\>` escape.
  assert.match(page('skathio-rogue-icommand-1.md'), /^# Interface ICommand<TResponse>\n/)
  assert.match(page('skathio-rogue-requeststate-1.md'), /^# Class RequestState<TResponse>\n/)
})

test('an xref to one of our types, or to one of its members, links to the type page', () => {
  const generic = page('skathio-rogue-icommand-1.md')
  assert.match(generic, /Sent through \[ISender\]\(\.\/skathio-rogue-isender\.md\), see/)
  assert.match(generic, /\[ISender\.Send\]\(\.\/skathio-rogue-isender\.md\) and/)
  assert.match(generic, /\[RequestState\.Handled\]\(\.\/skathio-rogue-requeststate-1\.md\)\./)
  assert.match(page('skathio-rogue-isender.md'), linkTo('skathio-rogue-icommand-1.md'))
})

test('an xref to an outside type, or with no href, is inline code and no xref is left', () => {
  const isender = page('skathio-rogue-isender.md')
  assert.match(isender, /throws `InvalidOperationException`\./)
  assert.match(isender, /Gone before `[^`]*` after\./)
  for (const [name, body] of Object.entries(clean())) {
    assert.doesNotMatch(body, /<\/?xref\b|\]\(\)/, name)
  }
})

test('a learn.microsoft.com link is inline code, not a link', () => {
  assert.match(page('skathio-rogue-isender.md'), /`cancellationToken` `CancellationToken`/)
  assert.match(page('skathio-rogue-requeststate-1.md'), /Runs them \*all\*; `null` means none\./)
  for (const body of Object.values(clean())) {
    assert.doesNotMatch(body, /\]\(https:\/\/learn\.microsoft\.com\//)
  }
})

test('the HTML of the /// comments becomes Markdown', () => {
  const isender = page('skathio-rogue-isender.md')
  assert.match(isender, /returning `TResponse`\./)
  assert.match(isender, /\*\*Resolve this from a scope\.\*\* Resolving from the root `IServiceProvider`/)
  assert.doesNotMatch(isender, /<\/?(?:p|b|code)\b|typeparamref/)
  const state = page('skathio-rogue-requeststate-1.md')
  assert.match(state, /\n- `IsValid\(TValue value\)`, one per type;\n- `DefaultCode`, the code\.\n/)
  assert.match(state, /Resolves `IQuery<TResponse>`\./)
  assert.doesNotMatch(state.split('```')[0], /<\/?(?:ul|li|code|em|a)\b/)
})

test('a docfx link goes to the slug page it wrote, a member anchor to the type page, a page not written to code', () => {
  assert.match(page('skathio-rogue.md'), /\[ISender\]\(\.\/skathio-rogue-isender\.md\)/)
  assert.match(page('skathio-rogue-requeststate-1.md'), /\[ISender\.Send\]\(\.\/skathio-rogue-isender\.md\), `Gone`/)
  for (const [name, body] of Object.entries(clean())) {
    for (const [, href] of body.split('```')[0].matchAll(/\]\(([^)\s]+)\)/g)) {
      assert.ok(clean()[href.replace(/^\.\//, '')], `${name} links to ${href}, which was not written`)
    }
  }
})

test('fenced code is left as docfx wrote it', () => {
  assert.match(page('skathio-rogue-requeststate-1.md'), /public class A \{ \/\* <code>kept<\/code> \[X\]\(SkathIO\.Rogue\.ISender\.md\) \*\/ \}/)
})

test('one page with no toc.yml is the smallest input that works', () => {
  const { result, pages } = run({ 'SkathIO.Rogue.A.md': '# <a id="SkathIO_Rogue_A"></a> Class A\n' })
  assert.equal(result.status, 0, result.stderr)
  assert.deepEqual(pages, { 'skathio-rogue-a.md': '# Class A\n' })
})

test('missing arguments fail with the usage', () => {
  assertClearFailure(runWith(() => []), /usage: node clean-api\.mjs <docfx-md-dir> <out-dir>/)
  assertClearFailure(runWith((dir) => [join(dir, 'in')]), /usage: node clean-api\.mjs <docfx-md-dir> <out-dir>/)
})

test('a docfx folder that does not exist, or is a file, fails and names it', () => {
  assertClearFailure(runWith((dir) => [join(dir, 'in'), join(dir, 'out')]), /cannot read the docfx output folder .*in/)
  const file = runWith((dir) => {
    writeFileSync(join(dir, 'in'), '# Class A\n')
    return [join(dir, 'in'), join(dir, 'out')]
  })
  assertClearFailure(file, /cannot read the docfx output folder .*in/)
})

test('a docfx folder with no pages fails instead of writing an empty API', () => {
  assertClearFailure(run({ 'toc.yml': '### YamlMime:TableOfContent\n' }), /holds no docfx Markdown pages/)
})

test('a page with no H1 fails and names the page', () => {
  const { result } = run({ 'SkathIO.Rogue.Broken.md': 'Namespace: SkathIO.Rogue\n\n## Methods\n' })
  assert.notEqual(result.status, 0)
  assert.match(result.stderr, /^clean-api: SkathIO\.Rogue\.Broken\.md has no H1 heading/)
})

test('an output path that is a file fails and names it', () => {
  const { result } = runWith((dir) => {
    mkdirSync(join(dir, 'in'))
    writeFileSync(join(dir, 'in', 'SkathIO.Rogue.A.md'), '# Class A\n')
    writeFileSync(join(dir, 'out'), '')
    return [join(dir, 'in'), join(dir, 'out')]
  })
  assert.notEqual(result.status, 0)
  assert.match(result.stderr, /^clean-api: cannot create the output folder .*out/)
  assert.doesNotMatch(result.stderr, /\n\s+at /)
})

// ---------------------------------------------------------------- --sidebar

const GUIDES = { sidebar: [{ file: 'getting-started.md', label: 'Getting started' }] }
const TOC = [
  '### YamlMime:TableOfContent',
  '- name: SkathIO.Rogue',
  '  href: SkathIO.Rogue.md',
  '  items:',
  '  - name: Classes',
  '  - name: RequestState<TResponse>',
  '    href: SkathIO.Rogue.RequestState-1.md',
  '  - name: Structs',
  '  - name: Interfaces',
  '  - name: ICommand<TResponse>',
  '    href: SkathIO.Rogue.ICommand-1.md',
  '  - name: ISender',
  '    href: SkathIO.Rogue.ISender.md',
  '',
].join('\n')

// Runs the cleanup with --sidebar on `files` (FIXTURE's pages with `toc` as toc.yml, left out
// when null) and `pages` as pages.json (left out when null, written as is when a string). Returns the run and the
// sidebar it wrote, parsed, or null.
function runSidebar({ toc = TOC, pages = GUIDES, files = FIXTURE, extra = [] } = {}) {
  return inTemp((dir) => {
    mkdirSync(join(dir, 'in'))
    for (const [name, body] of Object.entries({ ...files, 'toc.yml': toc })) {
      if (body !== null) {
        writeFileSync(join(dir, 'in', name), body)
      }
    }
    if (pages !== null) {
      writeFileSync(join(dir, 'pages.json'), typeof pages === 'string' ? pages : JSON.stringify(pages))
    }
    const args = [join(dir, 'in'), join(dir, 'out'), '--sidebar', join(dir, '.sidebar.json'), join(dir, 'pages.json'), ...extra]
    const result = spawnSync(process.execPath, [script, ...args], { encoding: 'utf8', timeout: 60_000 })
    const file = join(dir, '.sidebar.json')
    return { result, sidebar: existsSync(file) ? JSON.parse(readFileSync(file, 'utf8')) : null }
  })
}

test('--sidebar writes the guides, then one folded group per namespace with its page and a group per kind', () => {
  const { result, sidebar } = runSidebar()
  assert.equal(result.status, 0, result.stderr)
  assert.match(result.stdout, /4 pages, 1 namespaces in the sidebar/)
  assert.deepEqual(sidebar, [
    { file: 'getting-started.md', label: 'Getting started' },
    {
      label: 'SkathIO.Rogue',
      collapsed: true,
      items: [
        { file: 'out/skathio-rogue.md', label: 'Overview' },
        { label: 'Classes', items: [{ file: 'out/skathio-rogue-requeststate-1.md', label: 'RequestState<TResponse>' }] },
        {
          label: 'Interfaces',
          items: [
            { file: 'out/skathio-rogue-icommand-1.md', label: 'ICommand<TResponse>' },
            { file: 'out/skathio-rogue-isender.md', label: 'ISender' },
          ],
        },
      ],
    },
  ])
})

test('--sidebar keeps a 60-character label and shortens a longer one to 60 with an ellipsis', () => {
  const at = 'A'.repeat(60)
  const past = 'B'.repeat(61)
  const toc = `- name: SkathIO.Rogue\n  href: SkathIO.Rogue.md\n  items:\n  - name: ${at}\n    href: SkathIO.Rogue.ISender.md\n  - name: ${past}\n    href: SkathIO.Rogue.ICommand-1.md\n`
  const { result, sidebar } = runSidebar({ toc })
  assert.equal(result.status, 0, result.stderr)
  const labels = sidebar[1].items.map((e) => e.label)
  assert.deepEqual(labels, ['Overview', at, `${'B'.repeat(59)}…`])
})

test('--sidebar with a pages.json that has no sidebar writes the API groups alone', () => {
  const { result, sidebar } = runSidebar({ pages: { name: 'Rogue' } })
  assert.equal(result.status, 0, result.stderr)
  assert.deepEqual(sidebar.map((e) => e.label), ['SkathIO.Rogue'])
})

test('--sidebar fails and names the file when toc.yml lists a page docfx did not write', () => {
  const toc = '- name: SkathIO.Rogue\n  href: SkathIO.Rogue.Gone.md\n'
  const { result, sidebar } = runSidebar({ toc })
  assert.notEqual(result.status, 0)
  assert.match(result.stderr, /^clean-api: toc\.yml lists SkathIO\.Rogue\.Gone\.md, which is not in the docfx output/)
  assert.equal(sidebar, null)
})

test('--sidebar fails when toc.yml lists a type before any namespace', () => {
  const { result } = runSidebar({ toc: '  - name: ISender\n    href: SkathIO.Rogue.ISender.md\n' })
  assert.notEqual(result.status, 0)
  assert.match(result.stderr, /^clean-api: toc\.yml lists ISender before any namespace/)
})

test('--sidebar fails and names what it could not read: no toc.yml, no pages.json, or pages.json that is not JSON', () => {
  const noToc = runSidebar({ toc: null })
  assert.match(noToc.result.stderr, /^clean-api: cannot read docfx's toc\.yml in /)
  const noPages = runSidebar({ pages: null })
  assert.match(noPages.result.stderr, /^clean-api: cannot read .*pages\.json/)
  const badPages = runSidebar({ pages: '{ not json' })
  assert.match(badPages.result.stderr, /^clean-api: cannot read .*pages\.json/)
  for (const { result, sidebar } of [noToc, noPages, badPages]) {
    assert.notEqual(result.status, 0)
    assert.doesNotMatch(result.stderr, /\n\s+at /)
    assert.equal(sidebar, null)
  }
})

test('--sidebar without both its values, or another flag, fails with the usage', () => {
  const usage = /usage: node clean-api\.mjs <docfx-md-dir> <out-dir> \[--sidebar <file> <pages\.json>\]/
  assertClearFailure(runWith((dir) => [join(dir, 'in'), join(dir, 'out'), '--sidebar', join(dir, 's.json')]), usage)
  assertClearFailure(runWith((dir) => [join(dir, 'in'), join(dir, 'out'), '--toc', 'a', 'b']), usage)
  const extra = runSidebar({ extra: ['more'] })
  assert.match(extra.result.stderr, usage)
  assert.equal(extra.sidebar, null)
})
