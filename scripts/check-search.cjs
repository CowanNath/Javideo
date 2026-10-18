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

  const gateStore = vue.reactive({ query: '', movie: null, grouped: [], trailerUrl: '', lastSearchAt: 0 })
  const calls = []
  let metadata = deferred(), translation = deferred(), magnets = deferred(), trailer = deferred()
  let candidateMode = 'found', localMovie = null
  const gated = vm.runInNewContext(`(function() { ${js}; return { search, pickCandidate, query, stepScrape, stepTranslate, stepMagnet, stepTrailer, scraping, magLoading }; })()`, {
    ...context,
    useSearchStore: () => gateStore,
    moviesApi: {
      byNumber: async () => localMovie,
      trailerUrl: async () => { calls.push('local-trailer'); return '/local-trailer' },
    },
    translate: { run: () => { calls.push('translate'); return translation.promise } },
    magnet: { searchGrouped: () => { calls.push('magnet'); return magnets.promise } },
    metatube: {
      candidates: async () => {
        calls.push('candidates')
        if (candidateMode === 'error') throw Error('metadata search failed')
        return candidateMode === 'empty' ? [] : [{ provider: 'mock', id: 'A' }, { provider: 'mock', id: 'B' }]
      },
      scrapeByProvider: () => { calls.push('metadata'); return metadata.promise },
      findTrailer: () => { calls.push('trailer'); return trailer.promise },
    },
  })
  gated.query.value = 'QA-GATE'
  const gatedSearch = gated.search()
  await until(() => calls.includes('metadata'))
  assert.deepEqual(calls, ['candidates', 'metadata'], 'No downstream requests before metadata arrives')
  assert.equal(gated.magLoading.value, false)
  metadata.resolve({ number: 'QA-GATE', title: 'Metadata', summary: 'Summary' })
  await until(() => calls.includes('translate') && calls.includes('magnet') && calls.includes('trailer'))
  assert.equal(gated.magLoading.value, true, 'All three requests start while their responses remain pending')
  translation.reject(Error('translation unavailable'))
  magnets.resolve([{ source: 'fixture', count: 1, results: [] }])
  trailer.resolve({ ok: true, url: '/trailer' })
  await gatedSearch
  assert.equal(gated.stepTranslate.value, -1)
  assert.equal(gated.stepMagnet.value, 1)
  assert.equal(gated.stepTrailer.value, 1)
  assert.equal(gated.magLoading.value, false)

  calls.length = 0
  metadata = deferred()
  const failedCandidate = gated.pickCandidate(1)
  await until(() => calls.includes('metadata'))
  metadata.reject(Error('candidate metadata unavailable'))
  await failedCandidate
  assert.deepEqual(calls, ['metadata'], 'A failed candidate must not query downstream services')
  assert.equal(gateStore.movie, null)
  assert.equal(gateStore.grouped.length, 0)
  assert.equal(gateStore.trailerUrl, '')

  for (const mode of ['empty', 'error', 'failed-scrape', 'missing-scrape']) {
    calls.length = 0
    candidateMode = mode
    metadata = deferred()
    const failedSearch = gated.search()
    if (mode.endsWith('scrape')) {
      await until(() => calls.includes('metadata'))
      if (mode === 'failed-scrape') metadata.reject(Error('metadata unavailable'))
      else metadata.resolve(null)
    }
    await failedSearch
    assert.equal(calls.some(call => ['translate', 'magnet', 'trailer'].includes(call)), false, mode)
    assert.equal(gated.stepScrape.value, -1)
    assert.equal(gated.scraping.value, false)
    assert.equal(gated.magLoading.value, false)
  }

  calls.length = 0
  candidateMode = 'found'
  localMovie = { id: 1, number: 'QA-GATE', title: 'Stored', magnets: [{ source: 'library' }], hasTrailer: true }
  await gated.search()
  assert.deepEqual(calls, ['local-trailer'], 'Complete local metadata and extras must be reused')
  assert.equal(gated.stepScrape.value, 1)
  assert.equal(gated.stepTranslate.value, 1)
  assert.equal(gated.stepMagnet.value, 1)
  assert.equal(gated.stepTrailer.value, 1)

  calls.length = 0
  localMovie = null
  metadata = deferred()
  translation = deferred()
  magnets = deferred()
  trailer = deferred()
  const emptyTextSearch = gated.search()
  await until(() => calls.includes('metadata'))
  metadata.resolve({ number: 'QA-GATE', title: '', summary: '' })
  await until(() => calls.includes('magnet') && calls.includes('trailer'))
  assert.equal(calls.includes('translate'), false, 'Empty metadata text does not need a translation call')
  magnets.resolve([])
  trailer.resolve({ ok: false })
  await emptyTextSearch
  assert.equal(gated.stepTranslate.value, 1)

  metadata = deferred()
  translation = deferred()
  magnets = deferred()
  trailer = deferred()
  calls.length = 0
  const staleSearch = gated.search()
  await until(() => calls.includes('metadata'))
  metadata.resolve({ number: 'QA-GATE', title: 'Old candidate' })
  await until(() => calls.includes('translate') && calls.includes('magnet') && calls.includes('trailer'))
  metadata = deferred()
  calls.length = 0
  const missingCandidate = gated.pickCandidate(1)
  await until(() => calls.includes('metadata'))
  metadata.reject(Error('new candidate unavailable'))
  await missingCandidate
  assert.deepEqual(calls, ['metadata'])
  translation.resolve({ title: 'Old translation' })
  magnets.resolve([{ source: 'stale', count: 1, results: [] }])
  trailer.resolve({ ok: true, url: '/stale-trailer' })
  await staleSearch
  assert.equal(gateStore.movie, null)
  assert.equal(gateStore.grouped.length, 0, 'Late magnets must not populate a failed candidate')
  assert.equal(gateStore.trailerUrl, '', 'Late trailer must not populate a failed candidate')
  console.log('PASS: metadata gates downstream requests; all three start in parallel; empty/failed metadata stops queries; candidate failure clears results; local reuse and empty text skip translation')
}
main().catch(error => { console.error(error); process.exitCode = 1 })
