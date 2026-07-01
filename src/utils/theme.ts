// Theme management. The chosen theme is persisted to localStorage so the UI
// applies it immediately on startup (before the worker is even reachable),
// and also mirrored into the settings store for sync.

export type Theme = 'light' | 'dark'

const STORAGE_KEY = 'javideo.theme'

export function getStoredTheme(): Theme {
  // Dark is the app's default design language.
  const v = localStorage.getItem(STORAGE_KEY)
  return v === 'light' ? 'light' : 'dark'
}

export function applyTheme(theme: Theme) {
  document.documentElement.setAttribute('data-theme', theme)
  localStorage.setItem(STORAGE_KEY, theme)
}

// Apply the stored theme as early as possible to avoid a flash of the wrong colors.
export function initTheme() {
  applyTheme(getStoredTheme())
}
