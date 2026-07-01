import { defineStore } from 'pinia'
import { ref } from 'vue'
import { favorites as api } from '@/api/worker'
import type { FavoriteTarget } from '@/types'

// Holds the favorited target IDs per type as reactive arrays. Reads of
// `.includes(id)` inside templates/computeds ARE tracked, so the heart flips
// the instant a toggle mutates the array.
export const useFavoritesStore = defineStore('favorites', () => {
  const movieIds = ref<number[]>([])
  const tagIds = ref<number[]>([])
  const actorIds = ref<number[]>([])
  // Use a plain object of loading promises to de-duplicate concurrent loads
  // (NOT a reactive Set — Set mutation isn't tracked by Vue).
  const loading: Partial<Record<FavoriteTarget, Promise<void>>> = {}

  function list(type: FavoriteTarget): number[] {
    return type === 'movie' ? movieIds.value : type === 'tag' ? tagIds.value : actorIds.value
  }
  function setList(type: FavoriteTarget, ids: number[]) {
    if (type === 'movie') movieIds.value = ids
    else if (type === 'tag') tagIds.value = ids
    else actorIds.value = ids
  }

  // Force a fresh fetch (used by views on mount).
  async function load(type: FavoriteTarget) {
    try { setList(type, await api.ids(type)) } catch { /* ignore */ }
  }

  // Idempotent: the first caller triggers the fetch, concurrent callers share
  // the same promise, later callers no-op (data already present).
  async function ensureLoaded(type: FavoriteTarget) {
    if (loading[type]) return loading[type]
    const p = load(type).finally(() => { delete loading[type] })
    loading[type] = p
    return p
  }

  function isFav(type: FavoriteTarget, id: number): boolean {
    return list(type).includes(id)
  }

  async function toggle(type: FavoriteTarget, id: number): Promise<boolean> {
    const fav = list(type).includes(id)
    const willAdd = !fav
    // Optimistic update (reassigns the array ref → reactive, heart flips now).
    setList(type, willAdd ? [...list(type), id] : list(type).filter((x) => x !== id))
    try {
      if (willAdd) await api.add(type, id)
      else await api.remove(type, id)
      return willAdd
    } catch {
      // Roll back on failure.
      setList(type, fav ? [...list(type), id] : list(type).filter((x) => x !== id))
      return fav
    }
  }

  return { movieIds, tagIds, actorIds, load, ensureLoaded, isFav, toggle }
})
