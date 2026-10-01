<script setup lang="ts">
import { ref, computed } from 'vue'
import { favorites } from '@/api/worker'
import type { FavoriteTarget, Favorite, Movie } from '@/types'
import { useRouter } from 'vue-router'
import { confirmDialog } from '@/utils/confirm'
import { toast } from '@/utils/toast'
import { t as ti } from '@/utils/i18n'
import { useFavoritesStore } from '@/stores/favorites'
import MovieCard from '@/components/MovieCard.vue'
import MovieDetailDrawer from '@/components/MovieDetailDrawer.vue'
import { useMovieDetail } from '@/utils/movieDetail'

const router = useRouter()
const favs = useFavoritesStore()
const tab = ref<FavoriteTarget>('movie')
const items = ref<Favorite[]>([])
const loading = ref(false)
const selected = ref<Set<number>>(new Set())
const search = ref('')
const sort = ref<'recent' | 'id'>('recent')
// Movie detail drawer (for the movie tab — open in place, not navigate).
const { drawerId, drawerOpen, openDetail } = useMovieDetail()

// Sequence guard so a slow tab's response can't overwrite the active tab's list.
let loadSeq = 0
async function load() {
  const seq = ++loadSeq
  const forTab = tab.value
  loading.value = true
  selected.value = new Set()
  // Load the heart store too, so card states (and instant removal on unfav)
  // are reactive.
  if (forTab !== 'tag') favs.ensureLoaded(forTab)
  try {
    const list = (await favorites.list(tab.value)) ?? []
    if (seq !== loadSeq || tab.value !== forTab) return
    items.value = list
  } catch (e: any) {
    if (seq === loadSeq) toast(ti('loadFailed') + ': ' + e.message, 'error')
  } finally {
    if (seq === loadSeq) loading.value = false
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

// Movie/actor cards read their heart state from the favorites store — when a
// heart is toggled off, the card disappears immediately without a refetch.
const displayed = computed(() => {
  const list = filtered.value
  if (tab.value === 'movie') return list.filter((it) => favs.movieIds.includes(it.targetId))
  if (tab.value === 'actor') return list.filter((it) => favs.actorIds.includes(it.targetId))
  return list
})

// Favorite → Movie for MovieCard. The backend already resolved Name=番号,
// Subtitle=标题, Cover to the local thumbnail endpoint, and the badge flags.
function asMovie(it: Favorite): Movie {
  return {
    id: it.targetId,
    number: it.name?.trim() || `#${it.targetId}`,
    title: it.subtitle ?? it.name,
    thumbUrl: it.cover ?? null,
    hasVideo: it.hasVideo ?? undefined,
    hasTrailer: it.hasTrailer ?? undefined,
  }
}

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
  try {
    await favorites.remove(tab.value, item.targetId)
    // Sync the heart store so detail pages don't show a stale favorite.
    await favs.load(tab.value)
    await load()
  } catch (e: any) {
    toast(ti('deleteFailed') + ': ' + e.message, 'error')
  }
}
async function batchRemove() {
  if (!selected.value.size) return
  const ids = items.value
    .filter((it) => selected.value.has(it.id))
    .map((it) => it.targetId)
    .filter((id): id is number => typeof id === 'number' && id > 0)
  if (!ids.length) return
  if (!await confirmDialog(`${ti('unfavConfirm')} ${ids.length} ${ti('itemsCount')}?`, ti('batchUnfav'))) return
  try {
    await favorites.batchRemove(tab.value, ids)
    await favs.load(tab.value)
    await load()
  } catch (e: any) {
    toast(ti('deleteFailed') + ': ' + e.message, 'error')
  }
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
    openDetail(asMovie(it))
  } else {
    router.push(`/${tab.value === 'tag' ? 'tags' : 'actors'}/${it.targetId}`)
  }
}
// Unfavorite straight from a card heart (movie cards use MovieCard's own
// heart — the store updates reactively and the card disappears).
async function unfavFromCard(it: Favorite) {
  if (it.targetId > 0) await favs.toggle(tab.value, it.targetId)
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
      <!-- Batch ops only in the tag list — movie/actor cards unfav via heart -->
      <template v-if="tab === 'tag'">
        <label class="flex items-center gap-2 text-[13px] text-text-soft cursor-pointer">
          <input type="checkbox" :checked="allSelected" @change="toggleAll" class="accent-[var(--primary)]" />
          {{ ti('selectAll') }}
        </label>
        <button class="btn !text-red-400 hover:!bg-red-500/10" :disabled="!selected.size" @click="batchRemove">
          <span class="i-carbon-trash-can" /> {{ ti('batchUnfav') }}({{ selected.size }})
        </button>
      </template>
    </div>

    <div v-if="loading" class="text-muted text-sm py-12 text-center">{{ ti('loading') }}</div>
    <div v-else-if="!items.length" class="card !rounded-lg p-12 text-center text-muted text-sm">
      <span class="i-carbon-favorite block text-4xl mb-3 opacity-50" />
      {{ ti('noFavs') }}{{ tabName(tab) }}
    </div>
    <div v-else-if="!displayed.length" class="text-muted text-sm py-8 text-center">{{ ti('noMatch') }}</div>

    <!-- Movies: same card grid as the library page -->
    <div v-else-if="tab === 'movie'" class="grid gap-4" style="grid-template-columns: repeat(auto-fill, minmax(230px, 1fr));">
      <MovieCard v-for="it in displayed" :key="it.id" :movie="asMovie(it)" @click="go(it)" />
    </div>

    <!-- Actors: same card style as the Actors page -->
    <div v-else-if="tab === 'actor'" class="grid gap-4" style="grid-template-columns: repeat(auto-fill, minmax(140px, 1fr));">
      <div v-for="it in displayed" :key="it.id" class="card cursor-pointer text-center p-4 group relative" @click="go(it)">
        <button
          class="absolute top-2 right-2 w-7 h-7 rounded-full flex items-center justify-center transition-all hover:scale-110 z-10"
          :class="favs.actorIds.includes(it.targetId) ? '!text-red-500' : 'text-muted opacity-0 group-hover:opacity-100'"
          style="background: var(--surface-3);"
          :title="ti('unfav')" :aria-label="ti('unfav')"
          @click.stop="unfavFromCard(it)"
        >
          <span class="i-carbon-favorite-filled" />
        </button>
        <div class="w-24 h-24 mx-auto mb-3 rounded-full bg-surface2 overflow-hidden ring-2 ring-border group-hover:ring-primary transition">
          <img v-if="it.cover" :src="it.cover" class="w-full h-full object-cover" referrerpolicy="no-referrer" />
          <div v-else class="w-full h-full flex items-center justify-center text-muted">
            <span class="i-carbon-user-filled text-4xl" />
          </div>
        </div>
        <div class="text-[13px] font-medium truncate">{{ display(it) }}</div>
      </div>
    </div>

    <!-- Tags: list with checkboxes + batch remove -->
    <div v-else class="flex flex-col gap-1.5">
      <div
        v-for="it in displayed"
        :key="it.id"
        class="card !rounded-md p-3 flex items-center gap-3"
        :class="selected.has(it.id) && '!border-primary'"
      >
        <input type="checkbox" :checked="selected.has(it.id)" class="accent-[var(--primary)]" @change="toggle(it.id)" />
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
    <MovieDetailDrawer v-model="drawerOpen" :movie-id="drawerId" @changed="load" />
  </div>
</template>
