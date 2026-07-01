<script setup lang="ts">
import { ref, watch, onMounted, onUnmounted, computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { actors } from '@/api/worker'
import type { ActorDetail, Movie } from '@/types'
import { useFavoritesStore } from '@/stores/favorites'
import { t } from '@/utils/i18n'
import MovieCard from '@/components/MovieCard.vue'
import MovieDetailDrawer from '@/components/MovieDetailDrawer.vue'

const route = useRoute()
const router = useRouter()
const favs = useFavoritesStore()
const actor = ref<ActorDetail | null>(null)
const movies = ref<Movie[]>([])
const loading = ref(false)
const configError = ref('')
const drawerId = ref<number | null>(null)
const drawerOpen = ref(false)
// Image lightbox with prev/next navigation.
const previewIdx = ref<number | null>(null)
const previewImg = computed(() =>
  previewIdx.value != null ? actor.value?.images?.[previewIdx.value] ?? undefined : undefined
)
function prevPreview() {
  const imgs = actor.value?.images
  if (!imgs?.length || previewIdx.value == null) return
  previewIdx.value = (previewIdx.value - 1 + imgs.length) % imgs.length
}
function nextPreview() {
  const imgs = actor.value?.images
  if (!imgs?.length || previewIdx.value == null) return
  previewIdx.value = (previewIdx.value + 1) % imgs.length
}
watch(previewIdx, (v) => {
  if (v != null) window.addEventListener('keydown', onKeydown)
  else window.removeEventListener('keydown', onKeydown)
})
onUnmounted(() => window.removeEventListener('keydown', onKeydown))
function onKeydown(e: KeyboardEvent) {
  if (previewIdx.value == null) return
  if (e.key === 'Escape') { previewIdx.value = null }
  else if (e.key === 'ArrowLeft') { e.preventDefault(); prevPreview() }
  else if (e.key === 'ArrowRight') { e.preventDefault(); nextPreview() }
}

async function load() {
  loading.value = true
  configError.value = ''
  try {
    const res = await actors.detail(Number(route.params.id))
    actor.value = res.actor
    movies.value = res.movies
    configError.value = res.configError ?? ''
    if (!actor.value) actor.value = { name: res.name }
  } catch (e: any) {
    configError.value = '加载失败: ' + e.message
  } finally {
    loading.value = false
  }
}
function openDetail(m: Movie) { drawerId.value = m.id ?? null; drawerOpen.value = true }
function fmtBirthday(d?: string | null) {
  if (!d) return ''
  return d.startsWith('0001') ? '' : d.slice(0, 10)
}
const actorId = () => Number(route.params.id)
const isActorFav = computed(() => favs.actorIds.includes(actorId()))
onMounted(() => { favs.ensureLoaded('actor'); load() })
watch(() => route.params.id, load)
</script>

<template>
  <div class="p-8">
    <button class="btn-ghost mb-4" @click="router.push('/actors')">
      <span class="i-carbon-arrow-left" /> {{ t('backToActors') }}
    </button>

    <div v-if="loading" class="text-muted text-sm py-12 text-center">{{ t('loading') }}</div>

    <template v-else>
      <p v-if="configError" class="text-amber-400 text-[13px] mb-4 p-3 rounded-md bg-amber-500/10 border border-amber-500/30">
        {{ configError }}
      </p>

      <!-- Actor profile with favorite heart (top-right) -->
      <section v-if="actor" class="card !rounded-lg p-6 mb-8 relative">
        <button
          class="absolute top-4 right-4 w-9 h-9 rounded-full flex items-center justify-center transition-all hover:scale-110"
          :class="isActorFav ? '!text-red-500 bg-red-500/10' : 'text-muted hover:text-text bg-surface2'"
          :title="t('favorites')"
          @click="favs.toggle('actor', actorId())"
        >
          <span :class="isActorFav ? 'i-carbon-favorite-filled' : 'i-carbon-favorite'" />
        </button>

        <div class="flex gap-6">
          <div class="shrink-0 w-40">
            <div class="w-40 h-40 rounded-lg overflow-hidden bg-surface2 ring-2 ring-border">
              <img v-if="actor.avatarUrl" :src="actor.avatarUrl" class="w-full h-full object-cover" referrerpolicy="no-referrer" />
              <div v-else class="w-full h-full flex items-center justify-center text-muted">
                <span class="i-carbon-user-filled text-5xl" />
              </div>
            </div>
            <div v-if="actor.images?.length" class="flex flex-wrap gap-1 mt-2">
              <button
                v-for="(img, i) in actor.images.slice(0, 6)"
                :key="i"
                class="w-9 h-9 rounded overflow-hidden border border-border hover:border-primary cursor-zoom-in"
                @click="previewIdx = i"
              >
                <img :src="img" class="w-full h-full object-cover" referrerpolicy="no-referrer" loading="lazy" />
              </button>
            </div>
          </div>
          <div class="flex-1 min-w-0">
            <h1 class="text-2xl font-bold tracking-tight">{{ actor.name }}</h1>
            <div v-if="actor.aliases?.length" class="text-[13px] text-muted mt-1">别名:{{ actor.aliases.join('、') }}</div>
            <div class="grid grid-cols-2 md:grid-cols-3 gap-x-6 gap-y-2 mt-4 text-[13px]">
              <div v-if="fmtBirthday(actor.birthday)" class="flex items-center gap-2"><span class="i-carbon-calendar text-muted" />{{ fmtBirthday(actor.birthday) }}</div>
              <div v-if="actor.height" class="flex items-center gap-2"><span class="i-carbon-rule text-muted" />{{ actor.height }} cm</div>
              <div v-if="actor.measurements" class="flex items-center gap-2"><span class="i-carbon-text-scale text-muted" />{{ actor.measurements }}</div>
              <div v-if="actor.cupSize" class="flex items-center gap-2"><span class="i-carbon-gender-female text-muted" />{{ actor.cupSize }}</div>
              <div v-if="actor.bloodType" class="flex items-center gap-2"><span class="i-carbon-warning-alt text-muted" />{{ actor.bloodType }} 型</div>
              <div v-if="actor.nationality" class="flex items-center gap-2"><span class="i-carbon-earth text-muted" />{{ actor.nationality }}</div>
            </div>
            <p v-if="actor.summary" class="text-[13px] text-text-soft leading-relaxed mt-4">{{ actor.summary }}</p>
          </div>
        </div>
      </section>

      <!-- Works -->
      <section>
        <div class="flex items-center gap-2 mb-4">
          <span class="i-carbon-video text-lg text-primary" />
          <h2 class="text-lg font-semibold">{{ t('relatedWorks') }}</h2>
          <span v-if="movies.length" class="text-xs text-muted">({{ movies.length }})</span>
        </div>
        <div v-if="!movies.length" class="card !rounded-lg p-12 text-center text-muted text-sm">
          <span class="i-carbon-video block text-4xl mb-3 opacity-50" />
          {{ t('noWorks') }}
        </div>
        <div v-else class="grid gap-4" style="grid-template-columns: repeat(auto-fill, minmax(176px, 1fr));">
          <MovieCard v-for="m in movies" :key="m.id" :movie="m" @click="openDetail(m)" />
        </div>
      </section>
    </template>

    <MovieDetailDrawer v-model="drawerOpen" :movie-id="drawerId" @changed="load" />

    <!-- Image lightbox (prev/next, keyboard) -->
    <Teleport to="body">
      <div
        v-if="previewIdx != null"
        class="fixed inset-0 z-[70] flex items-center justify-center p-8"
        style="background: rgba(0,0,0,0.88); backdrop-filter: blur(6px);"
        @click="previewIdx = null"
      >
        <img
          :src="previewImg"
          class="max-w-[85vw] max-h-[90vh] object-contain rounded-lg shadow-2xl pointer-events-none select-none"
          referrerpolicy="no-referrer"
        />
        <button
          class="absolute left-4 top-1/2 -translate-y-1/2 w-10 h-10 rounded-full flex items-center justify-center text-white hover:scale-110 transition-all pointer-events-auto"
          style="background: rgba(0,0,0,0.6); backdrop-filter: blur(4px);"
          @click.stop="prevPreview"
        ><span class="i-carbon-chevron-left text-xl" /></button>
        <button
          class="absolute right-4 top-1/2 -translate-y-1/2 w-10 h-10 rounded-full flex items-center justify-center text-white hover:scale-110 transition-all pointer-events-auto"
          style="background: rgba(0,0,0,0.6); backdrop-filter: blur(4px);"
          @click.stop="nextPreview"
        ><span class="i-carbon-chevron-right text-xl" /></button>
        <span
          class="absolute bottom-6 left-1/2 -translate-x-1/2 px-3 py-1 rounded-full text-xs text-white/80"
          style="background: rgba(0,0,0,0.6);"
        >{{ (previewIdx ?? 0) + 1 }} / {{ actor?.images?.length ?? 0 }}</span>
        <button
          class="absolute top-4 right-4 w-9 h-9 rounded-full flex items-center justify-center text-white/80 hover:text-white hover:scale-110 transition-all"
          style="background: rgba(0,0,0,0.6); backdrop-filter: blur(4px);"
          @click.stop="previewIdx = null"
        ><span class="i-carbon-close text-lg" /></button>
      </div>
    </Teleport>
  </div>
</template>
