import { createApp } from 'vue'
import { createPinia } from 'pinia'
import App from './App.vue'
import { router } from './router'
import { initTheme } from './utils/theme'
import { initLang } from './utils/i18n'

// Apply theme + language before mount.
initTheme()
initLang()
import './styles.css'
import 'virtual:uno.css'

// Apply theme before mount to avoid a flash of the wrong colors.
initTheme()

// Surface any error that happens during/after mount onto the page, so a blank
// screen always shows *something* diagnostic instead of nothing.
function showError(msg: string) {
  const box = document.createElement('pre')
  box.style.cssText =
    'position:fixed;top:10px;left:10px;right:10px;z-index:99999;' +
    'background:#2a1518;color:#ff9b9b;padding:12px;border-radius:6px;' +
    'font-size:13px;white-space:pre-wrap;word-break:break-all;'
  box.textContent = '[启动错误] ' + msg
  document.body.appendChild(box)
  // eslint-disable-next-line no-console
  console.error('[javideo]', msg)
}

window.addEventListener('error', (e) => showError(e.message + (e.error ? '\n' + e.error.stack : '')))
window.addEventListener('unhandledrejection', (e) => showError('Promise 未捕获: ' + e.reason))

try {
  const app = createApp(App)
  app.use(createPinia())
  app.use(router)
  app.mount('#app')
} catch (e: any) {
  showError('挂载失败: ' + (e?.message || String(e)) + '\n' + (e?.stack || ''))
}
