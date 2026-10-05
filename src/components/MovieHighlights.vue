<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { highlights as api } from '@/api/worker'
import type { MovieHighlight, MovieHighlightAsset, SaveHighlightRequest } from '@/types'
import { confirmDialog } from '@/utils/confirm'
import { useBackdropClose } from '@/utils/clickOutside'
import { toast } from '@/utils/toast'
import { t } from '@/utils/i18n'
import { formatHighlightTime as time, parseHighlightTime } from '@/utils/highlightTime'

const props = defineProps<{ movieId: number; disabled?: boolean }>()
const emit = defineEmits<{ preview: [] }>()
type DisplayAsset = MovieHighlightAsset & { displayUrl: string }
type DisplayRecord = Omit<MovieHighlight, 'assets'> & { assets: DisplayAsset[] }
const records = ref<DisplayRecord[]>([])
const loading = ref(true)
const loadError = ref('')
const editing = ref(false)
const editingId = ref<number | null>(null)
const saving = ref(false)
const deletingId = ref<number | null>(null)
const title = ref(''), note = ref(''), start = ref(''), end = ref(''), source = ref('')
const retained = ref<DisplayAsset[]>([])
const added = ref<{ file: File; url: string; kind: 'image' | 'video' }[]>([])
const fileInput = ref<HTMLInputElement | null>(null)
const titleInput = ref<HTMLInputElement | null>(null)
const videoFiles = ref<string[]>([])
const filesLoading = ref(false)
const formError = ref('')
const busy = computed(() => saving.value || deletingId.value != null || props.disabled)
const snapshot = () => JSON.stringify([title.value, note.value, start.value, end.value, source.value, retained.value.map(a => a.id)])
let baseline = ''
const dirty = computed(() => editing.value && (added.value.length > 0 || snapshot() !== baseline))
const preview = ref<{ record: DisplayRecord; assets: DisplayAsset[]; index: number } | null>(null)
const previewAsset = computed(() => preview.value?.assets[preview.value.index])
const previewButton = ref<HTMLButtonElement | null>(null)
const previewVideo = ref<HTMLVideoElement | null>(null)
const videoFailed = ref(false)
let returnFocus: HTMLElement | null = null
let disposed = false
let loadController: AbortController | null = null
let editSeq = 0

function label(record: MovieHighlight) {
  return record.title || (record.startSeconds != null ? time(record.startSeconds) : t('highlightDefault'))
}
function range(record: MovieHighlight) {
  return time(record.startSeconds) + (record.endSeconds != null ? ` — ${time(record.endSeconds)}` : '')
}
async function display(record: MovieHighlight): Promise<DisplayRecord> {
  return { ...record, assets: await Promise.all(record.assets.map(async asset => ({ ...asset, displayUrl: await api.assetUrl(asset) }))) }
}
async function load() {
  loadController?.abort()
  const controller = new AbortController()
  loadController = controller
  loading.value = true; loadError.value = ''
  try {
    const result = await api.list(props.movieId, controller.signal)
    const hydrated = await Promise.all(result.map(display))
    if (disposed || controller.signal.aborted) return
    records.value = hydrated
  } catch (e: any) {
    if (!disposed && !controller.signal.aborted) loadError.value = e.message
  } finally { if (!disposed && !controller.signal.aborted) loading.value = false }
}
load()

function releaseAdded() {
  added.value.forEach(a => URL.revokeObjectURL(a.url))
  added.value = []
  if (fileInput.value) fileInput.value.value = ''
}
function resetEditor() {
  editSeq++
  releaseAdded()
  editing.value = false
  formError.value = ''
}
async function beforeLeave(): Promise<boolean> {
  if (saving.value || deletingId.value != null) { toast(t('highlightWait'), 'error'); return false }
  if (dirty.value && !await confirmDialog(t('highlightDiscard'), t('highlightEditor'))) return false
  resetEditor()
  closePreview()
  return true
}
async function handleEscape(): Promise<boolean> {
  if (preview.value) { closePreview(); return true }
  if (editing.value) { await beforeLeave(); return true }
  return busy.value
}
defineExpose({ beforeLeave, handleEscape })

async function edit(record?: DisplayRecord) {
  if (busy.value) return
  if (!await beforeLeave()) return
  const seq = ++editSeq
  editingId.value = record?.id ?? null
  title.value = record?.title || ''; note.value = record?.note || ''
  start.value = time(record?.startSeconds); end.value = time(record?.endSeconds)
  source.value = record?.sourceFileName || ''
  retained.value = [...(record?.assets || [])]
  baseline = snapshot()
  videoFiles.value = []
  editing.value = true
  filesLoading.value = true
  await nextTick(); titleInput.value?.focus()
  try {
    const files = await api.videoFiles(props.movieId)
    if (!disposed && seq === editSeq) videoFiles.value = files
  } catch (e: any) {
    if (!disposed && seq === editSeq) toast(t('highlightFilesFailed') + ': ' + e.message, 'error')
  } finally { if (!disposed && seq === editSeq) filesLoading.value = false }
}

function addFiles(event: Event) {
  const input = event.target as HTMLInputElement
  const files = Array.from(input.files || [])
  formError.value = ''
  const extensions = /\.(jpe?g|png|gif|webp|mp4|m4v|mov|webm|mkv|avi)$/i
  if (files.some(f => !extensions.test(f.name) || f.size === 0)) formError.value = t('highlightFileType')
  else if (retained.value.length + added.value.length + files.length > 12) formError.value = t('highlightFileCount')
  else if ([...added.value.map(a => a.file), ...files].reduce((total, f) => total + f.size, 0) > 256 * 1024 * 1024) formError.value = t('highlightFileSize')
  else added.value.push(...files.map(file => ({ file, url: URL.createObjectURL(file), kind: /\.(jpe?g|png|gif|webp)$/i.test(file.name) ? 'image' as const : 'video' as const })))
  input.value = ''
}
function removeAdded(index: number) {
  URL.revokeObjectURL(added.value[index].url)
  added.value.splice(index, 1)
}

async function save() {
  if (busy.value) return
  formError.value = ''
  const startSeconds = parseHighlightTime(start.value), endSeconds = parseHighlightTime(end.value)
  if (Number.isNaN(startSeconds) || Number.isNaN(endSeconds)) { formError.value = t('highlightTimeInvalid'); return }
  if (endSeconds != null && (startSeconds == null || endSeconds <= startSeconds)) { formError.value = t('highlightTimeOrder'); return }
  if (!title.value.trim() && !note.value.trim() && startSeconds == null && !retained.value.length && !added.value.length) { formError.value = t('highlightEmpty'); return }
  const metadata: SaveHighlightRequest = {
    title: title.value.trim(), note: note.value.trim(), startSeconds, endSeconds,
    sourceFileName: source.value, keepAssetIds: retained.value.map(a => a.id),
  }
  saving.value = true
  try {
    const result = await api.save(props.movieId, editingId.value, metadata, added.value.map(a => a.file))
    const hydrated = await display(result)
    if (disposed) return
    records.value = [...records.value.filter(r => r.id !== result.id), hydrated]
      .sort((a, b) => (a.startSeconds ?? Infinity) - (b.startSeconds ?? Infinity) || a.id - b.id)
    resetEditor()
    toast(t('highlightSaved'), 'success')
  } catch (e: any) { if (!disposed) formError.value = e.message }
  finally { saving.value = false }
}
async function remove(record: DisplayRecord) {
  if (busy.value || !await beforeLeave()) return
  if (!await confirmDialog(t('highlightDeleteConfirm', { title: label(record) }), t('highlightDelete'))) return
  deletingId.value = record.id
  try {
    await api.remove(props.movieId, record.id)
    if (!disposed) { records.value = records.value.filter(r => r.id !== record.id); toast(t('highlightDeleted'), 'success') }
  } catch (e: any) { if (!disposed) toast(e.message, 'error') }
  finally { deletingId.value = null }
}
async function copyTime(record: MovieHighlight) {
  try { await navigator.clipboard.writeText(range(record)); toast(t('copied'), 'success') }
  catch { toast(t('highlightCopyFailed'), 'error') }
}

function openPreview(record: DisplayRecord, asset: DisplayAsset) {
  if (asset.kind === 'video') emit('preview')
  returnFocus = document.activeElement as HTMLElement
  const assets = asset.kind === 'image' ? record.assets.filter(a => a.kind === 'image') : [asset]
  preview.value = { record, assets, index: assets.findIndex(a => a.id === asset.id) }
  videoFailed.value = false
  nextTick(() => previewButton.value?.focus())
}
function closePreview() {
  previewVideo.value?.pause()
  preview.value = null
  const target = returnFocus
  returnFocus = null
  nextTick(() => { if (target?.isConnected) target.focus() })
}
function changeImage(delta: number) {
  if (preview.value) preview.value.index = (preview.value.index + delta + preview.value.assets.length) % preview.value.assets.length
}
function previewKey(event: KeyboardEvent) {
  if (event.key === 'Tab') {
    const controls = Array.from((event.currentTarget as HTMLElement).querySelectorAll<HTMLElement>('button:not(:disabled), video[controls]'))
    const first = controls[0], last = controls[controls.length - 1]
    if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last?.focus() }
    else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus() }
  }
  if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); closePreview() }
  if (previewAsset.value?.kind === 'image' && ['ArrowLeft', 'ArrowRight'].includes(event.key)) {
    event.preventDefault(); event.stopPropagation(); changeImage(event.key === 'ArrowLeft' ? -1 : 1)
  }
}
const backdrop = useBackdropClose(closePreview)
async function playExternal() {
  if (!preview.value || !previewAsset.value) return
  try {
    const result = await api.playAsset(props.movieId, preview.value.record.id, previewAsset.value.id)
    toast(result.detail, result.ok ? 'success' : 'error')
  } catch (e: any) { toast(e.message, 'error') }
}
watch(previewAsset, () => { videoFailed.value = false })
onBeforeUnmount(() => { disposed = true; loadController?.abort(); previewVideo.value?.pause(); releaseAdded() })
</script>

<template>
  <section class="movie-highlights" :aria-label="t('highlights')" :aria-busy="loading || busy">
    <div class="flex items-center justify-between flex-wrap gap-3 mb-4">
      <div class="flex items-center gap-3"><h2 class="text-[18px] font-medium">{{ t('highlights') }}</h2><span v-if="!loading" class="text-muted text-xs">{{ t('totalItems', { n: records.length }) }}</span></div>
      <button class="btn-accent" :disabled="busy || loading || !!loadError" @click="edit()"><span class="i-carbon-add" />{{ t('highlightAdd') }}</button>
    </div>
    <p v-if="loading" class="py-8 text-center text-muted">{{ t('loading') }}</p>
    <div v-else-if="loadError" class="text-sm" role="alert"><p class="text-danger break-words">{{ loadError }}</p><button class="btn mt-3" @click="load">{{ t('highlightRetry') }}</button></div>
    <template v-else>
      <form v-if="editing" class="highlight-editor mb-5" :aria-label="t('highlightEditor')" @submit.prevent="save">
        <h3 class="font-medium mb-4">{{ t(editingId == null ? 'highlightAdd' : 'highlightEdit') }}</h3>
        <fieldset :disabled="busy" class="border-0 p-0 m-0 flex flex-col gap-4 min-w-0">
          <label class="text-xs flex flex-col gap-2">{{ t('highlightTitle') }}<input ref="titleInput" v-model="title" class="input" maxlength="120" :placeholder="t('highlightTitleHint')" /></label>
          <div class="highlight-fields">
            <label class="text-xs flex flex-col gap-2">{{ t('highlightStart') }}<input v-model="start" class="input" inputmode="text" placeholder="00:12:35" maxlength="9" /></label>
            <label class="text-xs flex flex-col gap-2">{{ t('highlightEnd') }}<input v-model="end" class="input" inputmode="text" placeholder="00:14:10" maxlength="9" /></label>
          </div>
          <label v-if="filesLoading || videoFiles.length > 1 || source" class="text-xs flex flex-col gap-2">{{ t('highlightSource') }}
            <select v-model="source" class="input" :disabled="filesLoading"><option value="">{{ t('highlightSourceNone') }}</option><option v-if="source && !videoFiles.includes(source)" :value="source">{{ source }}</option><option v-for="file in videoFiles" :key="file" :value="file">{{ file }}</option></select>
          </label>
          <label class="text-xs flex flex-col gap-2">{{ t('highlightNote') }}<textarea v-model="note" class="input min-h-24 resize-y" maxlength="4000" :placeholder="t('highlightNoteHint')" /></label>
          <div class="flex flex-col gap-3">
            <label class="btn self-start"> <span class="i-carbon-attachment" />{{ t('highlightAttachments') }}<input ref="fileInput" type="file" class="sr-only" accept=".jpg,.jpeg,.png,.gif,.webp,.mp4,.m4v,.mov,.webm,.mkv,.avi" multiple @change="addFiles" /></label>
            <p class="text-xs text-muted">{{ t('highlightAttachmentsHint') }}</p>
            <div v-if="retained.length || added.length" class="flex flex-wrap gap-3">
              <div v-for="asset in retained" :key="asset.id" class="highlight-file">
                <img v-if="asset.kind === 'image'" :src="asset.displayUrl" :alt="asset.originalName" />
                <span v-else class="i-carbon-video text-2xl" />
                <span class="truncate text-xs max-w-32" :title="asset.originalName">{{ asset.originalName }}</span>
                <button type="button" class="icon-btn shrink-0" :aria-label="t('highlightRemoveAttachment', { name: asset.originalName })" @click="retained = retained.filter(a => a.id !== asset.id)"><span class="i-carbon-close" /></button>
              </div>
              <div v-for="(asset, i) in added" :key="asset.url" class="highlight-file">
                <img v-if="asset.kind === 'image'" :src="asset.url" :alt="asset.file.name" /><span v-else class="i-carbon-video text-2xl" />
                <span class="truncate text-xs max-w-32" :title="asset.file.name">{{ asset.file.name }}</span>
                <button type="button" class="icon-btn shrink-0" :aria-label="t('highlightRemoveAttachment', { name: asset.file.name })" @click="removeAdded(i)"><span class="i-carbon-close" /></button>
              </div>
            </div>
          </div>
          <p v-if="formError" class="text-danger text-sm break-words" role="alert">{{ formError }}</p>
          <div class="flex justify-end gap-2"><button type="button" class="btn" @click="beforeLeave">{{ t('cancel') }}</button><button type="submit" class="btn-accent"><span :class="saving ? 'i-carbon-renew animate-spin' : 'i-carbon-checkmark'" />{{ t(saving ? 'highlightSaving' : 'save') }}</button></div>
        </fieldset>
      </form>
      <p v-if="!records.length && !editing" class="text-sm text-muted text-center py-10">{{ t('highlightEmptyHint') }}</p>
      <div class="highlight-grid">
        <article v-for="record in records" :key="record.id" class="highlight-card">
          <div v-if="record.assets.length" class="highlight-gallery">
            <button v-for="asset in record.assets" :key="asset.id" class="highlight-media" :aria-label="t(asset.kind === 'image' ? 'highlightViewImage' : 'highlightPlayClip', { name: asset.originalName })" @click="openPreview(record, asset)">
              <img v-if="asset.kind === 'image'" :src="asset.displayUrl" :alt="asset.originalName" loading="lazy" />
              <span v-else class="flex flex-col items-center gap-2 text-primary"><span class="i-carbon-play-filled-alt text-3xl" /><span class="text-xs">{{ t('highlightClip') }}</span></span>
            </button>
          </div>
          <div class="p-4 flex flex-col gap-2">
            <button v-if="record.startSeconds != null" class="highlight-time self-start" :title="t('highlightCopyTime')" :aria-label="t('highlightCopyTime') + ': ' + range(record)" @click="copyTime(record)"><span class="i-carbon-time" />{{ range(record) }}</button>
            <h3 class="text-[15px] font-medium break-words">{{ label(record) }}</h3>
            <p v-if="record.note" class="text-sm text-text-soft whitespace-pre-wrap break-words">{{ record.note }}</p>
            <p v-if="record.sourceFileName" class="text-xs text-muted break-all">{{ record.sourceFileName }}</p>
            <div class="flex justify-end gap-1 mt-2"><button class="btn-ghost" :disabled="busy" @click="edit(record)"><span class="i-carbon-edit" />{{ t('highlightEdit') }}</button><button class="btn-ghost hover:!text-danger" :disabled="busy" @click="remove(record)"><span :class="deletingId === record.id ? 'i-carbon-renew animate-spin' : 'i-carbon-trash-can'" />{{ t('delete') }}</button></div>
          </div>
        </article>
      </div>
    </template>
    <Teleport defer to="#movie-detail-host">
      <div v-if="preview && previewAsset" class="absolute inset-0 z-[85] flex items-center justify-center p-6 bg-bg/95" @mousedown="backdrop.onMouseDown" @mouseup="backdrop.onMouseUp" @keydown="previewKey">
        <section role="dialog" aria-modal="true" :aria-label="label(preview.record)" class="w-full max-w-4xl max-h-full overflow-auto bg-surface text-text border border-border rounded-lg p-4">
          <div class="flex justify-between items-center gap-3 mb-3"><h3 class="text-sm break-words">{{ label(preview.record) }}</h3><button ref="previewButton" class="icon-btn shrink-0" :aria-label="t('cancel')" @click="closePreview"><span class="i-carbon-close" /></button></div>
          <img v-if="previewAsset.kind === 'image'" :src="previewAsset.displayUrl" :alt="previewAsset.originalName" class="w-full max-h-[65vh] object-contain" />
          <template v-else>
            <video ref="previewVideo" :key="previewAsset.id" :src="previewAsset.displayUrl" :aria-label="previewAsset.originalName" class="w-full max-h-[65vh] bg-black" controls autoplay playsinline @error="videoFailed = true" />
            <p v-if="videoFailed" class="text-sm text-muted mt-3" role="alert">{{ t('highlightVideoFallback') }}</p>
            <button class="btn mt-3" @click="playExternal"><span class="i-carbon-launch" />{{ t('highlightExternalPlay') }}</button>
          </template>
          <div v-if="previewAsset.kind === 'image' && preview.assets.length > 1" class="flex justify-center items-center gap-4 mt-3"><button class="icon-btn" :aria-label="t('highlightPreviousImage')" @click="changeImage(-1)"><span class="i-carbon-chevron-left" /></button><span class="text-xs text-muted">{{ preview.index + 1 }} / {{ preview.assets.length }}</span><button class="icon-btn" :aria-label="t('highlightNextImage')" @click="changeImage(1)"><span class="i-carbon-chevron-right" /></button></div>
        </section>
      </div>
    </Teleport>
  </section>
</template>

<style scoped>
.movie-highlights { grid-column: 1 / -1; padding-top: 24px; border-top: 1px solid var(--border); min-width: 0; color: var(--text); }
.highlight-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 16px; }
.highlight-card { background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius); overflow: hidden; min-width: 0; align-self: start; }
.highlight-gallery { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 3px; }
.highlight-gallery:has(> :only-child) { grid-template-columns: 1fr; }
.highlight-media { height: 150px; width: 100%; background: var(--surface-2); display: flex; align-items: center; justify-content: center; overflow: hidden; }
.highlight-media img { width: 100%; height: 100%; object-fit: cover; }
.highlight-time { display: inline-flex; gap: 6px; align-items: center; padding: 4px 8px; border-radius: 5px; background: var(--primary-soft); color: var(--primary); font-size: 12px; font-variant-numeric: tabular-nums; }
.highlight-editor { background: var(--surface); border: 1px solid var(--primary); border-radius: var(--radius); padding: 20px; }
.highlight-fields { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 16px; }
.highlight-file { display: flex; align-items: center; gap: 8px; min-width: 0; max-width: 100%; padding: 5px 8px; border: 1px solid var(--border); border-radius: 8px; }
.highlight-file img { width: 44px; height: 44px; object-fit: cover; border-radius: 4px; }
@media (max-width: 650px) { .highlight-grid, .highlight-fields { grid-template-columns: 1fr; } }
</style>
