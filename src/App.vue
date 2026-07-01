<script setup lang="ts">
import MainLayout from '@/layouts/MainLayout.vue'
import Toast from '@/components/Toast.vue'
</script>

<template>
  <MainLayout>
    <RouterView />
  </MainLayout>
  <Toast />
</template>
