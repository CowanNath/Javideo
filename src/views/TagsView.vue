<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { tags } from '@/api/worker'
import type { Tag } from '@/types'
import { useRouter } from 'vue-router'
import { toast } from '@/utils/toast'
import { t as ti } from '@/utils/i18n'

const router = useRouter()
const list = ref<Tag[]>([])
const category = ref<'genre' | 'series' | 'maker' | 'custom'>('genre')
const loading = ref(false)

async function load() {
  loading.value = true
  try {
    if (category.value === 'custom') {
      const all = await tags.list()
      list.value = all.filter((t) => !t.isStandard || t.category === 'custom')
    } else {
      list.value = await tags.list({ category: category.value })
    }
  } finally {
    loading.value = false
  }
}
function switchCat(c: typeof category.value) {
  category.value = c
  load()
}
onMounted(load)

async function renameTag(t: Tag) {
  const name = prompt(ti('editTagName'), t.name)
  if (!name || name.trim() === t.name) return
  try {
    await tags.rename(t.id, name.trim())
    toast(ti('tagUpdated'), 'success')
    load()
  } catch (e: any) {
    toast(e.message, 'error')
  }
}

const cats: { key: typeof category.value; label: string; icon: string }[] = [
  { key: 'genre', label: ti('genre'), icon: 'i-carbon-category' },
  { key: 'series', label: ti('series'), icon: 'i-carbon-series' },
  { key: 'maker', label: ti('maker'), icon: 'i-carbon-building' },
  { key: 'custom', label: ti('custom'), icon: 'i-carbon-tag-edit' },
]
</script>

<template>
  <div class="p-8 max-w-5xl mx-auto">
    <div class="mb-6">
      <h1 class="text-2xl font-bold tracking-tight mb-1">{{ ti('tagTitle') }}</h1>
      <p class="text-muted text-sm">{{ ti('tagSubtitle') }}</p>
    </div>

    <!-- Category tabs -->
    <div class="flex gap-1 mb-6 p-1 rounded-lg bg-surface2 w-fit">
      <button
        v-for="c in cats"
        :key="c.key"
        class="px-3.5 py-1.5 rounded-md text-[13px] font-medium flex items-center gap-1.5 transition-all duration-150"
        :class="category === c.key ? 'bg-surface text-primary shadow-sm' : 'text-muted hover:text-text'"
        @click="switchCat(c.key)"
      >
        <span :class="c.icon" /> {{ c.label }}
      </button>
    </div>

    <div v-if="loading" class="text-muted text-sm py-12 text-center">{{ ti('loading') }}</div>
    <div
      v-else-if="!list.length"
      class="card !rounded-lg p-12 text-center text-muted text-sm"
    >
      <span class="i-carbon-tag block text-4xl mb-3 opacity-50" />
      {{ ti('noTags') }}
    </div>
    <div v-else class="flex flex-wrap gap-2">
      <span
        v-for="t in list"
        :key="t.id"
        class="chip !text-[13px] !pl-3 !pr-1.5 !py-1.5"
      >
        <span class="cursor-pointer" @click="router.push(`/tags/${t.id}`)">{{ t.name }}</span>
        <span
          class="text-[10px] px-1.5 py-0.5 rounded-full mx-0.5"
          style="background: var(--primary-soft); color: var(--primary);"
        >{{ t.movieCount ?? 0 }}</span>
        <button v-if="category === 'custom'"
          class="w-5 h-5 rounded-full inline-flex items-center justify-center text-muted hover:text-text hover:bg-surface2 align-middle"
          :title="ti('editTag')" @click.stop="renameTag(t)"
        ><span class="i-carbon-edit text-[11px]" /></button>
      </span>
    </div>
  </div>
</template>
