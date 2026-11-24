// The sidebar label for a guide that has no short label in config.mts: its first `# ` heading,
// skipping fenced code blocks (a `# comment` line in a bash sample is not a heading), with code
// and emphasis marks dropped. VitePress renders sidebar text as HTML, so `&` and `<` are written
// as entities, as clean-api.mjs does for the API labels.
const FENCE = /^ {0,3}(`{3,}|~{3,})/

export function h1Label(markdown, fallback) {
  let fence = null
  for (const line of markdown.split(/\r?\n/)) {
    const marker = line.match(FENCE)?.[1]
    if (fence) {
      // A fence closes on a line of the same character, at least as long, and nothing else.
      if (marker && marker[0] === fence[0] && marker.length >= fence.length && line.trim() === marker) {
        fence = null
      }
      continue
    }
    if (marker) {
      fence = marker
      continue
    }
    const heading = line.match(/^# (.*)$/)?.[1]
    if (heading !== undefined) {
      return heading.replace(/[`*]/g, '').replace(/&/g, '&amp;').replace(/</g, '&lt;')
    }
  }
  return fallback
}
