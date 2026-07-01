<script setup lang="ts">
import { ref, onMounted, computed } from 'vue'
import { actors } from '@/api/worker'
import type { Actor } from '@/types'
import { useRouter } from 'vue-router'
import { useFavoritesStore } from '@/stores/favorites'
import { t } from '@/utils/i18n'

const router = useRouter()
const favs = useFavoritesStore()
const list = ref<Actor[]>([])
const q = ref('')
const sortBy = ref<'count' | 'name'>('count')
const loading = ref(false)

async function load() {
  loading.value = true
  try {
    list.value = await actors.list(q.value || undefined)
    await favs.load('actor')
  } finally {
    loading.value = false
  }
}
onMounted(load)

const sortedList = computed(() => {
  if (sortBy.value === 'name') {
    return [...list.value].sort((a, b) => a.name.localeCompare(b.name))
  }
  return [...list.value].sort((a, b) => (b.movieCount ?? 0) - (a.movieCount ?? 0))
})
</script>

<template>
  <div class="p-8 max-w-6xl mx-auto">
    <div class="mb-6">
      <h1 class="text-2xl font-bold tracking-tight mb-1">{{ t('actors') }}</h1>
      <p class="text-muted text-sm">{{ t('actorsSubtitle') }}</p>
    </div>

    <div class="flex gap-2 mb-6">
      <div class="relative flex-1 max-w-md">
        <span class="i-carbon-search absolute left-3 top-1/2 -translate-y-1/2 text-muted text-sm" />
        <input v-model="q" class="input !pl-9" :placeholder="t('searchActor')" @keyup.enter="load" />
      </div>
      <button class="btn-primary" @click="load"><span class="i-carbon-search" /> {{ t('searchBtn') }}</button>
      <select v-model="sortBy" class="input !w-auto">
        <option value="count">{{ t('sortByCount') }}</option>
        <option value="name">{{ t('sortByName') }}</option>
      </select>
    </div>

    <div v-if="loading" class="text-muted text-sm py-12 text-center">{{ t('loading') }}</div>
    <div v-else-if="!list.length" class="card !rounded-lg p-12 text-center text-muted text-sm">
      <span class="i-carbon-user block text-4xl mb-3 opacity-50" />
      {{ t('noActors') }}
    </div>
    <div v-else class="grid gap-4" style="grid-template-columns: repeat(auto-fill, minmax(140px, 1fr));">
      <div v-for="a in sortedList" :key="a.id" class="card cursor-pointer text-center p-4 group relative" @click="router.push(`/actors/${a.id}`)">
        <!-- favorite heart top-right -->
        <button
          class="absolute top-2 right-2 w-7 h-7 rounded-full flex items-center justify-center transition-all hover:scale-110 z-10"
          :class="favs.actorIds.includes(a.id) ? '!text-red-500' : 'text-muted opacity-0 group-hover:opacity-100'"
          style="background: var(--surface-3);"
          :title="t('favorites')"
          :aria-label="t('favorites')"
          @click.stop="favs.toggle('actor', a.id)"
        >
          <span :class="favs.actorIds.includes(a.id) ? 'i-carbon-favorite-filled' : 'i-carbon-favorite'" />
        </button>

        <div class="w-24 h-24 mx-auto mb-3 rounded-full bg-surface2 overflow-hidden ring-2 ring-border group-hover:ring-primary transition">
          <img v-if="a.avatarUrl" :src="a.avatarUrl" class="w-full h-full object-cover" referrerpolicy="no-referrer" />
          <div v-else class="w-full h-full flex items-center justify-center text-muted">
            <span class="i-carbon-user-filled text-4xl" />
          </div>
        </div>
        <div class="text-[13px] font-medium truncate">{{ a.name }}</div>
        <div class="text-[11px] text-muted mt-0.5">{{ a.movieCount ?? 0 }} {{ t('videos') }}</div>
      </div>
    </div>
  </div>
</template>
