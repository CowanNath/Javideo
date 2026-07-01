import { defineStore } from 'pinia'
import { ref } from 'vue'
import type { Movie, MagnetSourceResult } from '@/types'

// Holds the last search so switching pages and coming back keeps the results
// (instead of clearing them on route change).
export const useSearchStore = defineStore('search', () => {
  const query = ref('')
  const movie = ref<Movie | null>(null)
  const grouped = ref<MagnetSourceResult[]>([])
  const lastSearchAt = ref(0)
  const trailerUrl = ref('')

  function clear() {
    query.value = ''
    movie.value = null
    grouped.value = []
    lastSearchAt.value = 0
    trailerUrl.value = ''
  }

  return { query, movie, grouped, lastSearchAt, trailerUrl, clear }
})
