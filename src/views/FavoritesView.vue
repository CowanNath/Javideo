<script setup lang="ts">
import { ref, computed } from 'vue'
import { favorites } from '@/api/worker'
import type { FavoriteTarget, Favorite } from '@/types'
import { useRouter } from 'vue-router'
import { confirmDialog } from '@/utils/confirm'
import { t as ti } from '@/utils/i18n'
import MovieDetailDrawer from '@/components/MovieDetailDrawer.vue'

const router = useRouter()
const tab = ref<FavoriteTarget>('movie')
const items = ref<Favorite[]>([])
const loading = ref(false)
const selected = ref<Set<number>>(new Set())
const search = ref('')
const sort = ref<'recent' | 'id'>('recent')
// Movie detail drawer (for the movie tab — open in place, not navigate).
const drawerId = ref<number | null>(null)
const drawerOpen = ref(false)

async function load() {
  loading.value = true
  selected.value = new Set()
  try {
    items.value = await favorites.list(tab.value)
  } finally {
    loading.value = false
  }
}

const filtered = computed(() => {
  let list = items.value
  const q = search.value.trim().toLowerCase()
  if (q) {
    list = list.filter((it) =>
      String(it.targetId).includes(q) || (it.name?.toLowerCase().includes(q)))
  }
  const arr = [...list]
  arr.sort((a, b) => sort.value === 'recent' ? b.id - a.id : a.targetId - b.targetId)
  return arr
})

const allSelected = computed(
  () => filtered.value.length > 0 && filtered.value.every((it) => selected.value.has(it.id)),
)

function toggle(id: number) {
  const next = new Set(selected.value)
  next.has(id) ? next.delete(id) : next.add(id)
  selected.value = next
}
function toggleAll() {
  selected.value = allSelected.value ? new Set() : new Set(filtered.value.map((it) => it.id))
}
async function remove(item: Favorite) {
  if (!await confirmDialog(`${ti('unfavConfirm')}「${item.name ?? item.targetId}」?`, ti('unfavItem'))) return
  await favorites.remove(tab.value, item.targetId)
  await load()
}
async function batchRemove() {
  if (!selected.value.size) return
  const ids = items.value
    .filter((it) => selected.value.has(it.id))
    .map((it) => it.targetId)
    .filter((id): id is number => typeof id === 'number' && id > 0)
  if (!ids.length) return
  if (!await confirmDialog(`${ti('unfavConfirm')} ${ids.length} 项?`, ti('batchUnfav'))) return
  await favorites.batchRemove(tab.value, ids)
  await load()
}
function switchTab(t: FavoriteTarget) {
  tab.value = t
  search.value = ''
  load()
}
function go(it: Favorite) {
  if (tab.value === 'movie') {
    // Movies open the detail drawer in place (their id is a movie id, not a
    // library id — navigating to /library/:id was wrong).
    drawerId.value = it.targetId
    drawerOpen.value = true
  } else {
    router.push(`/${tab.value === 'tag' ? 'tags' : 'actors'}/${it.targetId}`)
  }
}
// Display name + icon per type.
function display(it: Favorite): string {
  return it.name?.trim() || `#${it.targetId}`
}
const tabName = (t: FavoriteTarget) => t === 'movie' ? ti('movie') : t === 'tag' ? ti('tags') : ti('actors')
const tabIcon = (t: FavoriteTarget) => t === 'movie' ? 'i-carbon-video' : t === 'tag' ? 'i-carbon-tag' : 'i-carbon-user-multiple'
load()
</script>

<template>
  <div class="p-8 max-w-5xl mx-auto">
    <div class="mb-6">
      <h1 class="text-2xl font-bold tracking-tight mb-1">{{ ti('favorites') }}</h1>
      <p class="text-muted text-sm">{{ ti('favoritesSubtitle') }}</p>
    </div>

    <!-- Tabs -->
    <div class="flex gap-1 mb-5 p-1 rounded-lg bg-surface2 border border-border w-fit">
      <button
        v-for="t in (['movie','tag','actor'] as FavoriteTarget[])"
        :key="t"
        class="px-3.5 py-1.5 rounded-md text-[13px] font-medium flex items-center gap-1.5 transition-all duration-150"
        :class="tab === t ? 'bg-surface text-primary shadow-sm' : 'text-muted hover:text-text'"
        @click="switchTab(t)"
      >
        <span :class="tabIcon(t)" /> {{ tabName(t) }}
      </button>
    </div>

    <!-- Toolbar -->
    <div v-if="items.length" class="flex items-center gap-3 mb-5 flex-wrap">
      <div class="relative flex-1 min-w-[200px] max-w-xs">
        <span class="i-carbon-search absolute left-3 top-1/2 -translate-y-1/2 text-muted text-sm" />
        <input v-model="search" class="input !pl-9" :placeholder="ti('searchNameOrId')" />
      </div>
      <select v-model="sort" class="input !w-auto">
        <option value="recent">{{ ti('recent') }}</option>
        <option value="id">{{ ti('byId') }}</option>
      </select>
      <label class="flex items-center gap-2 text-[13px] text-text-soft cursor-pointer">
        <input type="checkbox" :checked="allSelected" @change="toggleAll" class="accent-[var(--primary)]" />
        {{ ti('selectAll') }}
      </label>
      <button class="btn !text-red-400 hover:!bg-red-500/10" :disabled="!selected.size" @click="batchRemove">
        <span class="i-carbon-trash-can" /> {{ ti('batchUnfav') }}({{ selected.size }})
      </button>
    </div>

    <div v-if="loading" class="text-muted text-sm py-12 text-center">{{ ti('loading') }}</div>
    <div v-else-if="!items.length" class="card !rounded-lg p-12 text-center text-muted text-sm">
      <span class="i-carbon-favorite block text-4xl mb-3 opacity-50" />
      {{ ti('noFavs') }}{{ tabName(tab) }}
    </div>
    <div v-else-if="!filtered.length" class="text-muted text-sm py-8 text-center">未匹配到结果</div>

    <!-- List -->
    <div v-else class="flex flex-col gap-1.5">
      <div
        v-for="it in filtered"
        :key="it.id"
        class="card !rounded-md p-3 flex items-center gap-3"
        :class="selected.has(it.id) && '!border-primary'"
      >
        <input type="checkbox" :checked="selected.has(it.id)" class="accent-[var(--primary)]" @change="toggle(it.id)" />
        <!-- thumbnail / icon -->
        <div
          class="w-9 h-9 rounded-md flex items-center justify-center shrink-0 overflow-hidden"
          style="background: var(--primary-soft); color: var(--primary);"
          @click="go(it)"
        >
          <img v-if="it.cover" :src="it.cover" class="w-full h-full object-cover" referrerpolicy="no-referrer" />
          <span v-else :class="tabIcon(tab)" />
        </div>
        <div class="flex-1 min-w-0 cursor-pointer" @click="go(it)">
          <div class="text-[13px] font-medium truncate">{{ display(it) }}</div>
          <div v-if="it.subtitle" class="text-[11px] text-muted truncate">{{ it.subtitle }}</div>
        </div>
        <button class="btn-ghost !text-red-400 hover:!bg-red-500/10 !px-2" @click="remove(it)">
          <span class="i-carbon-favorite-filled" />
        </button>
      </div>
    </div>

    <!-- Movie detail drawer (movie tab opens this in place) -->
    <MovieDetailDrawer v-model="drawerOpen" :movie-id="drawerId" />
  </div>
</template>
