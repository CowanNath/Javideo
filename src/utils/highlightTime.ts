// ponytail: timestamps are plain seconds; no date/time dependency.
export function parseHighlightTime(value: string): number | null {
  const text = value.trim()
  if (!text) return null
  if (!/^\d{1,3}:\d{2}(?::\d{2})?$/.test(text)) return NaN
  const parts = text.split(':').map(Number)
  if (parts[parts.length - 1] > 59 || parts.length === 3 && (parts[0] > 99 || parts[1] > 59)) return NaN
  return parts.reduce((total, part) => total * 60 + part, 0)
}

export function formatHighlightTime(seconds: number | null | undefined): string {
  if (seconds == null) return ''
  return [Math.floor(seconds / 3600), Math.floor(seconds % 3600 / 60), seconds % 60]
    .map(part => String(part).padStart(2, '0')).join(':')
}
