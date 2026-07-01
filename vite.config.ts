import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import UnoCSS from 'unocss/vite'
import { fileURLToPath, URL } from 'node:url'

// During `vite dev` (no Tauri), the worker runs separately on a fixed port.
// Set VITE_DEV_WORKER_PORT to match, or default to 1375.
const devWorkerBase =
  `http://127.0.0.1:${process.env.VITE_DEV_WORKER_PORT || '1375'}`

export default defineConfig({
  plugins: [vue(), UnoCSS()],
  clearScreen: false,
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  server: {
    port: 1420,
    strictPort: true,
  },
  define: {
    __DEV_WORKER_BASE__: JSON.stringify(devWorkerBase),
  },
  build: {
    target: 'esnext',
    outDir: 'dist',
  },
  // Relative base so assets resolve correctly under Tauri's custom protocol
  // (https://tauri.localhost) when loading the bundled dist folder.
  base: './',
})
