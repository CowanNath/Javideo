<script setup lang="ts">
defineProps<{ title: string; desc?: string }>()
</script>

<template>
  <section class="card !rounded-lg mb-3">
    <div class="flex items-center justify-between px-4 py-2.5 border-b border-border">
      <div>
        <div class="text-[13px] font-semibold">{{ title }}</div>
        <div v-if="desc" class="text-[11px] text-muted mt-0.5">{{ desc }}</div>
      </div>
      <slot name="actions" />
    </div>
    <div class="px-4 py-2.5">
      <slot />
    </div>
  </section>
</template>
