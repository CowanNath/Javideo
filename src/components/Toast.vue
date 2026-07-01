<script setup lang="ts">
import { toastMsg, toastType } from '@/utils/toast'
</script>

<template>
  <Teleport to="body">
    <Transition name="toast">
      <div
        v-if="toastMsg"
        class="fixed top-6 right-6 z-[100] px-4 py-3 rounded-lg shadow-lg text-[14px] font-medium flex items-center gap-2"
        :style="{
          background: toastType === 'error' ? '#dc2626' : toastType === 'success' ? '#16a34a' : 'var(--primary)',
          color: '#fff',
        }"
      >
        <span :class="toastType === 'error' ? 'i-carbon-warning' : toastType === 'success' ? 'i-carbon-checkmark-filled' : 'i-carbon-information'" />
        {{ toastMsg }}
      </div>
    </Transition>
  </Teleport>
</template>

<style scoped>
.toast-enter-active, .toast-leave-active { transition: all 0.3s ease; }
.toast-enter-from { opacity: 0; transform: translateX(40px); }
.toast-leave-to { opacity: 0; transform: translateX(40px); }
</style>
