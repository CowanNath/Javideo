// Opens the native OS folder picker via Tauri's dialog plugin. Returns the
// chosen path or null if the user cancelled / we're not running in Tauri.
export async function pickFolder(): Promise<string | null> {
  // The dialog plugin only works under Tauri's webview.
  if (typeof window === 'undefined' || !('__TAURI_INTERNALS__' in window)) return null
  try {
    const { open } = await import('@tauri-apps/plugin-dialog')
    const sel = await open({ directory: true, multiple: false })
    // open() returns string | string[] | null for directory selection.
    return typeof sel === 'string' ? sel : null
  } catch {
    return null
  }
}
