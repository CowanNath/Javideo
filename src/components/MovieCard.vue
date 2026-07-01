<script setup lang="ts">
import { computed } from 'vue'
import type { Movie } from '@/types'
import { useFavoritesStore } from '@/stores/favorites'

const props = defineProps<{ movie: Movie; size?: 'sm' | 'md' | 'lg' }>()
const emit = defineEmits<{ click: [movie: Movie] }>()

const favs = useFavoritesStore()
// Make sure movie favorites are loaded once so the heart shows real state.
favs.ensureLoaded('movie')

// Wrap the store check in a computed so the reactive dependency on
// favs.movieIds is tracked explicitly — calling a method inside :class can
// fail to re-render in some setups.
const isFav = computed(() =>
  props.movie.id != null && favs.movieIds.includes(props.movie.id)
)

async function toggleFav(e: Event) {
  e.stopPropagation()
  if (props.movie.id != null) favs.toggle('movie', props.movie.id)
}
</script>

<template>
  <div class="card group cursor-pointer w-44" @click="emit('click', movie)">
    <div class="aspect-[2/3] bg-surface2 overflow-hidden relative">
      <img
        v-if="movie.coverUrl || movie.thumbUrl"
        :src="movie.coverUrl || movie.thumbUrl || ''"
        :alt="movie.number"
        class="w-full h-full object-cover group-hover:scale-105 transition-transform duration-300"
        loading="lazy"
        referrerpolicy="no-referrer"
      />
      <div v-else class="w-full h-full flex items-center justify-center text-muted">
        <span class="i-carbon-image text-3xl" />
      </div>

      <!-- 番号 badge -->
      <span
        class="absolute bottom-2 left-2 text-[11px] font-semibold px-2 py-0.5 rounded-md text-white"
        style="background: rgba(0,0,0,0.65); backdrop-filter: blur(4px);"
      >{{ movie.number }}</span>

      <!-- favorite heart (top-right, reactive state) -->
      <button
        v-if="movie.id != null"
        class="absolute top-2 right-2 w-7 h-7 rounded-full flex items-center justify-center text-white transition-all duration-150 hover:scale-110"
        :class="isFav ? '!text-red-500 opacity-100' : 'opacity-0 group-hover:opacity-100'"
        style="background: rgba(0,0,0,0.55); backdrop-filter: blur(4px);"
        title="收藏"
        @click="toggleFav"
      >
        <span :class="isFav ? 'i-carbon-favorite-filled' : 'i-carbon-favorite'" />
      </button>

      <!-- hover overlay -->
      <div
        class="absolute inset-0 opacity-0 group-hover:opacity-100 transition-opacity duration-200 flex items-end p-2.5"
        style="background: linear-gradient(to top, rgba(0,0,0,0.85), transparent 55%);"
      >
        <p class="text-white text-xs leading-snug line-clamp-3 drop-shadow">{{ movie.title || movie.number }}</p>
      </div>
    </div>
  </div>
</template>
