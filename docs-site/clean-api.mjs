// Turns the Markdown that `docfx metadata` writes (outputFormat: markdown) into pages VitePress
// renders as they are, plus a sidebar.json in docfx's toc order.
//
// Usage: node docs-site/clean-api.mjs <docfx-md-dir> <out-dir>
//
// - File names become lowercase slugs (SkathIO.Rogue.ICommand-1.md -> skathio-rogue-icommand-1.md).
// - <xref> becomes a link to the page of the type it names (or declares the member it names),
//   or inline code when the type is not one of ours. Links to learn.microsoft.com become inline
//   code too: types outside our packages are named, not linked.
// - The HTML that docfx keeps from the /// comments (<p>, <b>, <em>, <code>, <ul>/<li>) becomes
//   Markdown, because CommonMark parses no Markdown inside an HTML block.
// - The H1 loses its <a id> anchor and is copied into a `title` field, without Markdown escapes
//   and with `<` written as &lt;, because VitePress reads the title as inline Markdown.
// - sidebar.json groups the pages by namespace, then by kind, as toc.yml lists them, with `<`
//   escaped in labels: VitePress renders sidebar labels as HTML.
import { mkdirSync, readFileSync, readdirSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'

const LEARN = /^https:\/\/learn\.microsoft\.com\//

function fail(message) {
  process.stderr.write(`clean-api: ${message}\n`)
  process.exit(1)
}

// SkathIO.Rogue.ICommand-1.md -> skathio-rogue-icommand-1
function slug(file) {
  return file.slice(0, -3).toLowerCase().replace(/[^a-z0-9-]+/g, '-').replace(/^-+|-+$/g, '')
}

// Drops Markdown backslash escapes: ICommand<TResponse\> -> ICommand<TResponse>.
function unescapeMd(text) {
  return text.replace(/\\([!-/:-@[-`{-~])/g, '$1')
}

function decodeEntities(text) {
  return text.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&#39;/g, "'").replace(/&amp;/g, '&')
}

function code(text) {
  const plain = text.replace(/\s+/g, ' ').trim()
  return plain.includes('`') ? `\`\` ${plain} \`\`` : `\`${plain}\``
}

// docfx writes a type's uid with `n for its arity, its page with -n: ICommand`1 <-> ICommand-1.md.
function pagesByUid(files) {
  return new Map(files.map((f) => [f.slice(0, -3).replace(/-(\d+)$/, '`$1'), f]))
}

function shortName(uid) {
  return uid.split('.').pop().replace(/`+\d+/g, '')
}

// The page for a uid: the type's own page, or, for a member, the page of the type before the
// last dot outside the parameter list.
function resolveXref(uid, byUid) {
  if (byUid.has(uid)) {
    return { file: byUid.get(uid), label: shortName(uid) }
  }
  const head = uid.split('(')[0]
  const type = head.slice(0, head.lastIndexOf('.'))
  if (byUid.has(type)) {
    const member = shortName(head)
    return { file: byUid.get(type), label: member === '#ctor' ? shortName(type) : `${shortName(type)}.${member}` }
  }
  return { file: null, label: shortName(head) }
}

function xrefToMarkdown(attrs, byUid) {
  const href = decodeURIComponent(attrs.match(/\bhref="([^"]*)"/)?.[1] ?? '')
  if (!href) {
    return code(decodeEntities(attrs.match(/\bdata-raw-source="([^"]*)"/)?.[1] ?? 'unresolved reference'))
  }
  const { file, label } = resolveXref(href, byUid)
  return file ? `[${label}](./${slug(file)}.md)` : code(label)
}

function list(inner) {
  const items = [...inner.matchAll(/<li>([\s\S]*?)<\/li>/g)].map((m) => `- ${m[1].replace(/\s+/g, ' ').trim()}`)
  return `\n\n${items.join('\n')}\n\n`
}

// HTML from the /// comments, turned into Markdown. Lists and paragraphs go first, so the inline
// tags inside them are converted in the same pass as the rest of the page.
function htmlToMarkdown(text) {
  return text
    .replace(/<ul>([\s\S]*?)<\/ul>/g, (_, inner) => list(inner))
    .replace(/<p>([\s\S]*?)<\/p>/g, (_, inner) => `\n\n${inner.trim()}\n\n`)
    .replace(/<code\b[^>]*>([\s\S]*?)<\/code>/g, (_, inner) => code(decodeEntities(inner)))
    .replace(/<b>([\s\S]*?)<\/b>/g, '**$1**')
    .replace(/<em>([\s\S]*?)<\/em>/g, '*$1*')
    .replace(/<a href="([^"]*)">([\s\S]*?)<\/a>/g, (whole, href, inner) => (LEARN.test(href) ? code(decodeEntities(inner)) : whole))
}

// [Text](SkathIO.Rogue.X\-1.md) -> [Text](./skathio-rogue-x-1.md); a learn.microsoft.com link,
// or a docfx page that was not written, -> `Text`. docfx's member anchors (X.md#SkathIO_...)
// are dropped: VitePress names heading anchors its own way, so the link goes to the type page.
function rewriteLinks(text, written) {
  return text.replace(/\[((?:\\.|[^\]\\])*)\]\(((?:\\.|[^)\s\\])+)\)/g, (whole, label, rawHref) => {
    const href = unescapeMd(rawHref)
    if (LEARN.test(href)) {
      return code(unescapeMd(label))
    }
    const file = href.split('#')[0]
    if (/^[a-z]+:/i.test(href) || !file.endsWith('.md')) {
      return whole
    }
    return written.has(file) ? `[${label}](./${slug(file)}.md)` : code(unescapeMd(label))
  })
}

// docfx's own links are rewritten before the xrefs become links, which already point at slugs.
function cleanProse(text, byUid, written) {
  return rewriteLinks(htmlToMarkdown(text), written)
    .replace(/<xref\b([^>]*)>[\s\S]*?<\/xref>/g, (_, attrs) => xrefToMarkdown(attrs, byUid))
    .replace(/<a id="[^"]*"><\/a>\s*/g, '')
    .replace(/\n{3,}/g, '\n\n')
}

// Fenced code blocks are left as they are; only the prose between them is cleaned.
function cleanPage(text, byUid, written) {
  return text
    .split(/(^```[\s\S]*?^```$)/m)
    .map((part, i) => (i % 2 === 1 ? part : cleanProse(part, byUid, written)))
    .join('')
}

function writePage({ srcDir, outDir, byUid, written }, file) {
  const body = cleanPage(readFileSync(join(srcDir, file), 'utf8'), byUid, written)
  const h1 = body.match(/^# (.+)$/m)
  if (!h1) {
    fail(`${file} has no H1 heading: every docfx page should start with "# <kind> <name>".`)
  }
  // VitePress reads the title as inline Markdown, where a raw `<T>` would be an HTML tag.
  const title = unescapeMd(h1[1].trim()).replace(/&/g, '&amp;').replace(/</g, '&lt;')
  const page = `---\ntitle: ${JSON.stringify(title)}\n---\n\n${body.slice(h1.index).trimEnd()}\n`
  writeFileSync(join(outDir, `${slug(file)}.md`), page)
}

function yamlValue(raw) {
  const value = raw.trim()
  if (value.startsWith('"')) {
    return JSON.parse(value)
  }
  return value.startsWith("'") ? value.slice(1, -1).replace(/''/g, "'") : value
}

// docfx's toc.yml: a namespace at the top level, then under it a kind label (no href)
// followed by the types of that kind.
function sidebar(toc, written) {
  const groups = []
  const lines = toc.split('\n')
  lines.forEach((line, i) => {
    const m = line.match(/^(\s*)- name: (.+)$/)
    if (!m) {
      return
    }
    const text = yamlValue(m[2]).replace(/</g, '&lt;')
    const href = lines[i + 1]?.match(/^\s+href: (.+)$/)?.[1]
    const file = href ? yamlValue(href) : null
    if (file && !written.has(file)) {
      fail(`toc.yml lists ${file}, which is not in the docfx output.`)
    }
    if (m[1] === '') {
      groups.push({ text, slug: file ? slug(file) : null, items: [] })
    } else if (!file) {
      groups.at(-1)?.items.push({ text, items: [] })
    } else {
      const parent = groups.at(-1)
      const kind = parent?.items.at(-1)
      ;(kind ?? parent)?.items.push({ text, slug: slug(file) })
    }
  })
  return groups
}

// Reads through `read`, and on a file-system error names what was being read instead of
// leaving a stack trace in the build log.
function reading(what, read) {
  try {
    return read()
  } catch (error) {
    return fail(`cannot ${what}: ${error.message}`)
  }
}

function main([srcDir, outDir]) {
  if (!srcDir || !outDir) {
    fail('usage: node clean-api.mjs <docfx-md-dir> <out-dir>')
  }
  const files = reading(`read the docfx output folder ${srcDir}`, () => readdirSync(srcDir)).filter((f) => f.endsWith('.md')).sort()
  // No pages means docfx wrote nothing: stop, or the site would be built without its API reference.
  if (files.length === 0) {
    fail(`${srcDir} holds no docfx Markdown pages: run "dotnet docfx metadata docs-site/docfx.json" first.`)
  }
  const toc = reading(`read docfx's toc.yml in ${srcDir}`, () => readFileSync(join(srcDir, 'toc.yml'), 'utf8'))
  const written = new Set(files)
  const run = { srcDir, outDir, byUid: pagesByUid(files), written }
  reading(`create the output folder ${outDir}`, () => mkdirSync(outDir, { recursive: true }))
  for (const file of files) {
    writePage(run, file)
  }
  const groups = sidebar(toc, written)
  writeFileSync(join(outDir, 'sidebar.json'), `${JSON.stringify(groups, null, 1)}\n`)
  process.stdout.write(`clean-api: ${files.length} pages, ${groups.length} namespaces\n`)
}

main(process.argv.slice(2))
