<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { metatube } from '@/api/worker'
import { useSettingsStore } from '@/stores/settings'
import { useLibraryStore } from '@/stores/libraries'
import LibraryEditDialog from '@/components/LibraryEditDialog.vue'
import SettingSection from '@/components/SettingSection.vue'
import { applyTheme, getStoredTheme, type Theme } from '@/utils/theme'
import { confirmDialog } from '@/utils/confirm'
import { toast } from '@/utils/toast'
import { t, setLang, type Lang } from '@/utils/i18n'
import { backup } from '@/api/worker'

const importInput = ref<HTMLInputElement | null>(null)
const importMsg = ref('')
const importOk = ref(false)

async function doExport() {
  try {
    // Use Tauri's save dialog to let the user pick where to save.
    const { save } = await import('@tauri-apps/plugin-dialog')
    const defaultName = `javideo-backup-${new Date().toISOString().slice(0, 10)}.zip`
    const path = await save({
      defaultPath: defaultName,
      filters: [{ name: 'ZIP', extensions: ['zip'] }],
    })
    if (!path) return // user cancelled
    toast(t('exporting'), 'info')
    const res = await backup.exportTo(path)
    if (res.ok) toast(t('exportDone') + ': ' + path, 'success')
    else toast(t('exportFail') + ': ' + (res.detail ?? ''), 'error')
  } catch (e: any) {
    // Fallback: not in Tauri (browser dev) — use the old download approach.
    toast(t('exportFail') + ': ' + e.message, 'error')
  }
}

function triggerImport() {
  importInput.value?.click()
}

async function doImport(e: Event) {
  const target = e.target as HTMLInputElement
  const file = target.files?.[0]
  if (!file) return
  if (!await confirmDialog(t('importConfirm'), t('importData'))) { target.value = ''; return }
  importMsg.value = t('importing')
  try {
    const res = await backup.import(file)
    importMsg.value = res.detail
    importOk.value = res.ok
    if (res.ok) toast(res.detail, 'success')
  } catch (ex: any) {
    importMsg.value = t('importFail') + ': ' + ex.message
    importOk.value = false
  } finally {
    target.value = ''
  }
}

const s = useSettingsStore()
const libs = useLibraryStore()

const dialogOpen = ref(false)
const editingLib = ref<any>(null)
const testResult = ref('')
const testing = ref(false)

const mtAddr = ref(''), mtTimeout = ref(15000), playerPath = ref(''), theme = ref<Theme>('dark'), lang = ref<Lang>('zh'), scrapeTrailer = ref(false), proxyAddr = ref(''), proxyUser = ref(''), proxyPass = ref(''), debugMode = ref(false), closeBehavior = ref<'quit' | 'tray'>('quit'), defaultSort = ref<'date' | 'name'>('date')

onMounted(async () => {
  await s.load()
  mtAddr.value = s.get('metatube.address', '')
  mtTimeout.value = Number(s.get('metatube.timeoutMs', '15000'))
  playerPath.value = s.get('player.path', '')
  theme.value = (getStoredTheme()) as Theme
  lang.value = (s.get('ui.language', 'zh') as Lang) || 'zh'
  scrapeTrailer.value = s.get('ui.scrapeTrailer', 'false') === 'true'
  proxyAddr.value = s.get('network.proxy', '')
  proxyUser.value = s.get('network.proxyUser', '')
  proxyPass.value = s.get('network.proxyPass', '')
  debugMode.value = s.get('ui.debug', 'false') === 'true'
  closeBehavior.value = (s.get('ui.closeBehavior', 'quit') as 'quit' | 'tray') || 'quit'
  defaultSort.value = (s.get('ui.defaultSort', 'date') as 'date' | 'name') || 'date'
  // Sync close behavior to Rust.
  try { const { invoke } = await import('@tauri-apps/api/core'); await invoke('set_close_behavior', { behavior: closeBehavior.value }) } catch {}
})

function flash(msg: string) { toast(msg, 'success') }
async function saveMetatube() {
  await s.set('metatube.address', mtAddr.value)
  await s.set('metatube.timeoutMs', String(mtTimeout.value))
  flash(t('metatubeSaved'))
}
async function savePlayer() { await s.set('player.path', playerPath.value); flash(t('playerSaved')) }
async function changeTheme(th: Theme) {
  theme.value = th
  applyTheme(th)
  await s.set('ui.theme', th)
  flash(t('saved'))
}
async function changeLanguage(l: Lang) {
  lang.value = l
  setLang(l)
  await s.set('ui.language', l)
  flash(t('saved'))
  // Immediate reload so ALL pages re-render in the new language.
  location.reload()
}
async function toggleScrapeTrailer(on: boolean) {
  scrapeTrailer.value = on
  await s.set('ui.scrapeTrailer', on ? 'true' : 'false')
  flash(on ? t('trailerOn') : t('trailerOff'))
}
async function saveProxy() {
  await s.set('network.proxy', proxyAddr.value.trim())
  await s.set('network.proxyUser', proxyUser.value.trim())
  await s.set('network.proxyPass', proxyPass.value)
  flash(t('proxySaved'))
}
async function toggleDebug(on: boolean) {
  debugMode.value = on
  await s.set('ui.debug', on ? 'true' : 'false')
  if (on) { try { const { invoke } = await import('@tauri-apps/api/core'); await invoke('open_devtools') } catch {} }
  flash(on ? t('debugOn') : t('debugOff'))
}
async function changeCloseBehavior(v: 'quit' | 'tray') {
  closeBehavior.value = v
  await s.set('ui.closeBehavior', v)
  try { const { invoke } = await import('@tauri-apps/api/core'); await invoke('set_close_behavior', { behavior: v }) } catch {}
  flash(t('saved'))
}
async function changeDefaultSort(v: 'date' | 'name') {
  defaultSort.value = v
  await s.set('ui.defaultSort', v)
  flash(t('saved'))
}
async function testConn() {
  testing.value = true
  try {
    await saveMetatube()
    const r = await metatube.test()
    testResult.value = `${r.ok ? '✅' : '❌'} ${r.detail}`
  } finally { testing.value = false }
}

function openCreate() { editingLib.value = null; dialogOpen.value = true }
function openEdit(lib: any) { editingLib.value = lib; dialogOpen.value = true }
async function onSave(lib: any) {
  if (editingLib.value) await libs.update(editingLib.value.id, lib)
  else await libs.create(lib)
}
async function onDelete(id: number) {
  if (await confirmDialog(t('deleteLibConfirm'), t('deleteLib'))) await libs.remove(id)
}
</script>

<template>
  <div class="p-8 max-w-3xl mx-auto">
    <div class="mb-5">
      <h1 class="text-2xl font-bold tracking-tight">{{ t('settings') }}</h1>
      <p class="text-muted text-sm mt-0.5">{{ t('settingsSubtitle') }}</p>
    </div>

    <!-- 媒体库 -->
    <SettingSection :title="t('libraries')" :desc="t('newLib')">
      <template #actions>
        <button class="btn-primary" @click="openCreate"><span class="i-carbon-add" /> {{ t('newLib') }}</button>
      </template>
      <div v-for="lib in libs.items" :key="lib.id" class="flex items-center justify-between py-2.5 border-b border-border last:border-0 last:pb-0 first:pt-0">
        <div class="flex items-center gap-3 min-w-0">
          <div class="w-9 h-9 rounded-md flex items-center justify-center shrink-0" style="background: var(--primary-soft); color: var(--primary);">
            <span class="i-carbon-folder" />
          </div>
          <div class="min-w-0">
            <div class="font-medium text-[14px] truncate">{{ lib.name }}</div>
            <div class="text-[11px] text-muted truncate">{{ lib.movieCount ?? 0 }} · {{ lib.directories.join('; ') }}</div>
          </div>
        </div>
        <div class="flex gap-1 shrink-0">
          <button class="btn-ghost !px-2" @click="openEdit(lib)"><span class="i-carbon-edit" /></button>
          <button class="btn-ghost !px-2 hover:!text-red-500" @click="onDelete(lib.id)"><span class="i-carbon-trash-can" /></button>
        </div>
      </div>
      <p v-if="!libs.items.length" class="text-[13px] text-muted py-2">{{ t('noLibsHint') }}</p>
    </SettingSection>

    <!-- MetaTube -->
    <SettingSection :title="t('metatube')" :desc="t('metatubeDesc')">
      <label class="flex flex-col gap-1.5 mb-3">
        <span class="text-[13px] text-text-soft font-medium">{{ t('serverAddr') }}</span>
        <input v-model="mtAddr" class="input" placeholder="http://127.0.0.1:8080" />
      </label>
      <label class="flex flex-col gap-1.5 mb-3">
        <span class="text-[13px] text-text-soft font-medium">{{ t('timeoutMs') }}</span>
        <input v-model.number="mtTimeout" type="number" class="input" />
      </label>
      <div class="flex gap-2 items-center justify-end flex-wrap">
        <span v-if="testResult" class="text-[13px] mr-auto">{{ testResult }}</span>
        <button class="btn" :disabled="testing" @click="testConn">
          <span class="i-carbon-connection-signal" /> {{ testing ? t('testing') : t('testConn') }}
        </button>
        <button class="btn-primary" @click="saveMetatube"><span class="i-carbon-save" /> {{ t('save') }}</button>
      </div>
    </SettingSection>

    <!-- 基本 -->
    <SettingSection :title="t('basic')" :desc="t('basicDesc')">
      <div class="flex gap-4">
        <label class="flex items-center justify-between py-1.5 flex-1">
          <span class="text-[13px] text-text-soft font-medium">{{ t('language') }}</span>
          <select :value="lang" class="input !w-[120px]" @change="changeLanguage(($event.target as HTMLSelectElement).value as Lang)">
            <option value="zh">中文</option>
            <option value="en">English</option>
          </select>
        </label>
        <label class="flex items-center justify-between py-1.5 flex-1">
          <span class="text-[13px] text-text-soft font-medium">{{ t('theme') }}</span>
          <select :value="theme" class="input !w-[120px]" @change="changeTheme(($event.target as HTMLSelectElement).value as Theme)">
            <option value="dark">{{ t('dark') }}</option>
            <option value="light">{{ t('light') }}</option>
          </select>
        </label>
      </div>
      <label class="flex items-center justify-between py-1.5 cursor-pointer">
        <span class="text-[13px] text-text-soft font-medium">{{ t('scrapeTrailer') }}<br><span class="text-[11px] text-muted font-normal">{{ t('scrapeTrailerHint') }}</span></span>
        <input type="checkbox" :checked="scrapeTrailer" class="w-4 h-4 accent-[var(--primary)]" @change="toggleScrapeTrailer(($event.target as HTMLInputElement).checked)" />
      </label>
      <label class="flex items-center justify-between py-1.5">
        <span class="text-[13px] text-text-soft font-medium">{{ t('closeBehavior') }}</span>
        <select :value="closeBehavior" class="input !w-[120px]" @change="changeCloseBehavior(($event.target as HTMLSelectElement).value as 'quit' | 'tray')">
          <option value="quit">{{ t('closeQuit') }}</option>
          <option value="tray">{{ t('closeTray') }}</option>
        </select>
      </label>
      <label class="flex items-center justify-between py-1.5 cursor-pointer">
        <span class="text-[13px] text-text-soft font-medium">{{ t('debugMode') }}</span>
        <input type="checkbox" :checked="debugMode" class="w-4 h-4 accent-[var(--primary)]" @change="toggleDebug(($event.target as HTMLInputElement).checked)" />
      </label>
    </SettingSection>

    <!-- 播放器 -->
    <SettingSection :title="t('player')" :desc="t('playerDesc')">
      <label class="flex flex-col gap-1.5 mb-3">
        <span class="text-[13px] text-text-soft font-medium">{{ t('playerPath') }}</span>
        <input v-model="playerPath" class="input" placeholder="例如:C:\Program Files\mpv\mpv.exe" />
      </label>
      <div class="flex justify-end">
        <button class="btn-primary" @click="savePlayer"><span class="i-carbon-save" /> {{ t('save') }}</button>
      </div>
    </SettingSection>

    <!-- 网络/代理 -->
    <SettingSection :title="t('networkProxy')" :desc="t('proxyDesc')">
      <label class="flex flex-col gap-1.5 mb-3">
        <span class="text-[13px] text-text-soft font-medium">{{ t('proxyAddr') }}</span>
        <input v-model="proxyAddr" class="input" :placeholder="t('noProxyHint')" />
      </label>
      <div class="flex gap-2 mb-3">
        <label class="flex flex-col gap-1.5 flex-1">
          <span class="text-[13px] text-text-soft font-medium">{{ t('proxyUser') }}</span>
          <input v-model="proxyUser" class="input" :placeholder="t('noAuth')" />
        </label>
        <label class="flex flex-col gap-1.5 flex-1">
          <span class="text-[13px] text-text-soft font-medium">{{ t('proxyPass') }}</span>
          <input v-model="proxyPass" class="input" type="password" :placeholder="t('noAuth')" />
        </label>
      </div>
      <div class="flex justify-end">
        <button class="btn-primary" @click="saveProxy"><span class="i-carbon-save" /> {{ t('save') }}</button>
      </div>
    </SettingSection>

    <!-- 关于 -->
    <SettingSection :title="t('about')" :desc="t('aboutDesc')">
      <p class="text-[13px] text-text-soft">
        Javideo v0.1.0 ·
        <a class="text-primary hover:underline" href="https://github.com/metatube-community/metatube-sdk-go" target="_blank">MetaTube</a>
      </p>
    </SettingSection>

    <!-- 导入导出 -->
    <SettingSection :title="t('importExport')" :desc="t('importExportDesc')">
      <div class="flex gap-2">
        <button class="btn" @click="doExport"><span class="i-carbon-export" /> {{ t('exportData') }}</button>
        <button class="btn" @click="triggerImport"><span class="i-carbon-import" /> {{ t('importData') }}</button>
        <input ref="importInput" type="file" accept=".zip" class="hidden" @change="doImport" />
      </div>
      <p v-if="importMsg" class="text-[12px] mt-2" :class="importOk ? 'text-status-green' : 'text-red-400'">{{ importMsg }}</p>
    </SettingSection>

    <LibraryEditDialog v-model="dialogOpen" :library="editingLib" @save="onSave" />
  </div>
</template>
