// A confirm() replacement that works inside Tauri's webview. The browser's
// native window.confirm is blocked by Tauri's ACL, so we use the dialog
// plugin's `ask` (returns a Promise<boolean>). Falls back to window.confirm
// when not running under Tauri (plain browser dev).
export async function confirmDialog(message: string, title = '确认'): Promise<boolean> {
  if (typeof window !== 'undefined' && '__TAURI_INTERNALS__' in window) {
    try {
      const { ask } = await import('@tauri-apps/plugin-dialog')
      return await ask(message, { title, kind: 'warning' })
    } catch {
      return window.confirm(message)
    }
  }
  return window.confirm(message)
}
