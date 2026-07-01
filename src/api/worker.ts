// Resolves the Worker base URL. Two modes:
//  - Tauri (production/dev with sidecar): the Rust shell spawns the .NET
//    sidecar and exposes `get_worker_url` via a Tauri command. The worker
//    takes a moment to start, so we poll the command until it returns a URL.
//  - Plain dev (vite only, no Tauri): use the fixed dev port injected by Vite.
let baseUrl: string | null = null
let useTauri = false

// Detect Tauri via its IPC global (set by the webview when loading via the
// tauri:// / localhost asset protocol). Do NOT depend on window.__TAURI__,
// which requires `withGlobalTauri: true`.
if (typeof window !== 'undefined' && '__TAURI_INTERNALS__' in window) {
  useTauri = true
}

async function queryWorkerUrl(): Promise<string | null> {
  const { invoke } = await import('@tauri-apps/api/core')
  try {
    return await invoke<string | null>('get_worker_url')
  } catch {
    return null
  }
}

export async function getBaseUrl(): Promise<string> {
  if (baseUrl) return baseUrl

  if (useTauri) {
    // The worker sidecar prints its port shortly after launch; the Rust shell
    // stores it and exposes it here. Poll for up to ~15s until it's ready.
    for (let i = 0; i < 60; i++) {
      const url = await queryWorkerUrl()
      if (url) {
        baseUrl = url
        return baseUrl
      }
      await new Promise((r) => setTimeout(r, 250))
    }
    throw new Error('Worker 未能启动(等待端口超时)')
  }

  // Plain dev mode (no Tauri) — fixed port from Vite define.
  baseUrl = typeof __DEV_WORKER_BASE__ !== 'undefined' ? __DEV_WORKER_BASE__ : 'http://127.0.0.1:1375'
  return baseUrl
}

async function req<T>(path: string, init?: RequestInit): Promise<T> {
  const base = await getBaseUrl()
  const resp = await fetch(`${base}${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...(init?.headers || {}) },
  })
  if (!resp.ok) {
    const text = await resp.text().catch(() => '')
    throw new Error(`${resp.status} ${resp.statusText} ${text}`)
  }
  if (resp.status === 204) return undefined as unknown as T
  // Some endpoints return 200 with an empty body (e.g. favorites add). Parsing
  // that as JSON throws "Unexpected end of JSON input", so guard it.
  const text = await resp.text()
  if (!text) return undefined as unknown as T
  try {
    return JSON.parse(text) as T
  } catch {
    return undefined as unknown as T
  }
}

// ---- Libraries ----
export const libraries = {
  list: () => req<import('../types').Library[]>('/api/libraries'),
  get: (id: number) => req<import('../types').Library>(`/api/libraries/${id}`),
  create: (l: Partial<import('../types').Library>) =>
    req<import('../types').Library>('/api/libraries', { method: 'POST', body: JSON.stringify(l) }),
  update: (id: number, l: Partial<import('../types').Library>) =>
    req<import('../types').Library>(`/api/libraries/${id}`, { method: 'PUT', body: JSON.stringify(l) }),
  remove: (id: number) => req<void>(`/api/libraries/${id}`, { method: 'DELETE' }),
  checkDir: (path: string) =>
    req<{ exists: boolean }>(`/api/libraries/check-dir?path=${encodeURIComponent(path)}`),
  checkName: (name: string, exclude?: number) =>
    req<{ taken: boolean }>(`/api/libraries/check-name?name=${encodeURIComponent(name)}${exclude ? `&exclude=${exclude}` : ''}`),
}

// ---- Movies ----
export const movies = {
  byLibrary: async (libraryId: number): Promise<import('../types').Movie[]> => {
    const list = await req<import('../types').Movie[]>(`/api/movies/library/${libraryId}`)
    // Hydrate relative local-image paths to absolute URLs.
    await Promise.all(list.map(async (m) => {
      m.coverUrl = await absoluteAvatar(m.coverUrl)
      m.thumbUrl = await absoluteAvatar(m.thumbUrl)
    }))
    return list
  },
  get: async (id: number): Promise<import('../types').Movie> => {
    const m = await req<import('../types').Movie>(`/api/movies/${id}`)
    // Hydrate relative local-image paths to absolute URLs.
    m.coverUrl = await absoluteAvatar(m.coverUrl)
    m.thumbUrl = await absoluteAvatar(m.thumbUrl)
    if (m.previewImages?.length)
      m.previewImages = await Promise.all(m.previewImages.map(async (u) => await absoluteAvatar(u) ?? u))
    return m
  },
  ingest: (libraryId: number, movie: import('../types').Movie, magnets?: import('../types').MagnetResult[]) =>
    req<import('../types').IngestResult>('/api/movies/ingest', {
      method: 'POST',
      body: JSON.stringify({ LibraryId: libraryId, Movie: movie, Magnets: magnets }),
    }),
  // Scrape by 番号 + ingest in one shot (scan-to-ingest flow).
  ingestByNumber: (libraryId: number, number: string, sourceFilePath?: string) =>
    req<import('../types').IngestResult>('/api/movies/ingest-by-number', {
      method: 'POST',
      body: JSON.stringify({ LibraryId: libraryId, Number: number, SourceFilePath: sourceFilePath }),
    }),
  // Add a custom tag to a movie.
  addTag: (id: number, name: string) =>
    req<{ ok: boolean; detail: string }>(`/api/movies/${id}/tags`, {
      method: 'POST',
      body: JSON.stringify({ Name: name }),
    }),
  // Play a movie's local file in the configured player.
  play: (id: number) =>
    req<{ ok: boolean; detail: string }>(`/api/movies/${id}/play`, { method: 'POST' }),
  // Trailer (DMM free preview) file URL for inline playback.
  trailerUrl: async (id: number) => `${await getBaseUrl()}/api/movies/${id}/trailer`,
  // Remove a movie (optionally wipe its generated folder).
  remove: (id: number, removeFiles = false) =>
    req<void>(`/api/movies/${id}?removeFiles=${removeFiles}`, { method: 'DELETE' }),
  // Re-scrape an existing movie.
  rescrape: (id: number) =>
    req<{ ok: boolean; detail: string }>(`/api/movies/${id}/rescrape`, { method: 'POST' }),
  rescrapePick: (id: number, provider: string, movId: string) =>
    req<{ ok: boolean; detail: string }>(`/api/movies/${id}/rescrape-pick`, {
      method: 'POST',
      body: JSON.stringify({ Provider: provider, Id: movId }),
    }),
}

// ---- MetaTube scraping ----
export class MetatubeError extends Error {
  needsConfig: boolean
  constructor(message: string, needsConfig = false) {
    super(message)
    this.needsConfig = needsConfig
  }
}
export const metatube = {
  // Dedicated handler so the frontend can detect the "not configured" case
  // (412 + needsConfig) and prompt the user to set up the server.
  scrape: async (number: string): Promise<import('../types').Movie> => {
    const base = await getBaseUrl()
    const resp = await fetch(`${base}/api/metatube/movie/${encodeURIComponent(number)}`, {
      headers: { 'Content-Type': 'application/json' },
    })
    if (resp.ok) return resp.json()
    let detail = `${resp.status} ${resp.statusText}`
    let needsConfig = false
    try {
      const body = await resp.json()
      detail = body.detail ?? detail
      needsConfig = !!body.needsConfig
    } catch { /* keep default detail */ }
    throw new MetatubeError(detail, needsConfig)
  },
  test: () => req<{ ok: boolean; detail: string }>('/api/metatube/test'),
  findTrailer: async (number: string): Promise<{ ok: boolean; url: string | null }> => {
    const res = await req<{ ok: boolean; url: string | null }>(`/api/metatube/trailer/${encodeURIComponent(number)}`)
    // Backend returns a relative path like "/api/metatube/trailer-temp/XXX"
    // — hydrate to absolute for <video src>.
    if (res.ok && res.url && res.url.startsWith('/')) {
      res.url = await getBaseUrl() + res.url
    }
    return res
  },
  // Search all candidates for a picker UI.
  candidates: (number: string) =>
    req<{ provider: string; id: string; number: string; title?: string | null; coverUrl?: string | null; thumbUrl?: string | null; score?: number | null }[]>(
      `/api/metatube/candidates/${encodeURIComponent(number)}`),
  // Scrape a specific provider/id (after user picks a candidate).
  scrapeByProvider: async (number: string, provider: string, id: string): Promise<import('../types').Movie> => {
    const base = await getBaseUrl()
    const resp = await fetch(`${base}/api/metatube/movie/${encodeURIComponent(number)}/${encodeURIComponent(provider)}/${encodeURIComponent(id)}`, {
      headers: { 'Content-Type': 'application/json' },
    })
    if (resp.ok) return resp.json()
    throw new Error(`${resp.status}`)
  },
}

// ---- Magnet ----
export const magnet = {
  search: (q: string) =>
    req<import('../types').MagnetResult[]>(`/api/magnet/search?q=${encodeURIComponent(q)}`),
  searchGrouped: (q: string) =>
    req<import('../types').MagnetSourceResult[]>(`/api/magnet/search-grouped?q=${encodeURIComponent(q)}`),
}

// ---- Scan ----
export const scan = {
  run: (libraryId: number) =>
    req<import('../types').ScanResult>(`/api/libraries/${libraryId}/scan`, { method: 'POST' }),
}

// ---- Settings ----
export const settings = {
  all: () => req<Record<string, string>>('/api/settings'),
  get: (key: string) => req<{ key: string; value: string | null }>(`/api/settings/${key}`),
  set: (key: string, value: string) =>
    req<{ key: string; value: string }>(`/api/settings/${key}`, {
      method: 'PUT',
      body: JSON.stringify({ Value: value }),
    }),
}

// ---- Favorites ----
export const favorites = {
  list: (type: import('../types').FavoriteTarget) =>
    req<import('../types').Favorite[]>(`/api/favorites/${type}`),
  ids: (type: import('../types').FavoriteTarget) =>
    req<number[]>(`/api/favorites/${type}/ids`),
  add: (type: import('../types').FavoriteTarget, targetId: number) =>
    req<void>('/api/favorites', { method: 'POST', body: JSON.stringify({ TargetType: type, TargetId: targetId }) }),
  remove: (type: import('../types').FavoriteTarget, targetId: number) =>
    req<void>(`/api/favorites/${type}/${targetId}`, { method: 'DELETE' }),
  batchRemove: (type: import('../types').FavoriteTarget, targetIds: number[]) =>
    req<{ removed: number }>('/api/favorites/batch-remove', {
      method: 'POST',
      body: JSON.stringify({ targetType: type, targetIds: targetIds }),
    }),
}

// ---- Actors ----
// Resolve a possibly-relative avatar URL (returned as "/api/actors/{id}/avatar"
// by the worker) to a full URL for <img src>.
async function absoluteAvatar(url?: string | null): Promise<string | null | undefined> {
  if (!url) return url
  if (/^https?:\/\//i.test(url)) return url
  return (await getBaseUrl()) + url
}

export const actors = {
  list: async (q?: string): Promise<import('../types').Actor[]> => {
    const list = await req<import('../types').Actor[]>(`/api/actors${q ? `?q=${encodeURIComponent(q)}` : ''}`)
    // Hydrate avatar URLs to absolute (the list returns the local endpoint path).
    await Promise.all(list.map(async (a) => { a.avatarUrl = await absoluteAvatar(a.avatarUrl) }))
    return list
  },
  movies: (id: number) => req<import('../types').Movie[]>(`/api/actors/${id}/movies`),
  detail: async (id: number): Promise<import('../types').ActorDetailResponse> => {
    const res = await req<import('../types').ActorDetailResponse>(`/api/actors/${id}/detail`)
    if (res.actor) res.actor.avatarUrl = await absoluteAvatar(res.actor.avatarUrl)
    return res
  },
}

// ---- Tags ----
export const tags = {
  list: (params?: { category?: string; standard?: boolean }) => {
    const qs = new URLSearchParams()
    if (params?.category) qs.set('category', params.category)
    if (params?.standard !== undefined) qs.set('standard', String(params.standard))
    const q = qs.toString()
    return req<import('../types').Tag[]>(`/api/tags${q ? `?${q}` : ''}`)
  },
  movies: (id: number) => req<import('../types').Movie[]>(`/api/tags/${id}/movies`),
  info: (id: number) => req<import('../types').Tag>(`/api/tags/${id}/info`),
  rename: (id: number, name: string) =>
    req<{ ok: boolean; detail: string }>(`/api/tags/${id}`, {
      method: 'PUT',
      body: JSON.stringify({ Name: name }),
    }),
}

// ---- Translation ----
export const translate = {
  run: (title?: string | null, summary?: string | null) =>
    req<{ title?: string; summary?: string }>('/api/translate', {
      method: 'POST',
      body: JSON.stringify({ Title: title, Summary: summary }),
    }),
  test: () => req<{ ok: boolean; detail: string }>('/api/translate/test', { method: 'POST' }),
}

// ---- Health ----
export const health = () => req<{ ok: boolean; name: string; time: string }>('/api/health')

// ---- Backup (export/import) ----
export const backup = {
  // Export to a user-chosen local path (Tauri save dialog picks the path,
  // worker writes the zip there directly — no silent webview download).
  exportTo: async (path: string) =>
    req<{ ok: boolean; path?: string; detail?: string }>(`/api/backup/export-to?path=${encodeURIComponent(path)}`),
  // Import: upload a zip file.
  import: async (file: File): Promise<{ ok: boolean; detail: string }> => {
    const base = await getBaseUrl()
    const form = new FormData()
    form.append('file', file)
    const resp = await fetch(`${base}/api/backup/import`, { method: 'POST', body: form })
    return resp.json()
  },
}
