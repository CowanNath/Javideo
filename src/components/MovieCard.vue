<script setup lang="ts">
import { computed } from 'vue'
import type { Movie } from '@/types'
import { useFavoritesStore } from '@/stores/favorites'
import { t } from '@/utils/i18n'

const props = defineProps<{ movie: Movie; size?: 'sm' | 'md' | 'lg'; libraryName?: string }>()
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
  <!-- w-full: fill the parent grid column (minmax tracks) instead of a fixed
       width, so cards stretch uniformly in every grid. -->
  <div
    class="movie-card group cursor-pointer w-full rounded-lg overflow-hidden bg-surface border border-border transition-all duration-200 hover:border-primary hover:shadow-lg"
    @click="emit('click', movie)"
  >
    <!-- 16:9 landscape still (thumb first), like the reference design -->
    <div class="aspect-video bg-surface2 overflow-hidden relative">
      <img
        v-if="movie.thumbUrl || movie.coverUrl"
        :src="(movie.thumbUrl || movie.coverUrl) as string"
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

      <!-- video / trailer badges (top-left, streaming-style pills) -->
      <div v-if="movie.hasVideo || movie.hasTrailer" class="absolute top-2 left-2 flex gap-1">
        <span
          v-if="movie.hasVideo"
          class="h-[18px] px-1.5 rounded-[5px] flex items-center gap-1 text-[10px] font-medium leading-none"
          style="background: rgba(0,0,0,0.55); backdrop-filter: blur(6px); color: var(--status-green);"
          :title="t('hasVideo')"
        >
          <span class="i-carbon-video text-[11px]" />{{ t('hasVideo') }}
        </span>
        <span
          v-if="movie.hasTrailer"
          class="h-[18px] px-1.5 rounded-[5px] flex items-center gap-1 text-[10px] font-medium leading-none"
          style="background: rgba(0,0,0,0.55); backdrop-filter: blur(6px); color: var(--primary);"
          :title="t('hasTrailer')"
        >
          <span class="i-carbon-play-filled-alt text-[11px]" />{{ t('hasTrailer') }}
        </span>
      </div>
    </div>

    <!-- Caption: title + heart, then date + rating badge -->
    <div class="px-2.5 pt-2 pb-2.5">
      <div class="flex items-center justify-between gap-2">
        <div class="text-[13px] font-medium leading-tight truncate">{{ movie.title || movie.number }}</div>
        <!-- favorite heart: always visible, red when saved -->
        <button
          v-if="movie.id != null"
          class="shrink-0 w-6 h-6 -mr-1 rounded flex items-center justify-center transition-all duration-150 hover:scale-110"
          :class="isFav ? 'text-red-500' : 'text-muted hover:text-text'"
          :title="t('favorites')"
          :aria-label="t('favorites')"
          @click="toggleFav"
        >
          <span :class="isFav ? 'i-carbon-favorite-filled' : 'i-carbon-favorite'" class="text-[15px]" />
        </button>
      </div>
      <div v-if="libraryName" class="flex items-center gap-1 mt-1.5 text-[11px] text-muted" :title="`${t('libraries')}: ${libraryName}`">
        <span class="i-carbon-folder shrink-0" />
        <span class="truncate">{{ libraryName }}</span>
      </div>
      <div class="flex items-center justify-between gap-2 mt-1.5">
        <span class="text-[11px] text-muted">{{ movie.releaseDate?.slice(0, 10) }}</span>
        <span
          v-if="movie.score"
          class="text-[11px] font-semibold px-1.5 py-0.5 rounded-[5px] leading-none"
          style="background: var(--accent); color: var(--on-accent);"
        >★ {{ movie.score }}</span>
        <span
          v-else-if="movie.runtimeMinutes"
          class="text-[11px] font-semibold px-1.5 py-0.5 rounded-[5px] leading-none"
          style="background: var(--accent); color: var(--on-accent);"
        >{{ movie.runtimeMinutes }} {{ t('minutes') }}</span>
      </div>
    </div>
  </div>
</template>
