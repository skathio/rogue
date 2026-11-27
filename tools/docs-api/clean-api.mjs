// Turns the Markdown that `docfx metadata` writes (outputFormat: markdown) into pages that
// hashira's docs-build publishes as they are. The docs_pre_build_command in site.yml (and the
// same command in ci.yml and publish.yml) runs it with <out-dir> set to $DOCS_STAGE/api, so the
// pages are served under /rogue/docs/api/.
//
// Usage: node tools/docs-api/clean-api.mjs <docfx-md-dir> <out-dir> [--sidebar <file> <pages.json>]
//
// - File names become lowercase slugs (SkathIO.Rogue.ICommand-1.md -> skathio-rogue-icommand-1.md).
// - <xref> becomes a link to the page of the type it names (or declares the member it names),
//   or inline code when the type is not one of ours. Links to learn.microsoft.com become inline
//   code too: types outside our packages are named, not linked.
// - The HTML that docfx keeps from the /// comments (<p>, <b>, <em>, <code>, <ul>/<li>) becomes
//   Markdown, because CommonMark parses no Markdown inside an HTML block.
// - The H1 loses its <a id> anchor and docfx's `\>` escape, and starts the page: docs-build takes
//   a page's title and its sidebar label from that first heading as written, and adds nothing
//   for front matter.
// - Only Markdown pages are written to <out-dir>: docs-build refuses any other file in the docs
//   stage. Each namespace page lists its types by kind.
// - With --sidebar, it also writes the docs sidebar to <file> ($DOCS_STAGE/.sidebar.json, which
//   docs-build reads in place of pages.json's sidebar and never publishes): pages.json's own
//   sidebar entries (the guides) first, then one folded group per namespace, in docfx's toc.yml
//   order, holding the namespace page and a group of types per kind, each labelled with its
//   short name. Labels go in as plain text: docs-build escapes them.
import { mkdirSync, readFileSync, readdirSync, writeFileSync } from 'node:fs'
import { join, relative } from 'node:path'

const LEARN = /^https:\/\/learn\.microsoft\.com\//
// docs-build's longest sidebar label.
const MAX_LABEL = 60

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
  // docs-build labels the sidebar entry with the H1 as written, so docfx's `\>` escape would
  // show there; a `>` needs no escape in a heading.
  const page = `# ${h1[1].replace(/\\>/g, '>')}${body.slice(h1.index + h1[0].length)}`
  writeFileSync(join(outDir, `${slug(file)}.md`), `${page.trimEnd()}\n`)
}

function yamlValue(raw) {
  const value = raw.trim()
  if (value.startsWith('"')) {
    return JSON.parse(value)
  }
  return value.startsWith("'") ? value.slice(1, -1).replace(/''/g, "'") : value
}

const label = (text) => (text.length > MAX_LABEL ? `${text.slice(0, MAX_LABEL - 1)}…` : text)

// docfx's toc.yml lists a namespace at the top level, then under it a kind label (no href)
// followed by the types of that kind. `page` is the stage path of a docfx page.
function apiSidebar(toc, written, page) {
  const groups = []
  const lines = toc.split('\n')
  lines.forEach((line, i) => {
    const m = line.match(/^(\s*)- name: (.+)$/)
    if (!m) {
      return
    }
    const text = label(yamlValue(m[2]))
    const href = lines[i + 1]?.match(/^\s+href: (.+)$/)?.[1]
    const file = href ? yamlValue(href) : null
    if (file && !written.has(file)) {
      fail(`toc.yml lists ${file}, which is not in the docfx output.`)
    }
    const namespace = groups.at(-1)
    if (m[1] === '') {
      groups.push({ label: text, collapsed: true, items: file ? [{ file: page(file), label: 'Overview' }] : [] })
    } else if (!namespace) {
      fail(`toc.yml lists ${text} before any namespace.`)
    } else if (!file) {
      namespace.items.push({ label: text, items: [] })
    } else {
      const kind = namespace.items.at(-1)
      ;(kind?.items ?? namespace.items).push({ file: page(file), label: text })
    }
  })
  // A kind label with no type under it would be an empty group, which docs-build refuses.
  return groups.map((g) => ({ ...g, items: g.items.filter((e) => !e.items || e.items.length > 0) })).filter((g) => g.items.length > 0)
}

// The guides of pages.json's sidebar, then the API groups, written where --sidebar says.
function writeSidebar({ srcDir, outDir, written }, [sidebarFile, pagesFile]) {
  const toc = reading(`read docfx's toc.yml in ${srcDir}`, () => readFileSync(join(srcDir, 'toc.yml'), 'utf8'))
  const guides = reading(`read ${pagesFile}`, () => JSON.parse(readFileSync(pagesFile, 'utf8'))).sidebar ?? []
  const base = relative(join(sidebarFile, '..'), outDir).split('\\').join('/')
  const groups = apiSidebar(toc, written, (file) => `${base}/${slug(file)}.md`)
  writeFileSync(sidebarFile, `${JSON.stringify([...guides, ...groups], null, 1)}\n`)
  return groups.length
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

function main([srcDir, outDir, flag, ...sidebarArgs]) {
  if (!srcDir || !outDir || (flag !== undefined && (flag !== '--sidebar' || sidebarArgs.length !== 2))) {
    fail('usage: node clean-api.mjs <docfx-md-dir> <out-dir> [--sidebar <file> <pages.json>]')
  }
  const files = reading(`read the docfx output folder ${srcDir}`, () => readdirSync(srcDir)).filter((f) => f.endsWith('.md')).sort()
  // No pages means docfx wrote nothing: stop, or the site would be built without its API reference.
  if (files.length === 0) {
    fail(`${srcDir} holds no docfx Markdown pages: run "dotnet docfx metadata tools/docs-api/docfx.json" first.`)
  }
  const run = { srcDir, outDir, byUid: pagesByUid(files), written: new Set(files) }
  reading(`create the output folder ${outDir}`, () => mkdirSync(outDir, { recursive: true }))
  for (const file of files) {
    writePage(run, file)
  }
  const namespaces = flag ? `, ${writeSidebar(run, sidebarArgs)} namespaces in the sidebar` : ''
  process.stdout.write(`clean-api: ${files.length} pages${namespaces}\n`)
}

main(process.argv.slice(2))
