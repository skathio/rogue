// Cases of clean-api.mjs that the real docfx output holds and api-site.test.mjs does not
// drive: lists, emphasis, keyword links, entities in code, member anchors, code fences, a
// toc with more than one namespace, and the command's own errors.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

const script = join(import.meta.dirname, 'clean-api.mjs')

function run(files, args) {
  const dir = mkdtempSync(join(tmpdir(), 'clean-api-unit-'))
  try {
    mkdirSync(join(dir, 'in'))
    for (const [name, body] of Object.entries(files)) {
      writeFileSync(join(dir, 'in', name), body)
    }
    const result = spawnSync(process.execPath, [script, ...(args ?? [join(dir, 'in'), join(dir, 'out')])], { encoding: 'utf8' })
    const read = (name) => (existsSync(join(dir, 'out', name)) ? readFileSync(join(dir, 'out', name), 'utf8') : null)
    return { result, page: read('skathio-rogue-a.md'), sidebar: JSON.parse(read('sidebar.json') ?? 'null') }
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
}

const TOC = [
  '### YamlMime:TableOfContent',
  '- name: SkathIO.Rogue',
  '  href: SkathIO.Rogue.md',
  '  items:',
  '  - name: Classes',
  '  - name: A',
  '    href: SkathIO.Rogue.A.md',
  '- name: SkathIO.Rogue.Jobs',
  '  href: SkathIO.Rogue.Jobs.md',
  '  items:',
  '  - name: Structs',
  '  - name: B<T>',
  '    href: SkathIO.Rogue.Jobs.B-1.md',
  '',
].join('\n')

const PAGE_A = [
  '# <a id="SkathIO_Rogue_A"></a> Class A',
  '',
  'A rule type provides',
  '<ul><li><code>IsValid(TValue value)</code>, one per',
  'type;</li><li><code>DefaultCode</code>, the code.</li></ul>',
  '',
  'Runs them <em>all</em>; <a href="https://learn.microsoft.com/dotnet/csharp/language-reference/keywords/null">null</a> means none.',
  'Resolves <code>IQuery&lt;TResponse&gt;</code>.',
  '',
  '[B.Value](SkathIO.Rogue.Jobs.B\\-1.md\\#SkathIO\\_Rogue\\_Jobs\\_B\\_1\\_Value), [Gone](SkathIO.Rogue.Gone.md)',
  '',
  '```csharp',
  'public class A { /* <code>kept</code> [X](SkathIO.Rogue.A.md) */ }',
  '```',
  '',
].join('\n')

const FILES = {
  'toc.yml': TOC,
  'SkathIO.Rogue.md': '# <a id="SkathIO_Rogue"></a> Namespace SkathIO.Rogue\n',
  'SkathIO.Rogue.Jobs.md': '# <a id="SkathIO_Rogue_Jobs"></a> Namespace SkathIO.Rogue.Jobs\n',
  'SkathIO.Rogue.A.md': PAGE_A,
  'SkathIO.Rogue.Jobs.B-1.md': '# <a id="SkathIO_Rogue_Jobs_B_1"></a> Struct B<T\\>\n',
}

test('a <ul> list becomes a Markdown list with code spans', () => {
  const { result, page } = run(FILES)
  assert.equal(result.status, 0, result.stderr)
  assert.match(page, /\n- `IsValid\(TValue value\)`, one per type;\n- `DefaultCode`, the code\.\n/)
  assert.doesNotMatch(page.split('```')[0], /<\/?(?:ul|li|code|em|a)\b/)
})

test('emphasis, keyword links and entities in code become Markdown', () => {
  const { page } = run(FILES)
  assert.match(page, /Runs them \*all\*; `null` means none\./)
  assert.match(page, /Resolves `IQuery<TResponse>`\./)
})

test('a link to a member anchor goes to the type page, and a page not written becomes code', () => {
  const { page } = run(FILES)
  assert.match(page, /\[B\.Value\]\(\.\/skathio-rogue-jobs-b-1\.md\), `Gone`/)
})

test('fenced code is left as docfx wrote it', () => {
  const { page } = run(FILES)
  assert.match(page, /public class A \{ \/\* <code>kept<\/code> \[X\]\(SkathIO\.Rogue\.A\.md\) \*\/ \}/)
})

test('each namespace in the toc gets its own sidebar group', () => {
  const { sidebar } = run(FILES)
  assert.deepEqual(sidebar, [
    { text: 'SkathIO.Rogue', slug: 'skathio-rogue', items: [{ text: 'Classes', items: [{ text: 'A', slug: 'skathio-rogue-a' }] }] },
    {
      text: 'SkathIO.Rogue.Jobs',
      slug: 'skathio-rogue-jobs',
      items: [{ text: 'Structs', items: [{ text: 'B&lt;T>', slug: 'skathio-rogue-jobs-b-1' }] }],
    },
  ])
})

test('a title with a generic name stores `<` as an entity', () => {
  const dir = run({ ...FILES, 'SkathIO.Rogue.A.md': '# <a id="x"></a> Class A<T\\>\n' })
  assert.match(dir.page, /^---\ntitle: "Class A&lt;T>"\n---\n\n# Class A<T\\>\n$/)
})

test('missing arguments fail with the usage', () => {
  const { result } = run(FILES, [])
  assert.notEqual(result.status, 0)
  assert.match(result.stderr, /usage: node clean-api\.mjs <docfx-md-dir> <out-dir>/)
})

test('a toc entry with no docfx page fails and names it', () => {
  const { result } = run({ ...FILES, 'toc.yml': `${TOC}- name: SkathIO.Rogue.Missing\n  href: SkathIO.Rogue.Missing.md\n` })
  assert.notEqual(result.status, 0)
  assert.match(result.stderr, /SkathIO\.Rogue\.Missing\.md/)
})
