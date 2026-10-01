<script setup lang="ts">
import { ref, watch, onMounted, onUnmounted, computed } from 'vue'
import { useRoute } from 'vue-router'
import { movies as moviesApi, scan as scanApi } from '@/api/worker'
import type { Movie, ScanResult } from '@/types'
import { useLibraryStore } from '@/stores/libraries'
import { useFavoritesStore } from '@/stores/favorites'
import { useIngestStore } from '@/stores/ingest'
import MovieCard from '@/components/MovieCard.vue'
import MovieDetailDrawer from '@/components/MovieDetailDrawer.vue'
import { useMovieDetail } from '@/utils/movieDetail'
import { t } from '@/utils/i18n'
import { toast } from '@/utils/toast'
import { confirmDialog } from '@/utils/confirm'

const route = useRoute()
const libs = useLibraryStore()
const favs = useFavoritesStore()
const ingest = useIngestStore()
const movies = ref<Movie[]>([])
const searchQuery = ref('')
const sortBy = ref<'date' | 'name'>('date')

// Filter by search query + apply sort.
const sortedMovies = computed(() => {
  let list = movies.value
  if (searchQuery.value.trim()) {
    const q = searchQuery.value.trim().toLowerCase()
    list = list.filter(m =>
      (m.number ?? '').toLowerCase().includes(q) ||
      (m.title ?? '').toLowerCase().includes(q))
  }
  if (sortBy.value === 'name') {
    return [...list].sort((a, b) => (a.number ?? '').localeCompare(b.number ?? ''))
  }
  return list
})
const loading = ref(false)
const { drawerId, drawerOpen, openDetail } = useMovieDetail()

// Client-side pagination (API-Player-style bottom bar) — the view already
// loads the full list, paging just keeps huge grids from rendering at once.
const page = ref(1)
const PAGE_SIZE = 60
const pageCount = computed(() => Math.max(1, Math.ceil(sortedMovies.value.length / PAGE_SIZE)))
const pagedMovies = computed(() =>
  sortedMovies.value.slice((page.value - 1) * PAGE_SIZE, page.value * PAGE_SIZE))
// Sliding window of page numbers — up to 9, centered on the current page.
const pageList = computed(() => {
  const n = pageCount.value
  const span = 9
  let start = Math.max(1, page.value - Math.floor(span / 2))
  const end = Math.min(n, start + span - 1)
  start = Math.max(1, end - span + 1)
  return Array.from({ length: end - start + 1 }, (_, i) => start + i)
})
watch([searchQuery, sortBy, () => route.params.id], () => { page.value = 1 })
// Moving/deleting the last item on a page can reduce the page count.
watch(pageCount, (count) => { page.value = Math.min(page.value, count) })
// Right-click context menu for "move to library".
const ctxMenu = ref<{ x: number; y: number; movieId: number | null } | null>(null)

function onCardContextMenu(e: MouseEvent, m: Movie) {
  e.preventDefault()
  // Clamp so the menu never spills off-screen.
  const menuW = 180, menuH = 36 + libs.items.length * 30
  ctxMenu.value = {
    x: Math.min(e.clientX, window.innerWidth - menuW - 8),
    y: Math.min(e.clientY, window.innerHeight - menuH - 8),
    movieId: m.id ?? null,
  }
}
function onCtxMenuKeydown(e: KeyboardEvent) {
  if (e.key === 'Escape') ctxMenu.value = null
}
watch(ctxMenu, (v) => {
  window.removeEventListener('keydown', onCtxMenuKeydown)
  if (v) window.addEventListener('keydown', onCtxMenuKeydown)
})

async function moveToLibrary(targetId: number) {
  if (!ctxMenu.value?.movieId) return
  const targetLib = libs.items.find(l => l.id === targetId)
  if (!targetLib) return
  if (!await confirmDialog(`${t('moveConfirm')} ${targetLib.name}?`, t('moveTo'))) return
  try {
    const res = await moviesApi.move(ctxMenu.value.movieId, targetId)
    toast(res.detail, res.ok ? 'success' : 'error')
    ctxMenu.value = null
    await load()
    await libs.load()
  } catch (e: any) {
    toast(t('moveFail') + ': ' + e.message, 'error')
  }
}
onUnmounted(() => window.removeEventListener('keydown', onCtxMenuKeydown))
const scanResult = ref<ScanResult | null>(null)
const scanning = ref(false)

const libId = () => Number(route.params.id)
// The virtual "全部" library (route /library/all) shows every ingested movie.
const isAll = computed(() => route.params.id === 'all')
const currentLib = () => libs.items.find((l: any) => l.id === libId())

// Dashboard stats for the 全部 view — everything comes from the already-loaded
// movie list + library counts, no extra requests.
const LIB_PALETTE = ['#3b82f6', '#10b981', '#f59e0b', '#8b5cf6', '#ef4444', '#06b6d4', '#f97316', '#ec4899']
const allStats = computed(() => {
  const list = movies.value
  const libsTotal = libs.items.reduce((n: number, l: any) => n + (l.movieCount ?? 0), 0)
  return {
    total: list.length,
    playable: list.filter(m => m.hasVideo).length,
    trailers: list.filter(m => m.hasTrailer).length,
    hours: Math.round(list.reduce((n, m) => n + (m.runtimeMinutes ?? 0), 0) / 60 * 10) / 10,
    distribution: libs.items
      .filter((l: any) => (l.movieCount ?? 0) > 0)
      .map((l: any, i: number) => ({
        id: l.id,
        name: l.name,
        count: l.movieCount ?? 0,
        pct: libsTotal ? Math.max(((l.movieCount ?? 0) / libsTotal) * 100, 2) : 0,
        color: LIB_PALETTE[i % LIB_PALETTE.length],
      })),
  }
})

let loadSeq = 0
async function load() {
  const seq = ++loadSeq
  loading.value = true
  try {
    if (isAll.value) {
      const list = (await moviesApi.all()) ?? []
      if (seq !== loadSeq) return
      movies.value = list
    } else {
      const id = libId()
      if (!Number.isFinite(id)) return   // route param not ready yet — avoid library/NaN
      const list = (await moviesApi.byLibrary(id)) ?? []
      if (seq !== loadSeq) return
      movies.value = list
      await favs.load('movie')
    }
  } catch (e: any) {
    if (seq === loadSeq) toast(t('loadFailed') + ': ' + e.message, 'error')
  } finally {
    if (seq === loadSeq) loading.value = false
  }
}


async function runScan() {
  scanning.value = true
  scanResult.value = null
  try {
    scanResult.value = await scanApi.run(libId())
    await load()
  } catch (e: any) {
    toast(t('scanFailed') + ': ' + e.message, 'error')
  } finally {
    scanning.value = false
  }
}

// Per-file ingest now runs through the global ingest store (survives page
// switches). Each scan row renders its task status from the store.
function taskStatus(f: { number: string }) {
  return ingest.statusOf(libId(), f.number)
}

async function ingestNumber(f: { number: string; filePath?: string }) {
  ingest.enqueue(libId(), [{ number: f.number, filePath: f.filePath }])
}

function isIngested(number: string) {
  // Scanned file names keep their original case (akdl-294.mp4) while the DB
  // stores MetaTube's canonical number (AKDL-294) — compare case-insensitively.
  const n = number.toLowerCase()
  return movies.value.some(m => m.number?.toLowerCase() === n)
}

async function ingestAll() {
  if (!scanResult.value) return
  ingest.enqueue(
    libId(),
    scanResult.value.files.filter((f) => !isIngested(f.number)),
  )
}

// Reload the grid as tasks finish so freshly ingested movies appear (and
// multi-part rows flip to ✅).
watch(() => ingest.finished, () => { load() })

onMounted(() => { favs.load('movie'); load() })
watch(() => route.params.id, () => {
  scanResult.value = null
  load()
})
</script>

<template>
  <div class="p-8">
    <!-- Header — plain section heading; the 全部 view gets the hero in the
         layout instead, so only per-library pages render this header. -->
    <div v-if="!isAll" class="flex items-center justify-between mb-6">
      <div>
        <h1 class="text-xl font-bold leading-tight">{{ currentLib()?.name ?? t('libraries') }}</h1>
        <p class="text-xs text-muted mt-0.5">{{ movies.length }} {{ t('movies') }}</p>
      </div>
      <button v-if="!isAll" class="btn" :disabled="scanning" @click="runScan">
        <span class="i-carbon-search-locate" /> {{ scanning ? t('scanning') : t('scanDir') }}
      </button>
    </div>

    <!-- Dashboard (全部 view only) -->
    <template v-if="isAll && !loading">
      <div class="grid grid-cols-2 lg:grid-cols-4 gap-3 mb-3">
        <div class="card !rounded-lg p-4 flex items-center gap-3">
          <div class="w-10 h-10 rounded-lg flex items-center justify-center shrink-0" style="background: var(--primary-soft); color: var(--primary);">
            <span class="i-carbon-video text-xl" />
          </div>
          <div class="min-w-0">
            <div class="text-xl font-bold leading-tight">{{ allStats.total }}</div>
            <div class="text-[11px] text-muted truncate">{{ t('statTotal') }}</div>
          </div>
        </div>
        <div class="card !rounded-lg p-4 flex items-center gap-3">
          <div class="w-10 h-10 rounded-lg flex items-center justify-center shrink-0" style="background: rgba(16,185,129,0.12); color: var(--status-green);">
            <span class="i-carbon-play-filled-alt text-xl" />
          </div>
          <div class="min-w-0">
            <div class="text-xl font-bold leading-tight">{{ allStats.playable }}</div>
            <div class="text-[11px] text-muted truncate">{{ t('statPlayable') }}</div>
          </div>
        </div>
        <div class="card !rounded-lg p-4 flex items-center gap-3">
          <div class="w-10 h-10 rounded-lg flex items-center justify-center shrink-0" style="background: rgba(59,130,246,0.12); color: var(--primary);">
            <span class="i-carbon-closed-caption text-xl" />
          </div>
          <div class="min-w-0">
            <div class="text-xl font-bold leading-tight">{{ allStats.trailers }}</div>
            <div class="text-[11px] text-muted truncate">{{ t('statTrailers') }}</div>
          </div>
        </div>
        <div class="card !rounded-lg p-4 flex items-center gap-3">
          <div class="w-10 h-10 rounded-lg flex items-center justify-center shrink-0" style="background: rgba(245,158,11,0.12); color: #f59e0b;">
            <span class="i-carbon-time text-xl" />
          </div>
          <div class="min-w-0">
            <div class="text-xl font-bold leading-tight">{{ allStats.hours }}<span class="text-[12px] font-normal text-muted ml-1">{{ t('statHours') }}</span></div>
            <div class="text-[11px] text-muted truncate">{{ t('statDuration') }}</div>
          </div>
        </div>
      </div>

      <!-- 各库分布 -->
      <div v-if="allStats.distribution.length" class="card !rounded-lg p-4 mb-6">
        <div class="text-[12px] font-semibold text-text-soft mb-3">{{ t('statDistribution') }}</div>
        <div class="flex h-2.5 rounded-full overflow-hidden">
          <div
            v-for="seg in allStats.distribution"
            :key="seg.id"
            :style="{ width: seg.pct + '%', background: seg.color }"
            :title="`${seg.name} · ${seg.count}`"
          />
        </div>
        <div class="flex flex-wrap gap-x-4 gap-y-1 mt-3">
          <div v-for="seg in allStats.distribution" :key="'l' + seg.id" class="flex items-center gap-1.5 text-[11px] text-muted min-w-0">
            <span class="w-2 h-2 rounded-full shrink-0" :style="{ background: seg.color }" />
            <span class="truncate max-w-[120px]">{{ seg.name }}</span>
            <span class="text-text-soft shrink-0">{{ seg.count }}</span>
          </div>
        </div>
      </div>
    </template>

    <!-- Global ingest progress (lives in the store — survives page switches) -->
    <div v-if="ingest.running" class="card !rounded-md mb-5 px-4 py-3 flex items-center gap-3">
      <span class="i-carbon-in-progress animate-spin text-primary shrink-0" />
      <span class="text-[13px] shrink-0">{{ t('ingesting') }} {{ ingest.finished }}/{{ ingest.total }}</span>
      <div class="flex-1 h-1.5 rounded-full bg-surface2 overflow-hidden">
        <div class="h-full bg-primary transition-all duration-300" :style="{ width: (ingest.total ? Math.round(ingest.finished / ingest.total * 100) : 0) + '%' }" />
      </div>
    </div>

    <!-- Search + Sort toolbar -->
    <div class="flex items-center gap-2 mb-5">
      <div class="relative flex-1 max-w-xs">
        <span class="i-carbon-search absolute left-3 top-1/2 -translate-y-1/2 text-muted text-sm" />
        <input v-model="searchQuery" class="input !pl-9" :placeholder="t('searchPlaceholder')" />
      </div>
      <select v-model="sortBy" class="input !w-auto">
        <option value="date">{{ t('sortByDate') }}</option>
        <option value="name">{{ t('sortByName') }}</option>
      </select>
    </div>

    <!-- Scan result -->
    <div v-if="scanResult" class="card !rounded-md mb-5 overflow-hidden">
      <div class="flex items-center justify-between px-4 py-3 border-b border-border">
        <div class="flex items-center gap-4 text-[13px] text-text-soft">
          <span><span class="text-muted">{{ t('availDirs') }}</span> {{ scanResult.availableDirs }}</span>
          <span><span class="text-muted">{{ t('skipped') }}</span> {{ scanResult.skippedDirs }}</span>
          <span><span class="text-muted">{{ t('scannedFiles') }}</span> {{ scanResult.files.length }}</span>
        </div>
        <button v-if="scanResult.files.length" class="btn-primary !py-1.5 !px-3" @click="ingestAll">
          <span class="i-carbon-document-add" /> {{ t('ingestAll') }}
        </button>
      </div>
      <div v-if="scanResult.files.length" class="max-h-72 overflow-y-auto">
        <div v-for="f in scanResult.files" :key="f.filePath" class="flex items-center gap-3 px-4 py-2 border-b border-border last:border-0 text-[13px]">
          <span class="i-carbon-document text-muted shrink-0" />
          <span class="font-mono text-primary shrink-0 w-24">{{ f.number }}</span>
          <span class="text-muted truncate flex-1 text-[11px]">{{ f.fileName }}</span>
          <span v-if="taskStatus(f)?.status === 'running'" class="text-[11px] text-primary shrink-0">{{ t('ingesting') }}</span>
          <span v-else-if="taskStatus(f)?.status === 'pending'" class="text-[11px] text-muted shrink-0">{{ t('queued') }}</span>
          <span v-else-if="taskStatus(f)?.status === 'failed'" class="text-[11px] text-red-400 shrink-0 truncate max-w-[200px]" :title="taskStatus(f)?.message">❌ {{ taskStatus(f)?.message }}</span>
          <span v-else-if="taskStatus(f)?.status === 'done' || isIngested(f.number)" class="text-[11px] text-status-green shrink-0">✅ {{ t('ingested') }}</span>
          <button
            v-if="(!isIngested(f.number) && taskStatus(f)?.status !== 'done') || taskStatus(f)?.status === 'failed'"
            class="btn-ghost !text-primary !py-1 shrink-0"
            :disabled="taskStatus(f)?.status === 'running' || taskStatus(f)?.status === 'pending'"
            @click="ingestNumber(f)"
          >
            {{ taskStatus(f)?.status === 'failed' ? t('retry') : t('ingest') }}
          </button>
        </div>
      </div>
      <div v-for="(log, i) in scanResult.logs" :key="'log'+i" class="text-[11px] text-muted px-4 py-1.5 font-mono">{{ log }}</div>
    </div>

    <!-- Grid -->
    <div v-if="loading" class="text-muted text-sm py-12 text-center">{{ t('loading') }}</div>
    <div v-else-if="!movies.length" class="card !rounded-lg p-12 text-center">
      <span class="i-carbon-video block text-4xl mb-3 text-muted opacity-50" />
      <p class="text-muted text-sm mb-4">{{ t('noMovies') }}</p>
      <p class="text-xs text-muted">{{ t('noMoviesHint') }}</p>
    </div>
    <div v-else class="grid gap-4" style="grid-template-columns: repeat(auto-fill, minmax(230px, 1fr));">
      <MovieCard v-for="m in pagedMovies" :key="m.id" :movie="m" :library-name="isAll ? libs.items.find(l => l.id === m.libraryId)?.name : undefined" @click="openDetail(m)" @contextmenu="(e: MouseEvent) => onCardContextMenu(e, m)" />
    </div>

    <!-- Bottom pagination bar (sticky, API-Player style: blue current page) -->
    <div
      v-if="!loading && movies.length"
      class="sticky bottom-0 -mx-8 mt-8 px-8 py-2.5 flex items-center gap-1 flex-wrap"
      style="background: color-mix(in srgb, var(--bg) 86%, transparent); backdrop-filter: blur(10px); border-top: 1px solid var(--border);"
    >
      <button
        class="w-7 h-7 rounded-md flex items-center justify-center text-text-soft hover:bg-surface2 hover:text-text transition-colors disabled:opacity-30 disabled:pointer-events-none"
        :disabled="page <= 1"
        :title="t('prevPage')" :aria-label="t('prevPage')"
        @click="page--"
      >
        <span class="i-carbon-chevron-left text-[13px]" />
      </button>
      <span v-if="pageList[0] > 1" class="w-7 h-7 flex items-end justify-center pb-1.5 text-[12px] text-muted">…</span>
      <button
        v-for="p in pageList"
        :key="p"
        class="w-7 h-7 rounded-md flex items-center justify-center text-[12px] font-medium transition-colors"
        :class="p === page ? 'bg-primary text-white' : 'text-text-soft hover:bg-surface2 hover:text-text'"
        @click="page = p"
      >{{ p }}</button>
      <span v-if="pageList[pageList.length - 1] < pageCount" class="w-7 h-7 flex items-end justify-center pb-1.5 text-[12px] text-muted">…</span>
      <button
        class="w-7 h-7 rounded-md flex items-center justify-center text-text-soft hover:bg-surface2 hover:text-text transition-colors disabled:opacity-30 disabled:pointer-events-none"
        :disabled="page >= pageCount"
        :title="t('nextPage')" :aria-label="t('nextPage')"
        @click="page++"
      >
        <span class="i-carbon-chevron-right text-[13px]" />
      </button>
      <span class="text-[12px] text-muted ml-2">{{ t('totalItems', { n: sortedMovies.length }) }}</span>
    </div>

    <!-- Reusable detail drawer -->
    <MovieDetailDrawer v-model="drawerOpen" :movie-id="drawerId" @changed="load" />

    <!-- Right-click context menu: move to library -->
    <Teleport to="body">
      <div v-if="ctxMenu" class="fixed inset-0 z-[90]" @click="ctxMenu = null" @contextmenu.prevent="ctxMenu = null">
        <div class="absolute card !rounded-md shadow-lg py-1 min-w-[160px]" :style="{ left: ctxMenu.x + 'px', top: ctxMenu.y + 'px' }">
          <div class="px-3 py-1.5 text-[11px] text-muted">{{ t('moveTo') }}</div>
          <button
            v-for="lib in libs.items.filter(l => l.id !== libId())"
            :key="lib.id"
            class="w-full text-left px-3 py-1.5 text-[13px] hover:bg-surface2 transition-colors"
            @click.stop="moveToLibrary(lib.id)"
          >
            <span class="i-carbon-folder mr-1.5" />{{ lib.name }}
          </button>
        </div>
      </div>
    </Teleport>
  </div>
</template>
