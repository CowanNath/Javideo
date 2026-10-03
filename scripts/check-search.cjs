// Run with: node scripts/check-search.cjs
const assert = require('node:assert/strict')
const fs = require('node:fs')
const vm = require('node:vm')
const ts = require('typescript')
const { parse } = require('@vue/compiler-sfc')
const vue = require('vue')

function deferred() {
  let resolve, reject
  const promise = new Promise((ok, fail) => { resolve = ok; reject = fail })
  return { promise, resolve, reject }
}
async function until(condition) {
  for (let i = 0; i < 100; i++) {
    if (condition()) return
    await new Promise(resolve => setImmediate(resolve))
  }
  throw new Error('Timed out waiting for mock request')
}

async function main() {
  const store = vue.reactive({ query: '', movie: null, grouped: [], trailerUrl: '', lastSearchAt: 0 })
  const translations = [], scrapes = [], trailers = []
  const pendingScrapes = new Map()
  const firstTrailer = deferred()
  const context = {
    ...vue, onMounted: fn => fn(), onUnmounted: () => {},
    useSearchStore: () => store, useLibraryStore: () => vue.reactive({ items: [] }),
    useRouter: () => ({}), t: key => key, MetatubeError: class extends Error {},
    moviesApi: { byNumber: async () => null },
    actorsApi: { resolveAvatars: async () => ({}) }, absoluteAvatar: async x => x,
    magnet: { searchGrouped: async () => [] }, AbortController,
    translate: { run: (title, summary) => { const d = deferred(); translations.push({ title, ...d }); return d.promise } },
    metatube: {
      candidates: async () => [{ provider: 'mock', id: 'A' }, { provider: 'mock', id: 'B' }],
      scrapeByProvider: async (number, provider, id) => {
        scrapes.push({ number, id })
        if (pendingScrapes.has(id)) return pendingScrapes.get(id).promise
        return { number, title: id, summary: 'summary' }
      },
      findTrailer: async (number, signal) => { trailers.push({ number, signal }); return trailers.length === 1 ? firstTrailer.promise : { ok: false, url: null } },
    },
  }
  const script = parse(fs.readFileSync('src/views/SearchView.vue', 'utf8')).descriptor.scriptSetup.content
    .replace(/^import .*$/gm, '')
  const js = ts.transpileModule(script, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.None } }).outputText
  const page = vm.runInNewContext(`(function() { ${js}; return { search, pickCandidate, query, stepTranslate, scraping, error }; })()`, context)
  let visibleTitle
  // Match render dependencies: the spinner/progress can also cause a render.
  vue.effect(() => { page.scraping.value; page.stepTranslate.value; visibleTitle = store.movie?.title })

  page.query.value = 'QA-001'
  const initial = page.search()
  await until(() => translations.length === 1)
  translations[0].resolve({ title: '中文 A' })
  await until(() => page.stepTranslate.value === 1)
  assert.equal(visibleTitle, '中文 A')

  page.query.value = 'UNSUBMITTED-INPUT'
  const second = page.pickCandidate(1)
  await until(() => translations.length === 2)
  assert.equal(scrapes.at(-1).number, 'QA-001')
  // The original search finishes its trailer while the new result is translating.
  firstTrailer.resolve({ ok: false, url: null })
  await initial
  translations[1].resolve({ title: '中文 B' })
  await second
  assert.equal(visibleTitle, '中文 B', 'Switching results must visibly update translated fields')
  assert.equal(page.stepTranslate.value, 1)

  const late = deferred()
  pendingScrapes.set('A', late)
  const slowPick = page.pickCandidate(0)
  const fastPick = page.pickCandidate(1)
  await until(() => translations.length === 3)
  translations[2].resolve({ title: '最新 B' })
  await fastPick
  late.resolve({ number: 'QA-001', title: 'stale A' })
  await slowPick
  assert.equal(visibleTitle, '最新 B', 'Late scrape must not overwrite the selected result')
  pendingScrapes.clear()

  const oldPick = page.pickCandidate(0)
  await until(() => translations.length === 4)
  page.query.value = 'QA-002'
  const nextSearch = page.search()
  await until(() => translations.length === 5)
  assert.equal(trailers[0].signal.aborted, true)
  translations[4].resolve({ title: '新搜索' })
  await nextSearch
  translations[3].reject(new Error('stale failure'))
  await oldPick
  assert.equal(visibleTitle, '新搜索')
  assert.equal(page.stepTranslate.value, 1, 'Old translation failure must not change current progress')
  assert.equal(page.error.value, '')

  const failedPick = page.pickCandidate(1)
  await until(() => translations.length === 6)
  translations[5].reject(new Error('mock translator unavailable'))
  await failedPick
  assert.equal(page.stepTranslate.value, -1)
  assert.equal(page.scraping.value, false)
  console.log('PASS: translation reactivity, candidate progress, submitted query, stale scrape/translation, trailer cancellation and failure recovery')
}
main().catch(error => { console.error(error); process.exitCode = 1 })
