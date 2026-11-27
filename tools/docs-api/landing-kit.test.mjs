// Rules for the landing page on hashira's site kit: site/index.html loads _kit/tokens.css,
// _kit/theme.js and _kit/site-nav.js as hashira's Pages guide says, and renders one <site-nav>
// with the same name, logo, nav and home_link as .github/pages.json, so the landing and the docs
// show the same header in the same order. The landing keeps its URL (/rogue/) and its anchors,
// sets Rogue's own accent and leaves the nav's styling to the kit. The visual test serves the
// landing with the kit and shoots it at 390x844 and 1280x800 in light and dark, and CLAUDE.md,
// the visual-test skill and the changelog describe the new build.
//
// The landing is read as text: the cases find the <site-nav> element, its attributes and its
// inline JSON child the way site-nav.js reads them (the JSON child first, attributes over it).
import { test, describe } from 'node:test'
import assert from 'node:assert/strict'
import { existsSync, readFileSync } from 'node:fs'
import { join, normalize, resolve } from 'node:path'

const root = resolve(import.meta.dirname, '..', '..')
const LANDING = 'site/index.html'
const PAGES_JSON = '.github/pages.json'
const HOMEPAGE_JSON = '.github/homepage.json'
const SCREENSHOT = '.claude/skills/visual-test/screenshot.sh'
const SKILL = '.claude/skills/visual-test/SKILL.md'
const KIT_FILES = ['_kit/tokens.css', '_kit/theme.js', '_kit/site-nav.js']
// The landing's section anchors before the move to the kit; /rogue/#<id> links must keep working.
const ANCHORS = ['three', 'install', 'quick-start', 'benchmarks', 'compared', 'packages', 'docs']
// hashira's default accent (light, and the dark fallback) that a site without its own gets.
const HASHIRA_GREENS = ['#1e7d69', '#4fd1b5']
const HEX = /^#(?:[0-9a-f]{3}|[0-9a-f]{6})$/i

const read = (rel) => readFileSync(join(root, rel), 'utf8')
const landing = () => read(LANDING)
const head = () => landing().split(/<\/head>/i)[0]
const body = () => landing().split(/<body\b[^>]*>/i)[1] ?? ''

function pagesConfig() {
  const pages = JSON.parse(read(PAGES_JSON))
  const home = existsSync(join(root, HOMEPAGE_JSON)) ? JSON.parse(read(HOMEPAGE_JSON)) : {}
  return {
    name: pages.name ?? home.name,
    logo: pages.logo ?? home.image,
    nav: pages.nav ?? [],
    home_link: pages.home_link ?? null,
  }
}

const ENTITIES = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'" }
function decode(text) {
  return text.replace(/&(#x[0-9a-f]+|#\d+|[a-z]+);/gi, (all, code) => {
    if (code[0] === '#') {
      const hex = code[1] === 'x' || code[1] === 'X'
      return String.fromCodePoint(parseInt(code.slice(hex ? 2 : 1), hex ? 16 : 10))
    }
    return ENTITIES[code.toLowerCase()] ?? all
  })
}

function attributes(tagText) {
  const out = {}
  const inner = tagText.replace(/^<[a-z-]+/i, '').replace(/\/?>$/, '')
  const attr = /([^\s=/>]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'>]+)))?/g
  for (const m of inner.matchAll(attr)) {
    out[m[1].toLowerCase()] = decode(m[2] ?? m[3] ?? m[4] ?? '')
  }
  return out
}

function siteNavs() {
  return [...body().matchAll(/(<site-nav\b[^>]*>)([\s\S]*?)<\/site-nav>/gi)]
}

// The <site-nav>'s settings, merged as site-nav.js merges them, and its other children.
function siteNav() {
  const found = siteNavs()
  assert.equal(found.length, 1, `${LANDING} must render exactly one <site-nav>`)
  const [, open, inner] = found[0]
  const script = inner.match(/<script\b[^>]*type\s*=\s*["']?application\/json["']?[^>]*>([\s\S]*?)<\/script>/i)
  const config = script ? JSON.parse(script[1]) : {}
  const attrs = attributes(open)
  for (const [key, value] of Object.entries(attrs)) {
    config[key] = key === 'nav' || key === 'home_link' ? JSON.parse(value) : value
  }
  const children = (script ? inner.replace(script[0], '') : inner).trim()
  return { config, children }
}

function scriptTags(html) {
  return [...html.matchAll(/<script\b[^>]*>/gi)].map((m) => ({ text: m[0], index: m.index, attrs: attributes(m[0]) }))
}

const sansQuery = (href) => href.split(/[?#]/)[0]

describe('the landing loads hashira site kit', () => {
  test('its <head> links _kit/tokens.css as a stylesheet', () => {
    const links = [...head().matchAll(/<link\b[^>]*>/gi)].map((m) => attributes(m[0]))
    assert.ok(links.some((l) => l.rel === 'stylesheet' && sansQuery(l.href ?? '') === '_kit/tokens.css'),
      `${LANDING} <head> has no <link rel="stylesheet" href="_kit/tokens.css">`)
  })

  test('its <head> loads _kit/theme.js with no defer, async or type=module', () => {
    const theme = scriptTags(head()).filter((s) => sansQuery(s.attrs.src ?? '') === '_kit/theme.js')
    assert.equal(theme.length, 1, `${LANDING} <head> must load _kit/theme.js once`)
    const { attrs } = theme[0]
    assert.ok(!('defer' in attrs) && !('async' in attrs) && attrs.type !== 'module',
      `_kit/theme.js must run before first paint: ${theme[0].text}`)
  })

  test('it loads _kit/site-nav.js after _kit/theme.js', () => {
    const tags = scriptTags(landing())
    const theme = tags.find((s) => sansQuery(s.attrs.src ?? '') === '_kit/theme.js')
    const nav = tags.find((s) => sansQuery(s.attrs.src ?? '') === '_kit/site-nav.js')
    assert.ok(nav, `${LANDING} does not load _kit/site-nav.js`)
    assert.ok(theme && theme.index < nav.index, '_kit/theme.js must load before _kit/site-nav.js')
  })

  test('site/ holds no _kit or docs folder of its own, which the Pages build refuses', () => {
    for (const dir of ['site/_kit', 'site/docs']) {
      assert.ok(!existsSync(join(root, dir)), `${dir} exists`)
    }
  })

  test('the kit paths are relative, so they resolve under /rogue/', () => {
    const refs = [...landing().matchAll(/(?:href|src)\s*=\s*["']?([^"'\s>]*_kit\/[^"'\s>]*)/gi)].map((m) => m[1])
    assert.ok(refs.length >= KIT_FILES.length, `${LANDING} refers to the kit fewer than ${KIT_FILES.length} times`)
    for (const ref of refs) {
      assert.ok(KIT_FILES.includes(sansQuery(ref)), `kit reference ${ref} is not one of ${KIT_FILES.join(', ')}`)
    }
  })
})

describe('the landing header is the kit <site-nav>, matching the docs header', () => {
  test('the page renders exactly one <site-nav> and no hand-written <nav>', () => {
    assert.equal(siteNavs().length, 1, `${LANDING} must render exactly one <site-nav>`)
    const outside = body().replace(/<site-nav\b[\s\S]*?<\/site-nav>/gi, '')
    assert.doesNotMatch(outside, /<nav\b/i, `${LANDING} still has a <nav> of its own beside <site-nav>`)
  })

  test('its inline settings are valid JSON', () => {
    assert.doesNotThrow(() => siteNav())
  })

  test('its name is the name in .github/pages.json', () => {
    assert.equal(siteNav().config.name, pagesConfig().name)
  })

  test('its logo is the logo in .github/pages.json, relative to the landing', () => {
    const { logo } = siteNav().config
    assert.equal(typeof logo, 'string', '<site-nav> has no logo')
    assert.equal(normalize(join('site', sansQuery(logo))), normalize(pagesConfig().logo))
  })

  test('its nav links are the nav entries of .github/pages.json, in the same order', () => {
    assert.deepEqual(siteNav().config.nav ?? [], pagesConfig().nav)
  })

  test('its home link is the home_link of .github/pages.json', () => {
    assert.deepEqual(siteNav().config.home_link ?? null, pagesConfig().home_link)
  })

  test('it shows the Docs link to docs/, which is /rogue/docs/', () => {
    const { docs } = siteNav().config
    assert.ok(docs === true || docs === '' || docs === 'docs/' || docs === './docs/',
      `<site-nav> docs is ${JSON.stringify(docs)}, not docs/`)
  })

  test('it adds no extras the docs header lacks', () => {
    assert.equal(siteNav().children, '', `<site-nav> has children besides its JSON: ${siteNav().children}`)
  })

  test('every # link in the nav opens a section of the landing', () => {
    const ids = new Set([...landing().matchAll(/\bid\s*=\s*["']?([^"'\s>]+)/gi)].map((m) => m[1]))
    for (const { href } of pagesConfig().nav.filter((e) => e.href.startsWith('#'))) {
      assert.ok(ids.has(href.slice(1)), `nav link ${href} has no id="${href.slice(1)}" on the landing`)
    }
  })

  test('the page leaves the nav styling to the kit', () => {
    const css = [...landing().matchAll(/<style\b[^>]*>([\s\S]*?)<\/style>/gi)].map((m) => m[1]).join('\n')
    assert.doesNotMatch(css, /site-nav\s*::part\(/i, 'the landing restyles parts of <site-nav>')
    assert.doesNotMatch(css, /--nav-(?:gap|pad)\s*:/i, 'the landing overrides the kit nav spacing')
  })
})

describe('the landing keeps its URL and its own accent', () => {
  test('it is still served at /rogue/: site/index.html with the title Rogue', () => {
    assert.match(landing(), /<title>Rogue<\/title>/)
  })

  test('every section anchor of the landing is still there', () => {
    const ids = new Set([...landing().matchAll(/\bid\s*=\s*["']?([^"'\s>]+)/gi)].map((m) => m[1]))
    for (const id of ANCHORS) {
      assert.ok(ids.has(id), `/rogue/#${id} no longer opens a section`)
    }
  })

  test('it sets an accent of its own, not hashira green', () => {
    const css = [...landing().matchAll(/<style\b[^>]*>([\s\S]*?)<\/style>/gi)].map((m) => m[1]).join('\n')
    const declared = [...css.matchAll(/--(?:site-)?accent(?:-dark)?\s*:\s*(#[0-9a-f]+)/gi)].map((m) => m[1])
    const attr = siteNavs().length === 1 ? siteNav().config.accent : undefined
    const accents = [...declared, ...(typeof attr === 'string' ? [attr] : [])]
    assert.ok(accents.length > 0, 'the landing sets no --site-accent, --accent or <site-nav> accent')
    for (const accent of accents) {
      assert.match(accent, HEX, `accent ${accent} is not #rgb or #rrggbb`)
      assert.ok(!HASHIRA_GREENS.includes(accent.toLowerCase()), `accent ${accent} is hashira green`)
    }
  })
})

describe('the visual test follows the new build', () => {
  test('screenshot.sh shoots 390x844 and 1280x800', () => {
    const text = read(SCREENSHOT)
    assert.match(text, /390\D{1,3}844/, 'screenshot.sh has no 390x844 viewport')
    assert.match(text, /1280\D{1,3}800/, 'screenshot.sh has no 1280x800 viewport')
  })

  test('screenshot.sh shoots light and dark', () => {
    const text = read(SCREENSHOT)
    assert.match(text, /\blight\b/)
    assert.match(text, /\bdark\b/)
  })

  test('screenshot.sh serves the landing with the kit', () => {
    const text = read(SCREENSHOT)
    const servesBuiltPages = /artifacts\/pages(?!\/docs)/.test(text) && !/\/site\/\*/.test(text)
    assert.ok(/_kit/.test(text) || servesBuiltPages,
      'screenshot.sh serves site/ without _kit, so the landing would show no <site-nav>')
  })

  test('the visual-test skill names the 390x844 and 1280x800 sizes and the kit', () => {
    const text = read(SKILL)
    assert.match(text, /390\s*[×x]\s*844/)
    assert.match(text, /1280\s*[×x]\s*800/)
    assert.match(text, /_kit|site-nav|site kit|site-kit/i)
  })
})

describe('CLAUDE.md and the changelog describe the new build', () => {
  test('CLAUDE.md says the landing uses the site kit', () => {
    assert.match(read('CLAUDE.md'), /site-nav|tokens\.css/i)
  })

  test('CLAUDE.md visual testing names the 1280 desktop width', () => {
    const section = read('CLAUDE.md').split(/^## Visual testing/m)[1]?.split(/^## /m)[0] ?? ''
    assert.ok(section, 'CLAUDE.md has no Visual testing section')
    assert.match(section, /1280/)
  })

  test('the changelog [Unreleased] section records the landing on the site kit', () => {
    const unreleased = read('CHANGELOG.md').match(/^## \[Unreleased\]\s*$([\s\S]*?)(?=^## \[)/m)
    assert.ok(unreleased, 'CHANGELOG.md has no [Unreleased] section followed by a release')
    assert.match(unreleased[1], /site-nav|site kit|site-kit|_kit/i)
  })
})
