<script setup lang="ts">
import { ref, watch, onMounted } from 'vue'
import { metatube, magnet, movies as moviesApi, translate, actors as actorsApi, absoluteAvatar, MetatubeError } from '@/api/worker'
import type { Movie, MagnetSourceResult, MagnetResult } from '@/types'
import MagnetList from '@/components/MagnetList.vue'
import { t } from '@/utils/i18n'
import { useLibraryStore } from '@/stores/libraries'
import { useSearchStore } from '@/stores/search'
import { useRouter } from 'vue-router'

const router = useRouter()
const libs = useLibraryStore()
const store = useSearchStore()

// Local input is bound to the store so it persists across navigation.
const query = ref(store.query)
const scraping = ref(false)
const magLoading = ref(false)
const error = ref('')
const needsConfig = ref(false)
// Step progress: 0=pending, 1=done, -1=failed
const stepScrape = ref(0)
const stepMagnet = ref(0)
const stepTrailer = ref(0)
const stepTranslate = ref(0)
const ingestLibId = ref<number | null>(null)
const ingesting = ref(false)
const ingestMsg = ref('')
const ingestOk = ref(true)
// ponytail: persisted in store so it survives tab switches
const trailerUrl = ref(store.trailerUrl)
const candidates = ref<{ provider: string; id: string; title?: string | null; coverUrl?: string | null; score?: number | null }[]>([])
const selectedCandidate = ref(0)

async function doScrape(q: string, provider: string, id: string, seq?: number): Promise<Movie | null> {
  try {
    const m = await metatube.scrapeByProvider(q, provider, id)
    // A stale search's result must not clobber the store (see searchSeq).
    if (seq != null && seq !== searchSeq) return null
    store.movie = m
    // Scraped cast has no avatars of its own — resolve them by name against
    // the local actor cache / MetaTube so they show up before ingest.
    if (m.actors?.length) {
      const names = [...new Set(m.actors.map(a => a.name).filter((n): n is string => !!n))]
      if (names.length) {
        try {
          const map = await actorsApi.resolveAvatars(names)
          for (const a of m.actors) {
            const local = a.name ? map[a.name] : null
            if (local) a.avatarUrl = (await absoluteAvatar(local)) ?? null
          }
        } catch { /* avatar resolution optional */ }
      }
    }
    return m
  } catch (e: any) {
    error.value = `${t('scrapeFailed')}: ${e.message}`
    return null
  }
}

async function pickCandidate(i: number) {
  selectedCandidate.value = i
  const c = candidates.value[i]
  if (!c) return
  scraping.value = true
  store.movie = null
  try {
    const m = await doScrape(query.value.trim(), c.provider, c.id)
    if (m && (m.title?.trim() || m.summary?.trim())) {
      try {
        const r = await translate.run(m.title, m.summary)
        if (r.title) m.title = r.title
        if (r.summary) m.summary = r.summary
      } catch { /* LLM not configured */ }
    }
  } finally {
    scraping.value = false
  }
}

// Default target library = the first one (#9).
watch(() => libs.items, (items) => {
  if (ingestLibId.value == null && items.length) ingestLibId.value = items[0].id
}, { immediate: true })

// Restore previous results on mount (#1).
onMounted(() => {
  if (store.movie || store.grouped.length) query.value = store.query
})

// Monotonic sequence so a slow earlier search can't overwrite a newer one's
// results (fast repeat searches used to race).
let searchSeq = 0

// Group stored magnets by their source so they render in the same per-source
// tabs a fresh network search produces.
function groupBySource(magnets: MagnetResult[]): MagnetSourceResult[] {
  const bySrc = new Map<string, MagnetResult[]>()
  for (const m of magnets) {
    const src = m.source || 'library'
    if (!bySrc.has(src)) bySrc.set(src, [])
    bySrc.get(src)!.push(m)
  }
  return [...bySrc.entries()].map(([source, results]) => ({ source, count: results.length, results }))
}

async function search() {
  const q = query.value.trim()
  if (!q) return
  const seq = ++searchSeq
  error.value = ''
  needsConfig.value = false
  store.query = q
  store.movie = null
  store.grouped = []
  store.trailerUrl = ''
  ingestMsg.value = ''
  trailerUrl.value = ''
  candidates.value = []
  stepScrape.value = 0
  stepMagnet.value = 0
  stepTrailer.value = 0
  stepTranslate.value = 0
  scraping.value = true
  magLoading.value = true

  // Local-first: check the library before any network call. An ingested 番号
  // serves its stored record (real id → "已入库" badge), stored magnets and
  // local trailer file; only pieces missing locally fall back to the network.
  let stored: Movie | null = null
  try { stored = await moviesApi.byNumber(q) } catch { /* not ingested → scrape */ }
  if (seq !== searchSeq) return

  // First, fetch all candidates so the user can pick if there are multiple.
  const scrapeP = (async () => {
    if (stored) {
      store.movie = stored
      stepScrape.value = 1
      stepTranslate.value = 1 // 库内数据已是入库时处理过的内容
      return
    }
    await metatube.candidates(q)
      .then(async (list) => {
        if (seq !== searchSeq) return
        if (list.length === 0) {
          stepScrape.value = -1
          return
        }
        const m = list.length === 1
          ? await doScrape(q, list[0].provider, list[0].id, seq)
          : (candidates.value = list, await doScrape(q, list[0].provider, list[0].id, seq))
        if (seq !== searchSeq) return
        stepScrape.value = 1
        if (m) {
          if (m.title?.trim() || m.summary?.trim()) {
            try {
              const r = await translate.run(m.title, m.summary)
              if (seq !== searchSeq) return
              if (r.title) m.title = r.title
              if (r.summary) m.summary = r.summary
              stepTranslate.value = 1
            } catch {
              stepTranslate.value = -1
            }
          } else {
            // 元数据为空 — 没有可翻译的内容，跳过翻译。
            stepTranslate.value = 1
          }
        } else {
          stepTranslate.value = -1
        }
      })
      .catch((e) => {
        if (seq !== searchSeq) return
        stepScrape.value = -1
        if (e instanceof MetatubeError && e.needsConfig) {
          needsConfig.value = true
          error.value = e.message
        } else {
          error.value = `${t('scrapeFailed')}: ${e.message}`
        }
      })
  })()
  // Grouped magnet search (#10/#11) — stored magnets serve locally, grouped
  // per source; network search only when the library has none.
  const localMagnets = stored?.magnets ?? []
  const magP = localMagnets.length
    ? Promise.resolve().then(() => {
        if (seq !== searchSeq) return
        store.grouped = groupBySource(localMagnets)
        stepMagnet.value = 1
      })
    : magnet.searchGrouped(q)
      .then((r) => { if (seq === searchSeq) { store.grouped = r; stepMagnet.value = 1 } })
      .catch(() => { if (seq === searchSeq) stepMagnet.value = -1 })

  // Trailer — the local {番号}-trailer.mp4 streams straight from the worker;
  // DMM probe/download only when no local file exists.
  const trailerP = stored?.hasTrailer && stored.id
    ? moviesApi.trailerUrl(stored.id).then((u) => {
        if (seq !== searchSeq) return
        trailerUrl.value = store.trailerUrl = u
        stepTrailer.value = 1
      })
    : metatube.findTrailer(q)
      .then((tr) => {
        if (seq !== searchSeq) return
        trailerUrl.value = store.trailerUrl = tr.ok && tr.url ? tr.url : ''
        stepTrailer.value = tr.ok && tr.url ? 1 : -1
      })
      .catch(() => { if (seq === searchSeq) { trailerUrl.value = store.trailerUrl = ''; stepTrailer.value = -1 } })

  await Promise.allSettled([scrapeP, magP, trailerP])
  if (seq !== searchSeq) return
  scraping.value = false
  magLoading.value = false
  store.lastSearchAt = Date.now()
}

// #5: prompt when no library selected before ingesting.
async function ingest() {
  if (!store.movie) return
  if (ingestLibId.value == null) {
    ingestMsg.value = t('selectLibWarn')
    ingestOk.value = false
    return
  }
  ingesting.value = true
  ingestMsg.value = ''
  ingestOk.value = true
  try {
    // Pass the magnet results already shown in the UI — without them the
    // backend would re-run the full multi-source magnet search (tens of
    // seconds) even though the data is right here.
    const known = store.grouped.flatMap((g) => g.results as MagnetResult[])
    const res = await moviesApi.ingest(ingestLibId.value, store.movie, known.length ? known : undefined)
    store.movie!.id = res.movieId
    store.movie!.libraryId = ingestLibId.value
    store.movie!.folderPath = res.folderPath
    // Refresh from the stored record: actor avatars flip to local endpoints
    // (downloaded at ingest) and the trailer preview moves off the temp URL
    // (ingest relocates the temp file into the movie folder).
    try {
      const m = await moviesApi.get(res.movieId)
      store.movie = m
      if (m.hasTrailer && m.id) {
        trailerUrl.value = store.trailerUrl = await moviesApi.trailerUrl(m.id)
        stepTrailer.value = 1
      }
    } catch { /* keep scraped data */ }
    ingestMsg.value = t('ingestedId', { id: res.movieId }) + (res.folderPath ? ' — ' + res.folderPath : '')
    // Refresh library counts in the sidebar (整体 #3).
    await libs.load()
  } catch (e: any) {
    ingestMsg.value = `${t('ingestFailed')}: ${e.message}`
    ingestOk.value = false
  } finally {
    ingesting.value = false
  }
}
</script>

<template>
  <div class="p-8 max-w-6xl mx-auto">
    <!-- Hero search -->
    <div class="mb-8">
      <h1 class="text-3xl font-bold tracking-tight mb-1">{{ t('searchTitle') }}</h1>
      <p class="text-muted text-sm mb-5">{{ t('searchSubtitle') }}</p>
      <div class="flex gap-2">
        <div class="relative flex-1">
          <span class="i-carbon-search absolute left-3.5 top-1/2 -translate-y-1/2 text-muted text-sm" />
          <input
            v-model="query"
            class="input !pl-10 !py-2.5 text-sm"
            :placeholder="t('searchPlaceholder')"
            @keyup.enter="search"
          />
        </div>
        <button class="btn-primary !px-6 !py-2.5 min-w-[110px] justify-center" :disabled="scraping" @click="search">
          <span class="i-carbon-search" /> {{ scraping ? t('searching') : t('searchBtn') }}
        </button>
      </div>

      <!-- Step progress -->
      <div v-if="scraping || stepScrape !== 0 || stepMagnet !== 0 || stepTrailer !== 0 || stepTranslate !== 0" class="flex items-center gap-4 mt-4">
        <div v-for="s in [
          { label: t('stepScrape'), state: stepScrape },
          { label: t('stepTranslate'), state: stepTranslate },
          { label: t('stepTrailer'), state: stepTrailer },
          { label: t('stepMagnet'), state: stepMagnet },
        ]" :key="s.label" class="flex items-center gap-1.5 text-[12px]">
          <span
            class="w-4 h-4 rounded-full flex items-center justify-center text-[10px] shrink-0 transition-colors"
            :class="s.state === 1 ? 'bg-green-500 text-white' : s.state === -1 ? 'bg-red-500/30 text-red-400' : 'bg-surface3 text-muted animate-pulse'"
          >
            <span v-if="s.state === 1" class="i-carbon-checkmark" />
            <span v-else-if="s.state === -1" class="i-carbon-close" />
          </span>
          <span :class="s.state === 1 ? 'text-status-green' : s.state === -1 ? 'text-red-400' : 'text-muted'">{{ s.label }}</span>
        </div>
      </div>
    </div>
    <div
      v-if="error"
      class="text-sm mb-5 p-3.5 rounded-md flex items-center justify-between gap-3"
      :class="needsConfig
        ? 'bg-amber-500/10 border border-amber-500/30 text-amber-400'
        : 'bg-red-500/10 border border-red-500/20 text-red-400'"
    >
      <span class="flex items-center gap-2">
        <span :class="needsConfig ? 'i-carbon-warning-alt' : 'i-carbon-warning'" />
        {{ error }}
      </span>
      <button v-if="needsConfig" class="btn-primary !py-1.5 !px-3 shrink-0" @click="router.push('/settings')">
        <span class="i-carbon-settings" /> {{ t('goConfig') }}
      </button>
    </div>

    <!-- Candidate picker (when multiple results found) -->
    <div v-if="candidates.length > 1" class="mb-4">
      <div class="text-[13px] text-muted mb-2">{{ t('multipleResults') }} ({{ candidates.length }})</div>
      <div class="flex gap-2 overflow-x-auto pb-2">
        <button
          v-for="(c, i) in candidates"
          :key="c.provider + c.id"
          class="card !rounded-md p-2 w-36 shrink-0 text-left transition-all"
          :class="selectedCandidate === i ? '!border-primary ring-1 ring-primary' : ''"
          @click="pickCandidate(i)"
        >
          <div class="aspect-[2/3] bg-surface2 rounded overflow-hidden mb-1.5">
            <img v-if="c.coverUrl" :src="c.coverUrl" class="w-full h-full object-cover" referrerpolicy="no-referrer" loading="lazy" />
            <div v-else class="w-full h-full flex items-center justify-center text-muted"><span class="i-carbon-image text-2xl" /></div>
          </div>
          <div class="text-[10px] text-muted truncate">[{{ c.provider }}]</div>
          <div class="text-[11px] truncate leading-tight">{{ c.title || c.id }}</div>
          <div v-if="c.score" class="text-[10px] text-amber-400">★ {{ c.score }}</div>
        </button>
      </div>
    </div>

    <!-- Scraped result -->
    <div v-if="store.movie" class="mb-10">
      <!-- Trailer at top (with horizontal poster as cover) -->
      <div class="mb-4">
        <video
          v-if="trailerUrl"
          :src="trailerUrl"
          :poster="(store.movie.thumbUrl || store.movie.coverUrl || '') as string"
          controls
          class="w-full max-h-[420px] rounded-lg border border-border bg-black"
        />
        <!-- Fallback: show the horizontal poster when no trailer -->
        <img
          v-else-if="store.movie.thumbUrl || store.movie.coverUrl"
          :src="(store.movie.thumbUrl ?? store.movie.coverUrl ?? '') as string"
          class="w-full max-h-[420px] object-cover rounded-lg border border-border"
          referrerpolicy="no-referrer"
        />
      </div>

      <!-- Metadata -->
      <div class="flex flex-col gap-3">
        <!-- 1. 番号 + 评分 + 已入库标签 -->
        <div class="flex items-center gap-2 flex-wrap">
          <span class="text-primary font-bold text-lg">{{ store.movie.number }}</span>
          <span
            v-if="store.movie.score"
            class="text-[12px] font-semibold px-2 py-0.5 rounded-md leading-none"
            style="background: var(--accent); color: var(--on-accent);"
          >★ {{ store.movie.score }}</span>
          <span
            v-if="store.movie.id"
            class="chip !text-status-green !bg-green-500/10 !border-green-500/20"
          >
            <span class="i-carbon-checkmark-filled" /> {{ t('ingested') }}
          </span>
        </div>

        <!-- 2. 标题 -->
        <h2 class="text-xl font-semibold leading-snug">{{ store.movie.title }}</h2>

        <!-- 3. 演员 -->
        <div v-if="store.movie.actors?.length">
          <span class="text-xs text-muted mr-2 align-middle">{{ t('actorsLabel') }}</span>
          <button
            v-for="a in store.movie.actors"
            :key="a.id ?? a.name"
            class="inline-flex items-center gap-1.5 mr-2 align-middle hover:text-primary transition-colors"
            @click="a.id && router.push(`/actors/${a.id}`)"
          >
            <span class="relative w-6 h-6 rounded-full bg-surface2 overflow-hidden inline-flex items-center justify-center text-muted shrink-0">
              <span class="i-carbon-user text-[11px]" />
              <img
                v-if="a.avatarUrl"
                :src="a.avatarUrl"
                class="absolute inset-0 w-full h-full object-cover"
                referrerpolicy="no-referrer"
                @error="($event.target as HTMLImageElement).style.display = 'none'"
              />
            </span>
            <span class="text-[13px]">{{ a.name }}</span>
          </button>
        </div>

        <!-- 4. Other info -->
        <div class="flex flex-wrap gap-x-6 gap-y-1.5 text-[13px] text-text-soft">
          <span v-if="store.movie.maker" class="flex items-center gap-1.5"><span class="i-carbon-building text-muted" />{{ store.movie.maker }}</span>
          <span v-if="store.movie.releaseDate" class="flex items-center gap-1.5"><span class="i-carbon-calendar text-muted" />{{ store.movie.releaseDate.slice(0,10) }}</span>
          <span v-if="store.movie.runtimeMinutes" class="flex items-center gap-1.5"><span class="i-carbon-time text-muted" />{{ store.movie.runtimeMinutes }} {{ t('minutes') }}</span>
        </div>

        <!-- Tags -->
        <div v-if="store.movie.tags?.length" class="flex flex-wrap gap-1.5">
          <span
            v-for="t in store.movie.tags"
            :key="t.id ?? t.name"
            class="chip"
            @click="t.id && router.push(`/tags/${t.id}`)"
          >{{ t.name }}</span>
        </div>

        <!-- 5. Summary -->
        <p v-if="store.movie.summary" class="text-[13px] text-text-soft leading-relaxed line-clamp-4">{{ store.movie.summary }}</p>

        <!-- Ingest -->
        <div class="flex items-center gap-2 mt-2 pt-4 border-t border-border">
          <select v-model.number="ingestLibId" class="input !w-auto">
            <option :value="null" disabled>{{ t('selectLib') }}</option>
            <option v-for="lib in libs.items" :key="lib.id" :value="lib.id">{{ lib.name }}</option>
          </select>
          <button class="btn-primary" :disabled="ingesting" @click="ingest">
            <span class="i-carbon-add" /> {{ ingesting ? t('ingesting') : t('ingest') }}
          </button>
        </div>
        <p
          v-if="ingestMsg"
          class="text-[13px]"
          :class="ingestOk ? 'text-status-green' : 'text-red-400'"
        >{{ ingestMsg }}</p>
        <p v-if="!libs.items.length" class="text-xs text-muted">{{ t('notIngestedHint') }}</p>
      </div>
    </div>

    <!-- Magnet results (grouped, per-source tabs) -->
    <section>
      <div class="flex items-center gap-2 mb-4">
        <span class="i-carbon-link text-lg text-primary" />
        <h2 class="text-lg font-semibold">{{ t('magnetLinks') }}</h2>
      </div>
      <MagnetList :grouped="store.grouped" :loading="magLoading" />
    </section>
  </div>
</template>
