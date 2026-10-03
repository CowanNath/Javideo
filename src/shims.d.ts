declare module '*.vue' {
  import type { DefineComponent } from 'vue'
  const component: DefineComponent<{}, {}, any>
  export default component
}
declare const __DEV_WORKER_BASE__: string
declare const __APP_VERSION__: string
