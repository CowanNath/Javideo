<script setup lang="ts">
import { ref, watch, computed } from 'vue'
import type { Library } from '@/types'
import { libraries as api } from '@/api/worker'
import { useBackdropClose } from '@/utils/clickOutside'
import { pickFolder } from '@/utils/pickFolder'
import { t } from '@/utils/i18n'

const props = defineProps<{ modelValue: boolean; library?: Library | null }>()
const emit = defineEmits<{
  'update:modelValue': [v: boolean]
  save: [lib: { name: string; metadataSource: string; directories: string[] }]
}>()

const name = ref('')
const metadataSource = ref('metatube')
const directories = ref<string[]>([''])

// Validation state.
const nameTaken = ref(false)        // #2: duplicate name
const dirExists = ref<Record<number, boolean>>({})  // #3: per-dir existence
const dirChecking = ref<Record<number, boolean>>({})
const errorMsg = ref('')

watch(
  () => props.modelValue,
  (open) => {
    if (open) {
      name.value = props.library?.name ?? ''
      metadataSource.value = props.library?.metadataSource ?? 'metatube'
      directories.value = props.library?.directories?.length
        ? [...props.library.directories]
        : ['']
      nameTaken.value = false
      dirExists.value = {}
      dirChecking.value = {}
      errorMsg.value = ''
      // Validate existing dirs on open.
      directories.value.forEach((_, i) => checkDir(i))
    }
  },
)

const backdrop = useBackdropClose(() => close())

function close() { emit('update:modelValue', false) }

// #2: check name uniqueness (debounced) — excludes the library being edited.
let nameTimer: number | undefined
watch(name, (v) => {
  clearTimeout(nameTimer)
  if (!v.trim()) { nameTaken.value = false; return }
  nameTimer = window.setTimeout(async () => {
    try {
      const r = await api.checkName(v, props.library?.id)
      nameTaken.value = r.taken
    } catch { nameTaken.value = false }
  }, 300)
})

// #3: check a directory's existence in real time.
let dirTimers: Record<number, number> = {}
function checkDir(i: number) {
  clearTimeout(dirTimers[i])
  const path = directories.value[i]?.trim()
  if (!path) { delete dirExists.value[i]; return }
  dirChecking.value[i] = true
  dirTimers[i] = window.setTimeout(async () => {
    try {
      const r = await api.checkDir(path)
      dirExists.value[i] = r.exists
    } catch { delete dirExists.value[i] }
    finally { dirChecking.value[i] = false }
  }, 300)
}
function onDirInput(i: number) { checkDir(i) }

// #1: native folder picker.
async function browse(i: number) {
  const p = await pickFolder()
  if (p) {
    directories.value[i] = p
    checkDir(i)
  }
}

function removeDir(i: number) {
  directories.value.splice(i, 1)
  delete dirExists.value[i]
  if (!directories.value.length) directories.value.push('')
}

// Any directory that doesn't exist (only those filled in).
const hasMissingDir = computed(() =>
  directories.value.some((d, i) => d.trim() && dirExists.value[i] === false)
)

function save() {
  errorMsg.value = ''
  if (!name.value.trim()) { errorMsg.value = t('enterLibName'); return }
  if (nameTaken.value) { errorMsg.value = t('nameTaken'); return }
  const dirs = directories.value.map((d) => d.trim()).filter(Boolean)
  if (hasMissingDir.value) {
    errorMsg.value = t('fixDirs')
    return
  }
  emit('save', { name: name.value.trim(), metadataSource: metadataSource.value, directories: dirs })
  close()
}
</script>

<template>
  <Teleport to="body">
    <div
      v-if="modelValue"
      class="fixed inset-0 z-50 flex items-center justify-center"
      style="background: rgba(0,0,0,0.5); backdrop-filter: blur(3px);"
      @mousedown="backdrop.onMouseDown"
      @mouseup="backdrop.onMouseUp"
    >
      <div class="card !rounded-lg w-[520px] max-w-[90vw] shadow-lg">
        <div class="px-5 py-4 border-b border-border">
          <div class="text-[15px] font-semibold">{{ library ? t('editLibTitle') : t('newLibTitle') }}</div>
        </div>
        <div class="p-5 flex flex-col gap-4">
          <!-- Name (#2: uniqueness) -->
          <label class="flex flex-col gap-1.5">
            <span class="text-[13px] text-text-soft font-medium">{{ t('libName') }}</span>
            <input
              v-model="name"
              class="input"
              :class="nameTaken && '!border-red-500'"
              :placeholder="t('libNamePlaceholder')"
            />
            <span v-if="nameTaken" class="text-[11px] text-red-400">{{ t('nameTaken') }}</span>
          </label>

          <label class="flex flex-col gap-1.5">
            <span class="text-[13px] text-text-soft font-medium">{{ t('metaSource') }}</span>
            <select v-model="metadataSource" class="input">
              <option value="metatube">{{ t('useMetatube') }}</option>
            </select>
          </label>

          <!-- Directories (#1: picker, #3: existence check) -->
          <div class="flex flex-col gap-1.5">
            <span class="text-[13px] text-text-soft font-medium">{{ t('dirs') }}</span>
            <div v-for="(_, i) in directories" :key="i" class="flex gap-2 items-start">
              <div class="flex-1 flex flex-col gap-1">
                <div class="flex gap-1.5">
                  <input
                    v-model="directories[i]"
                    class="input"
                    :class="dirExists[i] === false && directories[i].trim() && '!border-red-500'"
                    :placeholder="t('dirPlaceholder')"
                    @input="onDirInput(i)"
                  />
                  <!-- #1: folder picker button (before the X) -->
                  <button class="btn !px-2.5 shrink-0" :title="t('browseFolder')" @click="browse(i)">
                    ...
                  </button>
                  <button class="btn-ghost !px-2.5 shrink-0" :title="t('remove')" @click="removeDir(i)">
                    <span class="i-carbon-close" />
                  </button>
                </div>
                <span
                  v-if="directories[i].trim() && dirExists[i] === false"
                  class="text-[11px] text-red-400"
                >{{ t('dirNotExist') }}</span>
                <span
                  v-else-if="directories[i].trim() && dirExists[i] === true"
                  class="text-[11px] text-status-green flex items-center gap-1"
                ><span class="i-carbon-checkmark" />{{ t('dirValid') }}</span>
              </div>
            </div>
            <p class="text-[11px] text-muted mt-1 leading-relaxed">
              {{ t('dirHint') }}
            </p>

          </div>

          <p v-if="errorMsg" class="text-[12px] text-red-400">{{ errorMsg }}</p>
        </div>
        <div class="px-5 py-4 border-t border-border flex justify-end gap-2">
          <button class="btn" @click="close">{{ t('cancel') }}</button>
          <button class="btn-primary" @click="save">{{ t('saveLib') }}</button>
        </div>
      </div>
    </div>
  </Teleport>
</template>
