import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import type { Movie } from '@/types'

// ponytail: keep details in Vue Router history so native Back/Forward work.
export function useMovieDetail() {
  const route = useRoute()
  const router = useRouter()
  const drawerId = computed(() => {
    const value = route.query.movie
    if (typeof value !== 'string' || !/^[1-9]\d*$/.test(value)) return null
    const id = Number(value)
    return Number.isSafeInteger(id) ? id : null
  })
  const drawerOpen = computed({
    get: () => drawerId.value != null,
    set: (open: boolean) => {
      if (open || drawerId.value == null) return
      const state = router.options.history.state
      if (typeof state.movieDetailOrigin === 'string' && state.back === state.movieDetailOrigin) {
        router.back()
      } else {
        const query = { ...route.query }
        delete query.movie
        router.replace({ path: route.path, query, hash: route.hash })
      }
    },
  })
  function openDetail(movie: Movie) {
    if (!movie.id || !Number.isSafeInteger(movie.id) || movie.id < 1) return
    router.push({
      path: route.path, query: { ...route.query, movie: String(movie.id) }, hash: route.hash,
      state: { movieDetailOrigin: route.fullPath },
    })
  }
  return { drawerId, drawerOpen, openDetail }
}
