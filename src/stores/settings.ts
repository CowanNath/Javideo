import { defineStore } from 'pinia'
import { ref } from 'vue'
import { settings as api } from '@/api/worker'

export const useSettingsStore = defineStore('settings', () => {
  const values = ref<Record<string, string>>({})
  const loaded = ref(false)

  async function load() {
    values.value = await api.all()
    loaded.value = true
  }

  async function set(key: string, value: string) {
    await api.set(key, value)
    values.value[key] = value
  }

  function get(key: string, fallback = '') {
    return values.value[key] ?? fallback
  }

  return { values, loaded, load, set, get }
})
