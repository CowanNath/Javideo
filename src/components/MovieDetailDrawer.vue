<script setup lang="ts">
import { ref, watch, computed, onMounted, onUnmounted, nextTick } from 'vue'
import { useRouter } from 'vue-router'
import { movies as moviesApi, metatube } from '@/api/worker'
import type { Movie } from '@/types'
import { useFavoritesStore } from '@/stores/favorites'
import { useBackdropClose } from '@/utils/clickOutside'
import { confirmDialog } from '@/utils/confirm'
import { toast } from '@/utils/toast'
import { t as ti } from '@/utils/i18n'

const props = defineProps<{ modelValue: boolean; movieId: number | null }>()
const emit = defineEmits<{
  'update:modelValue': [v: boolean]
  changed: []            // emitted after delete/rescrape so parent can reload
}>()

const router = useRouter()
const favs = useFavoritesStore()
favs.ensureLoaded('movie')
const backdrop = useBackdropClose(() => close())

// Reactive favorite state for the heart (computed → dependency tracked).
const isFav = computed(() => movie.value?.id != null && favs.movieIds.includes(movie.value.id))

// Lightbox state.
const previewIdx = ref<number | null>(null)
const previewImg = computed(() =>
  previewIdx.value != null ? movie.value?.previewImages?.[previewIdx.value] ?? undefined : undefined
)
function prevPreview() {
  const imgs = movie.value?.previewImages
  if (!imgs?.length || previewIdx.value == null) return
  previewIdx.value = (previewIdx.value - 1 + imgs.length) % imgs.length
}
function nextPreview() {
  const imgs = movie.value?.previewImages
  if (!imgs?.length || previewIdx.value == null) return
  previewIdx.value = (previewIdx.value + 1) % imgs.length
}
function onKeydown(e: KeyboardEvent) {
  if (previewIdx.value == null) return
  if (e.key === 'Escape') { previewIdx.value = null }
  else if (e.key === 'ArrowLeft') { e.preventDefault(); prevPreview() }
  else if (e.key === 'ArrowRight') { e.preventDefault(); nextPreview() }
}

// Keyboard navigation for lightbox.
watch(previewIdx, (v) => {
  if (v != null) window.addEventListener('keydown', onKeydown)
  else window.removeEventListener('keydown', onKeydown)
})
onUnmounted(() => window.removeEventListener('keydown', onKeydown))

// Drag-to-scroll for the preview gallery (document-level listeners for smooth tracking).
const galleryEl = ref<HTMLElement | null>(null)
function startDrag(e: MouseEvent) {
  const el = galleryEl.value
  if (!el) return
  e.preventDefault()
  const startX = e.clientX, startScroll = el.scrollLeft
  const onMove = (e2: MouseEvent) => { el.scrollLeft = startScroll - (e2.clientX - startX) }
  const onUp = () => { document.removeEventListener('mousemove', onMove); document.removeEventListener('mouseup', onUp) }
  document.addEventListener('mousemove', onMove)
  document.addEventListener('mouseup', onUp)
}

const movie = ref<Movie | null>(null)
const loading = ref(false)
const playing = ref(false)
const playMsg = ref('')
const busy = ref(false)
const trailerSrc = ref<string>('')
const showTrailer = ref(false)
const actionMsg = ref('')
const actionErr = ref(false)

function close() { emit('update:modelValue', false) }

async function load() {
  if (props.movieId == null) { movie.value = null; return }
  loading.value = true
  actionMsg.value = ''
  try {
    movie.value = await moviesApi.get(props.movieId)
    // Resolve the trailer URL once (only if a trailer file exists).
    trailerSrc.value = movie.value?.hasTrailer ? await moviesApi.trailerUrl(props.movieId) : ''
    showTrailer.value = false
  } catch (e: any) {
    actionMsg.value = ti('loadFailed') + ': ' + e.message; actionErr.value = true
  } finally { loading.value = false }
}
watch(() => [props.modelValue, props.movieId], async ([open]) => {
  if (open) await load()
}, { immediate: true })

async function play() {
  if (!movie.value?.id) return
  playing.value = true; playMsg.value = ''
  try {
    const res = await moviesApi.play(movie.value.id)
    playMsg.value = res.detail
  } catch (e: any) { playMsg.value = ti('playFailed') + ': ' + e.message }
  finally { playing.value = false }
}

async function remove() {
  if (!movie.value?.id) return
  if (!await confirmDialog(ti('deleteConfirm'), ti('deleteMovie'))) return
  busy.value = true
  try {
    await moviesApi.remove(movie.value.id, true)
    emit('changed')
    close()
  } catch (e: any) { actionMsg.value = ti('deleteFailed') + ': ' + e.message; actionErr.value = true }
  finally { busy.value = false }
}

const rescapeCandidates = ref<{ provider: string; id: string; title?: string | null; coverUrl?: string | null }[]>([])
const showRescrapePicker = ref(false)

async function rescrape() {
  if (!movie.value?.id || !movie.value.number) return
  busy.value = true; actionMsg.value = ti('scraping'); actionErr.value = false
  try {
    // First fetch candidates — if multiple, show picker.
    const cands = await metatube.candidates(movie.value.number)
    if (cands.length > 1) {
      rescapeCandidates.value = cands
      showRescrapePicker.value = true
      busy.value = false
      actionMsg.value = ''
      return
    }
    // Single result — scrape directly.
    await doRescrape()
  } catch { await doRescrape() }
  finally { busy.value = false }
}

async function doRescrape(provider?: string, id?: string) {
  if (!movie.value?.id) return
  busy.value = true; actionMsg.value = ti('scraping'); actionErr.value = false
  showRescrapePicker.value = false
  try {
    const res = provider && id
      ? await moviesApi.rescrapePick(movie.value.id, provider, id)
      : await moviesApi.rescrape(movie.value.id)
    actionMsg.value = res.detail || ti('scraped'); actionErr.value = false
    await load()
    emit('changed')
  } catch (e: any) { actionMsg.value = ti('rescrapeFail') + ': ' + e.message; actionErr.value = true }
  finally { busy.value = false }
}

async function toggleFav() {
  if (movie.value?.id) await favs.toggle('movie', movie.value.id)
}

async function openFolder() {
  if (!movie.value?.folderPath) return
  try {
    const { invoke } = await import('@tauri-apps/api/core')
    await invoke('shell_open', { path: movie.value.folderPath })
  } catch {
    // Fallback: use worker API
    try { await fetch(`${await import('@/api/worker').then(m => m.getBaseUrl())}/api/movies/${movie.value.id}/open-folder`, { method: 'POST' }) } catch {}
  }
}

const addingTag = ref(false)
const newTagName = ref('')
const tagInput = ref<HTMLInputElement | null>(null)

function startAddTag() {
  newTagName.value = ''
  addingTag.value = true
  nextTick(() => tagInput.value?.focus())
}
async function commitTag() {
  const name = newTagName.value.trim()
  if (!name || !movie.value?.id) return
  try {
    await moviesApi.addTag(movie.value.id, name)
    toast(ti('tagAdded'), 'success')
    addingTag.value = false
    await load()
  } catch (e: any) {
    toast(e.message, 'error')
  }
}
</script>

<template>
  <Teleport to="body">
    <div
      v-if="modelValue"
      class="fixed inset-0 z-50 flex justify-end"
      style="background: rgba(0,0,0,0.5); backdrop-filter: blur(2px);"
      @mousedown="backdrop.onMouseDown"
      @mouseup="backdrop.onMouseUp"
    >
      <div class="bg-surface border-l border-border w-[420px] max-w-[90vw] overflow-y-auto shadow-lg">
        <template v-if="loading">
          <div class="p-12 text-center text-muted text-sm">{{ ti('loading') }}</div>
        </template>
        <template v-else-if="movie">
          <!-- Cover banner with favorite heart + close -->
          <div class="relative">
            <img
              v-if="movie.thumbUrl || movie.coverUrl"
              :src="(movie.thumbUrl || movie.coverUrl) as string"
              class="w-full h-56 object-cover block"
              referrerpolicy="no-referrer"
            />
            <div v-else class="w-full h-56 bg-surface2 flex items-center justify-center text-muted">
              <span class="i-carbon-image text-4xl" />
            </div>
            <!-- favorite heart (top-left) -->
            <button
              v-if="movie.id"
              class="absolute top-3 left-3 w-9 h-9 rounded-full flex items-center justify-center text-white transition-all hover:scale-110"
              :class="isFav ? '!text-red-500' : ''"
              style="background: rgba(0,0,0,0.55); backdrop-filter: blur(4px);"
              :title="ti('favorites')"
              @click="toggleFav"
            >
              <span :class="isFav ? 'i-carbon-favorite-filled' : 'i-carbon-favorite'" />
            </button>
            <button
              class="absolute top-3 right-3 w-8 h-8 rounded-full flex items-center justify-center text-white"
              style="background: rgba(0,0,0,0.5); backdrop-filter: blur(4px);"
              @click="close"
            >
              <span class="i-carbon-close" />
            </button>
          </div>

          <!-- Preview images gallery (drag-to-scroll + double-click to lightbox) -->
          <div
            ref="galleryEl"
            v-if="trailerSrc || movie.previewImages?.length"
            class="flex gap-2 overflow-x-auto px-5 py-3 bg-surface2/50 cursor-grab select-none"
            style="-webkit-overflow-scrolling:touch; scrollbar-width:thin"
            @mousedown="startDrag"
          >
            <!-- Trailer (first) -->
            <div
              v-if="trailerSrc"
              class="relative h-28 w-44 shrink-0 rounded-md overflow-hidden border border-primary pointer-events-auto cursor-pointer group"
              @click.stop="showTrailer = true"
            >
              <video :src="trailerSrc" class="h-full w-full object-cover" muted preload="metadata" />
              <div class="absolute inset-0 flex items-center justify-center bg-black/40 group-hover:bg-black/20 transition-colors">
                <span class="i-carbon-play-filled-alt text-white text-3xl drop-shadow" />
              </div>
            </div>
            <img
              v-for="(img, i) in movie.previewImages"
              :key="i"
              :src="img"
              class="h-28 w-auto rounded-md shrink-0 object-cover border border-border cursor-zoom-in hover:brightness-90 transition-all pointer-events-auto"
              referrerpolicy="no-referrer"
              loading="lazy"
              @dblclick="previewIdx = i"
            />
          </div>

          <div class="p-5">
            <!-- order per search: number+score, title, actors, other, summary -->
            <div class="flex items-center gap-2 flex-wrap mb-1.5">
              <span class="text-primary font-bold text-base">{{ movie.number }}</span>
              <span v-if="movie.score" class="chip !text-amber-400 !bg-amber-500/10 !border-amber-500/20">★ {{ movie.score }}</span>
            </div>
            <h2 class="text-lg font-bold leading-tight">{{ movie.title || movie.number }}</h2>

            <!-- actors (clickable) -->
            <div v-if="movie.actors?.length" class="mt-3">
              <button
                v-for="a in movie.actors"
                :key="a.id ?? a.name"
                class="inline-flex items-center gap-1.5 mr-2 mb-1 hover:text-primary transition-colors"
                @click="a.id && router.push(`/actors/${a.id}`)"
              >
                <span class="w-5 h-5 rounded-full bg-surface2 overflow-hidden inline-block align-middle">
                  <img v-if="a.avatarUrl" :src="a.avatarUrl" class="w-full h-full object-cover" referrerpolicy="no-referrer" />
                </span>
                <span class="text-[12px]">{{ a.name }}</span>
              </button>
            </div>

            <!-- other info -->
            <div class="flex flex-wrap gap-x-5 gap-y-1.5 text-[12px] text-text-soft mt-3">
              <span v-if="movie.maker" class="flex items-center gap-1.5"><span class="i-carbon-building text-muted" />{{ movie.maker }}</span>
              <span v-if="movie.releaseDate" class="flex items-center gap-1.5"><span class="i-carbon-calendar text-muted" />{{ movie.releaseDate.slice(0,10) }}</span>
              <span v-if="movie.runtimeMinutes" class="flex items-center gap-1.5"><span class="i-carbon-time text-muted" />{{ movie.runtimeMinutes }} {{ ti('minutes') }}</span>
            </div>

            <!-- tags (clickable) + add tag -->
            <div class="flex flex-wrap gap-1.5 mt-3">
              <span v-for="t in movie.tags" :key="t.id ?? t.name" class="chip" @click="t.id && router.push(`/tags/${t.id}`)">{{ t.name }}</span>
              <button v-if="!addingTag" class="chip !px-2 !text-muted hover:!text-text hover:!border-primary" :title="ti('addCustomTag')" @click="startAddTag">
                <span class="i-carbon-add text-[14px]" />
              </button>
              <input v-else ref="tagInput" v-model="newTagName"
                class="chip !outline-none !border-primary !px-2 !py-0 !w-28 !text-[12px]"
                :placeholder="ti('tagName')" @keydown.enter.prevent="commitTag"
                @keydown.escape.prevent="addingTag = false" @blur="commitTag" />
            </div>

            <p v-if="movie.summary" class="text-[13px] text-text-soft leading-relaxed mt-3">{{ movie.summary }}</p>

            <div v-if="movie.folderPath" class="flex items-start gap-2 mt-3 text-[11px]">
              <span class="i-carbon-folder mt-0.5 text-muted" />
              <span class="break-all text-muted flex-1">{{ movie.folderPath }}</span>
              <button class="btn-ghost !text-primary !py-0.5 !px-2 shrink-0 text-[11px]" @click="openFolder">
                <span class="i-carbon-folder-open" /> {{ ti('openFolder') }}
              </button>
            </div>

            <!-- actions -->
            <div class="flex items-center gap-2 mt-5 pt-4 border-t border-border">
              <button class="btn-primary flex-1" :disabled="playing || busy" @click="play">
                <span class="i-carbon-play-filled" /> {{ playing ? ti('starting') : ti('play') }}
              </button>
              <button class="btn" :disabled="busy" :title="busy ? ti('scraping') : ti('rescrape')" @click="rescrape">
                <span :class="busy ? 'i-carbon-renew animate-spin' : 'i-carbon-renew'" />
              </button>
              <button class="btn hover:!text-red-400" :disabled="busy" :title="ti('delete')" @click="remove">
                <span class="i-carbon-trash-can" />
              </button>
            </div>
            <p v-if="playMsg" class="text-[12px] text-muted mt-2">{{ playMsg }}</p>
            <p
              v-if="actionMsg"
              class="text-[12px] mt-2"
              :class="actionErr ? 'text-red-400' : 'text-status-green'"
            >{{ actionMsg }}</p>
          </div>
        </template>
      </div>
    </div>

    <!-- Image lightbox (double-click preview to open, prev/next, keyboard) -->
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
        <!-- Prev -->
        <button
          class="absolute left-4 top-1/2 -translate-y-1/2 w-10 h-10 rounded-full flex items-center justify-center text-white hover:scale-110 transition-all pointer-events-auto"
          style="background: rgba(0,0,0,0.6); backdrop-filter: blur(4px);"
          @click.stop="prevPreview"
        >
          <span class="i-carbon-chevron-left text-xl" />
        </button>
        <!-- Next -->
        <button
          class="absolute right-4 top-1/2 -translate-y-1/2 w-10 h-10 rounded-full flex items-center justify-center text-white hover:scale-110 transition-all pointer-events-auto"
          style="background: rgba(0,0,0,0.6); backdrop-filter: blur(4px);"
          @click.stop="nextPreview"
        >
          <span class="i-carbon-chevron-right text-xl" />
        </button>
        <!-- Counter -->
        <span
          class="absolute bottom-6 left-1/2 -translate-x-1/2 px-3 py-1 rounded-full text-xs text-white/80"
          style="background: rgba(0,0,0,0.6);"
        >
          {{ (previewIdx ?? 0) + 1 }} / {{ movie?.previewImages?.length ?? 0 }}
        </span>
        <!-- Close -->
        <button
          class="absolute top-4 right-4 w-9 h-9 rounded-full flex items-center justify-center text-white/80 hover:text-white hover:scale-110 transition-all"
          style="background: rgba(0,0,0,0.6); backdrop-filter: blur(4px);"
          @click.stop="previewIdx = null"
        >
          <span class="i-carbon-close text-lg" />
        </button>
      </div>
    </Teleport>
  </Teleport>

  <!-- Trailer player overlay -->
  <Teleport to="body">
    <div
      v-if="showTrailer && trailerSrc"
      class="fixed inset-0 z-[80] flex items-center justify-center p-8"
      style="background: rgba(0,0,0,0.9); backdrop-filter: blur(6px);"
      @click.self="showTrailer = false"
    >
      <div class="relative max-w-[90vw] max-h-[90vh]">
        <video
          :src="trailerSrc"
          class="max-w-[88vw] max-h-[86vh] rounded-lg shadow-2xl bg-black"
          controls
          autoplay
        />
        <button
          class="absolute -top-3 -right-3 w-9 h-9 rounded-full flex items-center justify-center text-white shadow-lg"
          style="background: rgba(0,0,0,0.7);"
          @click="showTrailer = false"
        >
          <span class="i-carbon-close text-lg" />
        </button>
      </div>
    </div>
  </Teleport>

  <!-- Rescrape candidate picker -->
  <Teleport to="body">
    <div
      v-if="showRescrapePicker"
      class="fixed inset-0 z-[75] flex items-center justify-center p-8"
      style="background: rgba(0,0,0,0.7); backdrop-filter: blur(3px);"
      @click.self="showRescrapePicker = false"
    >
      <div class="card !rounded-lg p-5 w-[600px] max-w-[90vw] max-h-[80vh] overflow-y-auto">
        <h3 class="text-[15px] font-semibold mb-3">{{ ti('multipleResults') }}</h3>
        <div class="flex gap-2 overflow-x-auto pb-2">
          <button
            v-for="(c, i) in rescapeCandidates"
            :key="i"
            class="card !rounded-md p-2 w-36 shrink-0 text-left"
            @click="doRescrape(c.provider, c.id)"
          >
            <div class="aspect-[2/3] bg-surface2 rounded overflow-hidden mb-1.5">
              <img v-if="c.coverUrl" :src="c.coverUrl" class="w-full h-full object-cover" referrerpolicy="no-referrer" loading="lazy" />
              <div v-else class="w-full h-full flex items-center justify-center text-muted"><span class="i-carbon-image text-2xl" /></div>
            </div>
            <div class="text-[10px] text-muted truncate">[{{ c.provider }}]</div>
            <div class="text-[11px] truncate leading-tight">{{ c.title || c.id }}</div>
          </button>
        </div>
        <div class="flex justify-end mt-3">
          <button class="btn" @click="showRescrapePicker = false">{{ ti('cancel') }}</button>
        </div>
      </div>
    </div>
  </Teleport>
</template>
