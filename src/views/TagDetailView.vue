<script setup lang="ts">
import { ref, watch, onMounted, computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { tags } from '@/api/worker'
import type { Movie, Tag } from '@/types'
import { useFavoritesStore } from '@/stores/favorites'
import MovieCard from '@/components/MovieCard.vue'
import MovieDetailDrawer from '@/components/MovieDetailDrawer.vue'
import { t } from '@/utils/i18n'

const route = useRoute()
const router = useRouter()
const favs = useFavoritesStore()
const tag = ref<Tag | null>(null)
const movies = ref<Movie[]>([])
const loading = ref(false)
const drawerId = ref<number | null>(null)
const drawerOpen = ref(false)

async function load() {
  loading.value = true
  try {
    const id = Number(route.params.id)
    const [info, ms] = await Promise.all([tags.info(id), tags.movies(id)])
    tag.value = info
    movies.value = ms
    await favs.load('tag')
  } finally {
    loading.value = false
  }
}
function openDetail(m: Movie) { drawerId.value = m.id ?? null; drawerOpen.value = true }
const tagId = () => Number(route.params.id)
const isTagFav = computed(() => favs.tagIds.includes(tagId()))
onMounted(() => { favs.ensureLoaded('tag'); load() })
watch(() => route.params.id, load)
</script>

<template>
  <div class="p-8">
    <button class="btn-ghost mb-4" @click="router.push('/tags')">
      <span class="i-carbon-arrow-left" /> {{ t('backToTags') }}
    </button>

    <div class="flex items-center gap-2 mb-6">
      <span class="i-carbon-tag text-xl text-primary" />
      <!-- Show the real tag name + movie count (not a fixed "标签影片"). -->
      <h1 class="text-2xl font-bold tracking-tight">
        {{ tag?.name ?? t('tagTitle') }}
        <span v-if="tag?.movieCount" class="text-base font-normal text-muted">· {{ tag.movieCount }} {{ t('part') }}</span>
      </h1>
      <!-- favorite heart -->
      <button
        class="ml-auto w-9 h-9 rounded-full flex items-center justify-center transition-all hover:scale-110"
        :class="isTagFav ? '!text-red-500 bg-red-500/10' : 'text-muted hover:text-text bg-surface2'"
        :title="t('favTag')"
        @click="favs.toggle('tag', tagId())"
      >
        <span :class="isTagFav ? 'i-carbon-favorite-filled' : 'i-carbon-favorite'" />
      </button>
    </div>

    <div v-if="loading" class="text-muted text-sm py-12 text-center">{{ t('loading') }}</div>
    <div v-else-if="!movies.length" class="card !rounded-lg p-12 text-center text-muted text-sm">
      <span class="i-carbon-video block text-4xl mb-3 opacity-50" />
      {{ t('noTagMovies') }}
    </div>
    <div v-else class="grid gap-4" style="grid-template-columns: repeat(auto-fill, minmax(176px, 1fr));">
      <MovieCard v-for="m in movies" :key="m.id" :movie="m" @click="openDetail(m)" />
    </div>

    <MovieDetailDrawer v-model="drawerOpen" :movie-id="drawerId" @changed="load" />
  </div>
</template>
