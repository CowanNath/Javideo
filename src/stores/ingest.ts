import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import { movies as moviesApi } from '@/api/worker'
import { useLibraryStore } from '@/stores/libraries'

export interface IngestTask {
  number: string
  filePath?: string
  libraryId: number
  status: 'pending' | 'running' | 'done' | 'failed'
  message?: string
}

// Ingest queue that survives page switches: the serial loop lives in this
// store (not in a component), so navigating away never breaks a batch and
// progress is still visible when the user comes back.
export const useIngestStore = defineStore('ingest', () => {
  const tasks = ref<IngestTask[]>([])
  const running = ref(false)
  const total = computed(() => tasks.value.length)
  const finished = computed(() => tasks.value.filter(t => t.status === 'done' || t.status === 'failed').length)

  function statusOf(libraryId: number, number: string) {
    return tasks.value.find(t => t.libraryId === libraryId && t.number.toLowerCase() === number.toLowerCase())
  }

  // Enqueue files (dedup: pending/running/done are skipped, failed are reset
  // for retry) and make sure the serial loop is running.
  function enqueue(libraryId: number, files: { number: string; filePath?: string }[]) {
    for (const f of files) {
      const ex = statusOf(libraryId, f.number)
      if (ex) {
        if (ex.status === 'failed') {
          ex.status = 'pending'
          ex.message = undefined
          ex.filePath = f.filePath
          ex.libraryId = libraryId
        }
        continue
      }
      tasks.value.push({ number: f.number, filePath: f.filePath, libraryId, status: 'pending' })
    }
    ensureLoop()
  }

  async function ensureLoop() {
    if (running.value) return
    running.value = true
    try {
      for (;;) {
        const task = tasks.value.find(t => t.status === 'pending')
        if (!task) break
        task.status = 'running'
        const libs = useLibraryStore()
        try {
          await moviesApi.ingestByNumber(task.libraryId, task.number, task.filePath)
          task.status = 'done'
          libs.load().catch(() => { /* sidebar counts — best effort */ })
        } catch (e: any) {
          task.status = 'failed'
          task.message = e?.message ?? String(e)
        }
      }
    } finally {
      running.value = false
    }
  }

  function clearFinished() {
    if (running.value) return
    tasks.value = tasks.value.filter(t => t.status === 'pending' || t.status === 'running')
  }

  return { tasks, running, total, finished, statusOf, enqueue, clearFinished }
})
