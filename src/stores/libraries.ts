import { defineStore } from 'pinia'
import { ref } from 'vue'
import { libraries as api } from '@/api/worker'
import type { Library } from '@/types'

export const useLibraryStore = defineStore('libraries', () => {
  const items = ref<Library[]>([])
  const loading = ref(false)
  let poll: ReturnType<typeof setTimeout> | undefined
  let pending: Promise<void> | undefined

  function load(fresh = false): Promise<void> {
    if (pending) return fresh ? pending.then(() => load()) : pending
    clearTimeout(poll)
    loading.value = true
    pending = api.list().then(result => { items.value = result }).finally(() => {
      loading.value = false
      pending = undefined
      if (items.value.some(lib => ['queued', 'scanning', 'caching'].includes(lib.cacheStatus?.state ?? '')))
        poll = setTimeout(() => { void load().catch(() => {}) }, 2000)
    })
    return pending
  }

  async function create(lib: Partial<Library>) {
    const created = await api.create(lib)
    await load(true)
    return created
  }

  async function update(id: number, lib: Partial<Library>) {
    await api.update(id, lib)
    await load(true)
  }

  async function remove(id: number) {
    await api.remove(id)
    await load(true)
  }

  async function retryCache(id: number) {
    await api.retryCache(id)
    await load(true)
  }

  return { items, loading, load, create, update, remove, retryCache }
})
