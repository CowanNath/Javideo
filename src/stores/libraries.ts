import { defineStore } from 'pinia'
import { ref } from 'vue'
import { libraries as api } from '@/api/worker'
import type { Library } from '@/types'

export const useLibraryStore = defineStore('libraries', () => {
  const items = ref<Library[]>([])
  const loading = ref(false)

  async function load() {
    loading.value = true
    try {
      items.value = await api.list()
    } finally {
      loading.value = false
    }
  }

  async function create(lib: Partial<Library>) {
    const created = await api.create(lib)
    await load()
    return created
  }

  async function update(id: number, lib: Partial<Library>) {
    await api.update(id, lib)
    await load()
  }

  async function remove(id: number) {
    await api.remove(id)
    await load()
  }

  return { items, loading, load, create, update, remove }
})
