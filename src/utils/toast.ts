import { ref } from 'vue'

// Lightweight global toast — a single transient message shown at top-right.
export const toastMsg = ref('')
export const toastType = ref<'info' | 'success' | 'error'>('info')
let timer: number | undefined

export function toast(msg: string, type: 'info' | 'success' | 'error' = 'info') {
  toastMsg.value = msg
  toastType.value = type
  clearTimeout(timer)
  timer = window.setTimeout(() => { toastMsg.value = '' }, 2500)
}
