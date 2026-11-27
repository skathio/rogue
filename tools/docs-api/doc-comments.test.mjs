// The /// comments the API reference is generated from: no doc comment on a public member cites
// a planning note, since those notes are not in the public repo and the comment becomes an API
// page. The reason is written in words instead.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'

const root = resolve(import.meta.dirname, '..', '..')

const sourceFiles = () =>
  spawnSync('git', ['-C', root, 'ls-files', '-co', '--exclude-standard', '-z', '--', 'src'], { encoding: 'utf8' })
    .stdout.split('\0').filter((f) => f.endsWith('.cs'))

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
