<script setup lang="ts">
import { ref, computed } from 'vue'
import type { MagnetSourceResult, MagnetResult } from '@/types'

const props = defineProps<{
  grouped: MagnetSourceResult[]   // per-source results
  loading?: boolean
}>()

// Active source tab.
const activeSource = ref('')
const sources = computed(() => props.grouped.map((g) => g.source))
// Pick the first available source by default.
function ensureTab() {
  if (!activeSource.value && sources.value.length) activeSource.value = sources.value[0]
  if (activeSource.value && !sources.value.includes(activeSource.value)) {
    activeSource.value = sources.value[0] ?? ''
  }
}
const current = computed<MagnetSourceResult | undefined>(() => {
  ensureTab()
  return props.grouped.find((g) => g.source === activeSource.value)
})

// Total across sources (for the header count).
const totalCount = computed(() => props.grouped.reduce((n, g) => n + g.count, 0))

// Copy feedback.
const copied = ref<string | null>(null)
let copyTimer: number | undefined
async function copy(m: MagnetResult) {
  try {
    await navigator.clipboard.writeText(m.magnetUri)
    copied.value = m.magnetUri
    clearTimeout(copyTimer)
    copyTimer = window.setTimeout(() => (copied.value = null), 1500)
  } catch {
    /* clipboard may be unavailable */
  }
}
</script>

<template>
  <div>
    <!-- Loading skeleton -->
    <template v-if="loading">
      <div v-for="i in 3" :key="i" class="card !rounded-md p-4 flex gap-3 animate-pulse mb-2">
        <div class="w-6 h-6 rounded bg-surface3 shrink-0" />
        <div class="flex-1 space-y-2">
          <div class="h-3.5 bg-surface3 rounded w-3/4" />
          <div class="h-3 bg-surface3 rounded w-1/4" />
        </div>
      </div>
    </template>

    <template v-else-if="!grouped.length">
      <div class="card !rounded-md p-8 text-center text-muted text-sm">
        <span class="i-carbon-search block text-3xl mb-2 opacity-50" />
        未找到磁力链接
      </div>
    </template>

    <template v-else>
      <!-- Source tabs -->
      <div class="flex items-center gap-1 mb-3 p-1 rounded-lg bg-surface2 border border-border w-fit">
        <button
          v-for="g in grouped"
          :key="g.source"
          class="px-3 py-1.5 rounded-md text-[12px] font-medium flex items-center gap-1.5 transition-all duration-150"
          :class="activeSource === g.source ? 'bg-surface text-primary shadow-sm' : 'text-muted hover:text-text'"
          @click="activeSource = g.source"
        >
          {{ g.source }}
          <span
            class="text-[10px] px-1.5 py-0.5 rounded-full"
            :class="activeSource === g.source ? 'bg-primary-soft text-primary' : 'bg-surface3 text-muted'"
          >{{ g.count }}</span>
        </button>
        <span class="px-2 text-[11px] text-muted">共 {{ totalCount }} 条</span>
      </div>

      <!-- Results for the active source -->
      <div v-if="current && current.results.length" class="flex flex-col gap-2">
        <div
          v-for="(m, i) in current.results"
          :key="m.magnetUri"
          class="card !rounded-md p-3.5 flex items-start gap-3"
        >
          <div
            class="w-7 h-7 rounded-md flex items-center justify-center text-[11px] font-semibold shrink-0"
            style="background: var(--primary-soft); color: var(--primary);"
          >{{ i + 1 }}</div>
          <div class="flex-1 min-w-0">
            <div class="text-[13px] font-medium truncate flex items-center gap-2" :title="m.title">
              <span class="truncate">{{ m.title }}</span>
              <span v-if="m.size" class="text-[11px] text-muted shrink-0">{{ m.size }}</span>
            </div>
            <!-- Show the magnet link itself -->
            <div class="text-[11px] text-muted font-mono mt-1 truncate" :title="m.magnetUri">{{ m.magnetUri }}</div>
          </div>
          <button
            class="btn-ghost !text-primary shrink-0"
            :title="copied === m.magnetUri ? '已复制!' : '复制磁力链接'"
            @click="copy(m)"
          >
            <span :class="copied === m.magnetUri ? 'i-carbon-checkmark-filled' : 'i-carbon-copy'" />
            {{ copied === m.magnetUri ? '已复制' : '复制' }}
          </button>
        </div>
      </div>
      <div v-else class="card !rounded-md p-8 text-center text-muted text-sm">
        该搜索源暂无结果
      </div>
    </template>
  </div>
</template>
