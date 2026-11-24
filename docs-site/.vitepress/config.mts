import { existsSync, readFileSync, readdirSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { type DefaultTheme, defineConfig } from 'vitepress'
import { h1Label } from '../h1-label.mjs'

// The docs are read from the repo's docs/ as they are on GitHub: no front matter and no
// link edits. build.sh stages them (with the logo) under artifacts/, because VitePress
// reads one source folder and resolves node modules from the sources.
const repoPath = (p: string) => fileURLToPath(new URL(`../../${p}`, import.meta.url))
const stage = repoPath('artifacts/docs-site')

const GITHUB = 'https://github.com/skathio/rogue/blob/main/'
const unpublished = ['governance.md', 'roadmap.md']

// Short sidebar labels, in reading order. A published page missing here is still listed,
// under its H1, so a new guide never drops out of the sidebar.
const guides: [string, string][] = [
  ['getting-started.md', 'Getting started'],
  ['behaviors.md', 'Behaviors'],
  ['jobs.md', 'Jobs'],
  ['messaging.md', 'Messaging'],
  ['messaging-rabbitmq.md', 'RabbitMQ'],
  ['outbox.md', 'Outbox and inbox'],
  ['validation.md', 'Validation'],
  ['api-reference.md', 'API reference'],
  ['migration-guide.md', 'Migrating from MediatR'],
  ['benchmarks.md', 'Benchmarks'],
]
const h1Of = (md: string) => h1Label(readFileSync(repoPath(`docs/${md}`), 'utf8'), md)
const unlisted = readdirSync(repoPath('docs'))
  .filter((f) => f.endsWith('.md') && !unpublished.includes(f) && !guides.some(([g]) => g === f))
  .sort()
  .map((f): [string, string] => [f, h1Of(f)])
// /rogue/docs/ opens Getting started.
const rewrites: Record<string, string> = { 'getting-started.md': 'index.md' }
const pageLink = (md: string) => `/${(rewrites[md] ?? md).replace(/(index)?\.md$/, '')}`

// build.sh generates the API pages and their sidebar (namespace, then kind, then type, in
// docfx's toc order) with clean-api.mjs. Labels arrive with `<` already escaped.
type ApiEntry = { text: string; slug?: string | null; items?: ApiEntry[] }
const apiSidebarFile = `${stage}/src/api/sidebar.json`
if (!existsSync(apiSidebarFile)) {
  throw new Error(`${apiSidebarFile} is missing: build the docs with docs-site/build.sh.`)
}
const toSidebar = ({ text, slug, items }: ApiEntry): DefaultTheme.SidebarItem => ({
  text,
  ...(slug ? { link: pageLink(`api/${slug}.md`) } : {}),
  ...(items ? { items: items.map(toSidebar) } : {}),
})
const apiSidebar = (JSON.parse(readFileSync(apiSidebarFile, 'utf8')) as ApiEntry[])
  .map((ns) => ({ ...toSidebar(ns), collapsed: true }))

// Links that leave docs/ (../X) or reach an unpublished page go to the file on GitHub,
// keeping any #anchor. Every other relative link is left to VitePress's dead-link check.
const outbound = new RegExp(`^(?:\\./)?(\\.\\./[^#]*|(?:${unpublished.map((f) => f.replace('.', '\\.')).join('|')}))(#.*)?$`)
function githubUrl(href: string): string | null {
  const m = href.match(outbound)
  if (!m) {
    return null
  }
  return GITHUB + (m[1].startsWith('../') ? m[1].slice(3) : `docs/${m[1]}`) + (m[2] ?? '')
}

// VitePress moves a rewritten page but leaves the links to it alone, so point them at
// the page's new path. The dead-link check still sees the result.
function rewrittenLink(href: string): string | null {
  const m = href.match(/^(?:\.\/)?([\w-]+\.md)(#.*)?$/)
  return m && rewrites[m[1]] ? `./${rewrites[m[1]]}${m[2] ?? ''}` : null
}

// The landing page keeps its theme under `theme` (auto, light or dark). Before VitePress's
// own dark-mode check runs, map it onto VitePress's key, so a stored light or dark opens
// the docs in that theme with no flash. Anything else means auto.
const themeFromLanding = `try{var t=localStorage.getItem('theme');localStorage.setItem('vitepress-theme-appearance',t==='light'||t==='dark'?t:'auto')}catch(e){}`
// When the visitor flips the docs toggle, store light or dark under the landing key. The
// observer starts after load, so the initial class does not count as a choice.
const themeToLanding = `document.addEventListener('DOMContentLoaded',function(){var r=document.documentElement,d=r.classList.contains('dark');new MutationObserver(function(){var n=r.classList.contains('dark');if(n===d){return}d=n;try{localStorage.setItem('theme',n?'dark':'light')}catch(e){}}).observe(r,{attributes:true,attributeFilter:['class']})})`

export default defineConfig({
  srcDir: `${stage}/src`,
  srcExclude: unpublished,
  outDir: `${stage}/dist`,
  cacheDir: `${stage}/cache`,
  base: '/rogue/docs/',
  rewrites,
  title: 'Rogue',
  description: 'Guides for SkathIO.Rogue: an AOT-safe, source-generated mediator, background jobs and messaging for .NET.',
  head: [
    ['link', { rel: 'icon', href: '/rogue/docs/mark.svg', type: 'image/svg+xml' }],
    ['script', {}, themeFromLanding],
    ['script', {}, themeToLanding],
  ],
  themeConfig: {
    logo: '/mark.svg',
    siteTitle: 'Rogue',
    // The landing page sits one level above the docs base, outside VitePress's router.
    logoLink: { link: '/rogue/', target: '_self' },
    nav: [{ text: '← Rogue', link: '/../', target: '_self' }, { text: 'API', link: '/api/skathio-rogue' }],
    search: { provider: 'local' },
    sidebar: [
      { text: 'Guides', items: [...guides, ...unlisted].map(([md, text]) => ({ text, link: pageLink(md) })) },
      ...apiSidebar,
    ],
    outline: [2, 3],
    socialLinks: [{ icon: 'github', link: 'https://github.com/skathio/rogue' }],
  },
  markdown: {
    config: (md) => {
      md.core.ruler.push('outbound-and-rewritten-links', (state) => {
        for (const block of state.tokens) {
          for (const token of block.children ?? []) {
            const href = token.type === 'link_open' ? token.attrGet('href') ?? '' : ''
            const url = href && (githubUrl(href) ?? rewrittenLink(href))
            if (url) {
              token.attrSet('href', url)
            }
          }
        }
      })
      // VitePress labels a heading's permalink with the heading's Markdown source, backslash
      // escapes and code marks included. A screen reader should hear the heading as it reads.
      md.core.ruler.push('permalink-labels', (state) => {
        for (const block of state.tokens) {
          const permalink = block.children?.find((t) => t.type === 'link_open' && t.attrGet('class') === 'header-anchor')
          if (permalink) {
            const heading = block.children!.filter((t) => t.type === 'text' || t.type === 'code_inline').map((t) => t.content).join('').trim()
            permalink.attrSet('aria-label', `Permalink to "${heading}"`)
          }
        }
      })
    },
  },
})
