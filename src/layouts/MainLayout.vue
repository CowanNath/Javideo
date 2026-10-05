<script setup lang="ts">
import { computed, onMounted } from 'vue'
import { useRoute } from 'vue-router'
import { useLibraryStore } from '@/stores/libraries'
import { useSettingsStore } from '@/stores/settings'
import { t } from '@/utils/i18n'
import { useMovieDetail } from '@/utils/movieDetail'

const appVersion = __APP_VERSION__
const route = useRoute()
const { drawerOpen } = useMovieDetail()
const libs = useLibraryStore()
const settings = useSettingsStore()

onMounted(async () => {
  await Promise.allSettled([libs.load(), settings.load()])
})

// Total ingested movies across all libraries — hero meta + "全部影片" badge.
const totalMovies = computed(() => libs.items.reduce((n: number, l: any) => n + (l.movieCount ?? 0), 0))

// Sidebar rail entries. computed so labels follow the language switch.
const nav = computed(() => [
  { to: '/library/all', label: t('libraries'), icon: 'i-carbon-apps' },
  { to: '/search', label: t('search'), icon: 'i-carbon-search' },
  { to: '/favorites', label: t('favorites'), icon: 'i-carbon-favorite' },
  { to: '/actors', label: t('actors'), icon: 'i-carbon-user-multiple' },
  { to: '/tags', label: t('tags'), icon: 'i-carbon-tag' },
])

const isLibrary = computed(() => route.name === 'library')
const title = computed(() => {
  if (route.name === 'library') return t('libraries')
  return nav.value.find(n => n.to === route.path)?.label ?? t('settings')
})
const navSelected = (to: string) =>
  to === '/library/all' ? isLibrary.value : route.path.startsWith(to)

// —— Tauri 无边框窗口的自绘控制钮。浏览器 dev 下隐藏（isTauri=false），
//    拖动/双击最大化由 titlebar 上的 data-tauri-drag-region 处理。 ——
const isTauri = typeof window !== 'undefined' && '__TAURI_INTERNALS__' in window
async function winAction(action: 'minimize' | 'maximize' | 'close') {
  if (!isTauri) return
  const { getCurrentWindow } = await import('@tauri-apps/api/window')
  const w = getCurrentWindow()
  if (action === 'minimize') await w.minimize()
  else if (action === 'maximize') await w.toggleMaximize()
  else await w.close()
}
</script>

<template>
  <div class="app-stage">
    <div class="box-shell">
      <!-- 70px 独立圆角侧栏 -->
      <aside class="box-sidebar">
        <RouterLink to="/library/all" class="box-logo" aria-label="Javideo">J<span /></RouterLink>
        <nav class="box-nav" aria-label="main">
          <RouterLink
            v-for="item in nav"
            :key="item.to"
            :to="item.to"
            class="box-nav-link"
            :class="{ selected: navSelected(item.to) }"
            :aria-label="item.label"
            :title="item.label"
          >
            <span :class="item.icon" class="nav-glyph" />
            <span class="nav-caption">{{ item.label }}</span>
          </RouterLink>
        </nav>
        <div class="sidebar-bottom">
          <RouterLink
            to="/settings"
            class="box-nav-link"
            :class="{ selected: route.name === 'settings' }"
            :aria-label="t('settings')"
            :title="t('settings')"
          >
            <span class="i-carbon-settings nav-glyph" />
            <span class="nav-caption">{{ t('settings') }}</span>
          </RouterLink>
        </div>
      </aside>

      <!-- 分离内容面板：标题栏 / 滚动区 / 页脚 -->
      <section class="box-panel">
        <header class="box-titlebar" data-tauri-drag-region>
          <div class="title-breadcrumb" data-tauri-drag-region>
            <span class="wordmark">JAVIDEO</span>
            <span class="breadcrumb-divider">/</span>
            <span>{{ title }}</span>
          </div>
          <div class="window-actions">
            <button class="window-control" aria-label="Minimize" @click="winAction('minimize')">
              <span class="i-carbon-minimize text-[12px]" />
            </button>
            <button class="window-control" aria-label="Maximize" @click="winAction('maximize')">
              <span class="i-carbon-maximize text-[12px]" />
            </button>
            <button class="window-control close" aria-label="Close" @click="winAction('close')">
              <span class="i-carbon-close text-[13px]" />
            </button>
          </div>
        </header>

        <div class="box-content">
          <div id="movie-detail-host" class="movie-detail-host" />
          <main class="box-scroll" :inert="drawerOpen">
            <!-- 影片库 hero 横幅（真实库统计） -->
            <section v-if="isLibrary" class="library-hero">
              <div class="hero-art" aria-hidden="true" />
              <div class="hero-shade" aria-hidden="true" />
              <div class="hero-copy">
                <div class="hero-eyebrow"><span />YOUR PERSONAL CINEMA</div>
                <h1>{{ t('heroTitle') }}</h1>
                <p>{{ t('heroSub') }}</p>
                <div class="hero-meta">
                  <span>{{ totalMovies }} {{ t('movies') }}</span>
                  <i />
                  <span>{{ libs.items.length }} {{ t('libraries') }}</span>
                  <i />
                  <span>{{ t('heroLocal') }}</span>
                </div>
              </div>
              <div class="hero-index" aria-hidden="true">
                <span>J.</span>
                <small>COLLECTION<br>01 — 2026</small>
              </div>
            </section>

            <!-- 媒体库标签行（全部影片 + 各库，金色下划线） -->
            <div v-if="isLibrary" class="library-tabs" aria-label="libraries">
              <RouterLink to="/library/all" :class="{ active: route.params.id === 'all' }">
                {{ t('allLibraries') }}<span>{{ totalMovies }}</span>
              </RouterLink>
              <RouterLink
                v-for="lib in libs.items"
                :key="lib.id"
                :to="`/library/${lib.id}`"
                :class="{ active: String(lib.id) === route.params.id }"
              >{{ lib.name }}<span>{{ lib.movieCount }}</span></RouterLink>
            </div>

            <slot />
          </main>
        </div>

        <footer class="box-footer">
          <span>Javideo v{{ appVersion }}</span>
        </footer>
      </section>
    </div>
  </div>
</template>
