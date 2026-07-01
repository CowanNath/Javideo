<script setup lang="ts">
const props = defineProps<{ page: number; totalPages: number }>()
const emit = defineEmits<{ update: [page: number] }>()

function go(p: number) {
  if (p < 1 || p > props.totalPages || p === props.page) return
  emit('update', p)
}
</script>

<template>
  <div
    v-if="totalPages > 0"
    class="flex items-center gap-1 p-1 rounded-lg bg-surface2 border border-border shadow-md"
  >
    <button
      class="w-8 h-8 rounded-md flex items-center justify-center text-text-soft hover:bg-surface3 transition-colors disabled:opacity-30 disabled:hover:bg-transparent"
      :disabled="page <= 1"
      @click="go(page - 1)"
    >
      <span class="i-carbon-chevron-left" />
    </button>
    <span class="px-3 text-[13px] font-medium text-text min-w-[60px] text-center">
      {{ page }} / {{ totalPages }}
    </span>
    <button
      class="w-8 h-8 rounded-md flex items-center justify-center text-text-soft hover:bg-surface3 transition-colors disabled:opacity-30 disabled:hover:bg-transparent"
      :disabled="page >= totalPages"
      @click="go(page + 1)"
    >
      <span class="i-carbon-chevron-right" />
    </button>
  </div>
</template>
