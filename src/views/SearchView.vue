<script setup lang="ts">
import { ref, watch, onMounted } from 'vue'
import { metatube, magnet, movies as moviesApi, translate, MetatubeError } from '@/api/worker'
import type { Movie, MagnetSourceResult } from '@/types'
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
// ponytail: persisted in store so it survives tab switches
const trailerUrl = ref(store.trailerUrl)
const candidates = ref<{ provider: string; id: string; title?: string | null; coverUrl?: string | null; score?: number | null }[]>([])
const selectedCandidate = ref(0)

async function doScrape(q: string, provider: string, id: string): Promise<Movie | null> {
  try {
    const m = await metatube.scrapeByProvider(q, provider, id)
    store.movie = m
    return m
  } catch (e: any) {
    error.value = `刮削失败: ${e.message}`
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
    if (m) {
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

async function search() {
  const q = query.value.trim()
  if (!q) return
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

  // First, fetch all candidates so the user can pick if there are multiple.
  const scrapeP = metatube.candidates(q)
    .then(async (list) => {
      if (list.length === 0) {
        stepScrape.value = -1
        return
      }
      const m = list.length === 1
        ? await doScrape(q, list[0].provider, list[0].id)
        : (candidates.value = list, await doScrape(q, list[0].provider, list[0].id))
      stepScrape.value = 1
      if (m) {
        try {
          const r = await translate.run(m.title, m.summary)
          if (r.title) m.title = r.title
          if (r.summary) m.summary = r.summary
          stepTranslate.value = 1
        } catch {
          stepTranslate.value = -1
        }
      } else {
        stepTranslate.value = -1
      }
    })
    .catch((e) => {
      stepScrape.value = -1
      if (e instanceof MetatubeError && e.needsConfig) {
        needsConfig.value = true
        error.value = e.message
      } else {
        error.value = `刮削失败: ${e.message}`
      }
    })
  // Grouped magnet search (#10/#11).
  const magP = magnet.searchGrouped(q)
    .then((r) => { store.grouped = r; stepMagnet.value = 1 })
    .catch(() => { stepMagnet.value = -1 })

  // Trailer search (only once per search, not per candidate switch).
  const trailerP = metatube.findTrailer(q)
    .then((tr) => { trailerUrl.value = store.trailerUrl = tr.ok && tr.url ? tr.url : ''; stepTrailer.value = tr.ok && tr.url ? 1 : -1 })
    .catch(() => { trailerUrl.value = store.trailerUrl = ''; stepTrailer.value = -1 })

  await Promise.allSettled([scrapeP, magP, trailerP])
  scraping.value = false
  magLoading.value = false
  store.lastSearchAt = Date.now()
}

// #5: prompt when no library selected before ingesting.
async function ingest() {
  if (!store.movie) return
  if (ingestLibId.value == null) {
    ingestMsg.value = '⚠ 请先选择目标媒体库'
    return
  }
  ingesting.value = true
  ingestMsg.value = ''
  try {
    const res = await moviesApi.ingest(ingestLibId.value, store.movie)
    store.movie!.id = res.movieId
    store.movie!.libraryId = ingestLibId.value
    store.movie!.folderPath = res.folderPath
    ingestMsg.value = `已入库(影片 ID ${res.movieId})${res.folderPath ? ' — ' + res.folderPath : ''}`
    // Refresh library counts in the sidebar (整体 #3).
    await libs.load()
  } catch (e: any) {
    ingestMsg.value = `入库失败: ${e.message}`
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
            placeholder="例如:SSIS-001"
            @keyup.enter="search"
          />
        </div>
        <button class="btn-primary !px-6 !py-2.5 min-w-[110px] justify-center" :disabled="scraping" @click="search">
          <span class="i-carbon-search" /> {{ scraping ? t('searching') : t('searchBtn') }}
        </button>
      </div>

      <!-- Step progress -->
      <div v-if="scraping || stepScrape !== 0 || stepMagnet !== 0 || stepTranslate !== 0" class="flex items-center gap-4 mt-4">
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
        <span class="i-carbon-settings" /> 去配置
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
            class="chip !text-amber-400 !bg-amber-500/10 !border-amber-500/20"
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
            <span class="w-6 h-6 rounded-full bg-surface2 overflow-hidden inline-block align-middle">
              <img v-if="a.avatarUrl" :src="a.avatarUrl" class="w-full h-full object-cover" referrerpolicy="no-referrer" />
              <span v-else class="w-full h-full flex items-center justify-center text-muted text-[10px]"><span class="i-carbon-user" /></span>
            </span>
            <span class="text-[13px]">{{ a.name }}</span>
          </button>
        </div>

        <!-- 4. Other info -->
        <div class="flex flex-wrap gap-x-6 gap-y-1.5 text-[13px] text-text-soft">
          <span v-if="store.movie.maker" class="flex items-center gap-1.5"><span class="i-carbon-building text-muted" />{{ store.movie.maker }}</span>
          <span v-if="store.movie.releaseDate" class="flex items-center gap-1.5"><span class="i-carbon-calendar text-muted" />{{ store.movie.releaseDate.slice(0,10) }}</span>
          <span v-if="store.movie.runtimeMinutes" class="flex items-center gap-1.5"><span class="i-carbon-time text-muted" />{{ store.movie.runtimeMinutes }} 分钟</span>
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
          :class="ingestMsg.startsWith('⚠') || ingestMsg.startsWith('入库失败') ? 'text-red-400' : 'text-status-green'"
        >{{ ingestMsg }}</p>
        <p v-if="!libs.items.length" class="text-xs text-muted">先到「设置 → 媒体库」新建一个媒体库。</p>
      </div>
    </div>

    <!-- Magnet results (grouped, per-source tabs) -->
    <section>
      <div class="flex items-center gap-2 mb-4">
        <span class="i-carbon-magnet text-lg text-primary" />
        <h2 class="text-lg font-semibold">{{ t('magnetLinks') }}</h2>
      </div>
      <MagnetList :grouped="store.grouped" :loading="magLoading" />
    </section>
  </div>
</template>
