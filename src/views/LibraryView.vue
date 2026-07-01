<script setup lang="ts">
import { ref, watch, onMounted, computed } from 'vue'
import { useRoute } from 'vue-router'
import { movies as moviesApi, scan as scanApi } from '@/api/worker'
import type { Movie, ScanResult } from '@/types'
import { useLibraryStore } from '@/stores/libraries'
import { useFavoritesStore } from '@/stores/favorites'
import MovieCard from '@/components/MovieCard.vue'
import MovieDetailDrawer from '@/components/MovieDetailDrawer.vue'
import { t } from '@/utils/i18n'

const route = useRoute()
const libs = useLibraryStore()
const favs = useFavoritesStore()
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
const drawerId = ref<number | null>(null)
const drawerOpen = ref(false)
const scanResult = ref<ScanResult | null>(null)
const scanning = ref(false)
const ingestingIds = ref<Set<string>>(new Set())
const ingestStatus = ref<Record<string, string>>({})

const libId = () => Number(route.params.id)
const currentLib = () => libs.items.find((l: any) => l.id === libId())

async function load() {
  const id = libId()
  if (!Number.isFinite(id)) return   // route param not ready yet — avoid library/NaN
  loading.value = true
  try {
    movies.value = await moviesApi.byLibrary(id)
    await favs.load('movie')
  } finally {
    loading.value = false
  }
}

function openDetail(m: Movie) {
  drawerId.value = m.id ?? null
  drawerOpen.value = true
}

async function runScan() {
  scanning.value = true
  scanResult.value = null
  try {
    scanResult.value = await scanApi.run(libId())
    await load()
  } finally {
    scanning.value = false
  }
}

async function ingestNumber(number: string) {
  if (ingestingIds.value.has(number)) return
  ingestingIds.value.add(number)
  ingestStatus.value[number] = t('ingestingScrape')
  try {
    const src = scanResult.value?.files.find(f => f.number === number)?.filePath
    await moviesApi.ingestByNumber(libId(), number, src)
    ingestStatus.value[number] = '✅ ' + t('ingested')
    await load()
    await libs.load()
  } catch (e: any) {
    ingestStatus.value[number] = '❌ ' + e.message
  } finally {
    ingestingIds.value.delete(number)
  }
}

function isIngested(number: string) {
  return movies.value.some(m => m.number === number)
}

async function ingestAll() {
  const numbers = (scanResult.value?.files ?? [])
    .map((f) => f.number)
    .filter((n) => !ingestStatus.value[n]?.startsWith('✅') && !isIngested(n))
  for (const n of numbers) await ingestNumber(n)
}

onMounted(() => { favs.load('movie'); load() })
watch(() => route.params.id, () => {
  scanResult.value = null
  ingestStatus.value = {}
  ingestingIds.value.clear()
  load()
})
</script>

<template>
  <div class="p-8">
    <!-- Header -->
    <div class="flex items-center justify-between mb-6">
      <div class="flex items-center gap-3">
        <div class="w-10 h-10 rounded-lg flex items-center justify-center" style="background: var(--primary-soft); color: var(--primary);">
          <span class="i-carbon-folder text-xl" />
        </div>
        <div>
          <h1 class="text-xl font-bold leading-tight">{{ currentLib()?.name ?? t('libraries') }}</h1>
          <p class="text-xs text-muted">{{ movies.length }} {{ t('movies') }}</p>
        </div>
      </div>
      <button class="btn" :disabled="scanning" @click="runScan">
        <span class="i-carbon-search-locate" /> {{ scanning ? t('scanning') : t('scanDir') }}
      </button>
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
        <button v-if="scanResult.files.length" class="btn-primary !py-1.5 !px-3" :disabled="ingestingIds.size > 0" @click="ingestAll">
          <span class="i-carbon-add-all" /> {{ t('ingestAll') }}
        </button>
      </div>
      <div v-if="scanResult.files.length" class="max-h-72 overflow-y-auto">
        <div v-for="f in scanResult.files" :key="f.filePath" class="flex items-center gap-3 px-4 py-2 border-b border-border last:border-0 text-[13px]">
          <span class="i-carbon-document text-muted shrink-0" />
          <span class="font-mono text-primary shrink-0 w-24">{{ f.number }}</span>
          <span class="text-muted truncate flex-1 text-[11px]">{{ f.fileName }}</span>
          <span v-if="ingestStatus[f.number]" class="text-[11px] text-muted shrink-0">{{ ingestStatus[f.number] }}</span>
          <span v-else-if="isIngested(f.number)" class="text-[11px] text-status-green shrink-0">✅ {{ t('ingested') }}</span>
          <button v-if="!isIngested(f.number) || ingestStatus[f.number]?.startsWith('❌')" class="btn-ghost !text-primary !py-1 shrink-0" :disabled="ingestingIds.has(f.number)" @click="ingestNumber(f.number)">
            {{ ingestStatus[f.number]?.startsWith('✅') ? t('ingested') : t('ingest') }}
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
    <div v-else class="grid gap-4" style="grid-template-columns: repeat(auto-fill, minmax(176px, 1fr));">
      <MovieCard v-for="m in sortedMovies" :key="m.id" :movie="m" @click="openDetail(m)" />
    </div>

    <!-- Reusable detail drawer -->
    <MovieDetailDrawer v-model="drawerOpen" :movie-id="drawerId" @changed="load" />
  </div>
</template>
