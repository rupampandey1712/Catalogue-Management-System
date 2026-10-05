import { onMounted, onUnmounted, reactive, ref } from 'vue'

// ---------- toasts ----------
export type ToastKind = 'good' | 'bad' | 'info'
export interface Toast { id: number; kind: ToastKind; title: string; message?: string }

export const toasts = reactive<Toast[]>([])
let toastId = 0

export function toast(kind: ToastKind, title: string, message?: string, ms = 4500) {
  const id = ++toastId
  toasts.push({ id, kind, title, message })
  setTimeout(() => dismiss(id), ms)
}
export function dismiss(id: number) {
  const i = toasts.findIndex(t => t.id === id)
  if (i >= 0) toasts.splice(i, 1)
}

// ---------- theme ----------
export type Theme = 'light' | 'dark' | 'system'
export const theme = ref<Theme>((safeGet('theme') as Theme) || 'system')

export function applyTheme(t: Theme) {
  theme.value = t
  safeSet('theme', t)
  if (t === 'system') document.documentElement.removeAttribute('data-theme')
  else document.documentElement.setAttribute('data-theme', t)
}
export function isDark() {
  return theme.value === 'dark' ||
    (theme.value === 'system' && window.matchMedia('(prefers-color-scheme: dark)').matches)
}

function safeGet(k: string) { try { return localStorage.getItem(k) } catch { return null } }
function safeSet(k: string, v: string) { try { localStorage.setItem(k, v) } catch { /* private mode */ } }
export const prefs = { get: safeGet, set: safeSet }

// ---------- polling ----------
/** Calls fn now and every ms while the component is mounted and the tab is visible. */
export function usePolling(fn: () => Promise<unknown> | void, ms: number) {
  let timer: ReturnType<typeof setTimeout> | undefined
  let stopped = false
  const tick = async () => {
    if (stopped) return
    if (document.visibilityState === 'visible') {
      try { await fn() } catch { /* surfaced by caller */ }
    }
    if (!stopped) timer = setTimeout(tick, ms)
  }
  onMounted(tick)
  onUnmounted(() => { stopped = true; clearTimeout(timer) })
}

// ---------- formatting ----------
const nf = new Intl.NumberFormat()
const compactNf = new Intl.NumberFormat(undefined, { notation: 'compact', maximumFractionDigits: 1 })

export const fmt = {
  n: (v?: number | null) => (v == null ? '—' : nf.format(v)),
  compact: (v?: number | null) => (v == null ? '—' : Math.abs(v) < 10000 ? nf.format(Math.round(v)) : compactNf.format(v)),
  pct: (part: number, whole: number) => (whole > 0 ? Math.min(100, Math.round((part / whole) * 1000) / 10) : 0),
  money: (amount?: number | null, currency?: string | null) => {
    if (amount == null) return '—'
    try { return new Intl.NumberFormat(undefined, { style: 'currency', currency: currency || 'GBP' }).format(amount) }
    catch { return `${amount.toFixed(2)} ${currency ?? ''}` }
  },
  bytes: (b?: number | null) => {
    if (b == null) return '—'
    const u = ['B', 'KB', 'MB', 'GB', 'TB']
    let i = 0, v = b
    while (v >= 1024 && i < u.length - 1) { v /= 1024; i++ }
    return `${v.toFixed(v < 10 && i > 0 ? 1 : 0)} ${u[i]}`
  },
  date: (iso?: string | null) => (iso ? new Date(asUtc(iso)).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' }) : '—'),
  time: (iso?: string | null) => (iso ? new Date(asUtc(iso)).toLocaleTimeString() : '—'),
  ago: (iso?: string | null) => {
    if (!iso) return '—'
    const s = Math.round((Date.now() - new Date(asUtc(iso)).getTime()) / 1000)
    if (s < 5) return 'just now'
    if (s < 60) return `${s}s ago`
    if (s < 3600) return `${Math.floor(s / 60)}m ago`
    if (s < 86400) return `${Math.floor(s / 3600)}h ago`
    return `${Math.floor(s / 86400)}d ago`
  },
  duration: (ms: number) => {
    if (ms < 1000) return `${Math.round(ms)} ms`
    const s = ms / 1000
    if (s < 60) return `${s.toFixed(1)} s`
    const m = Math.floor(s / 60)
    return `${m}m ${Math.round(s % 60)}s`
  }
}

/** The API returns UTC DateTimes without a 'Z'; treat them as UTC. */
function asUtc(iso: string) {
  return /[zZ]|[+-]\d\d:\d\d$/.test(iso) ? iso : iso + 'Z'
}

// ---------- status → tone ----------
export function statusTone(status: string): 'good' | 'warn' | 'bad' | 'info' | 'neutral' {
  switch (status) {
    case 'Completed': case 'Active': case 'Published': return 'good'
    case 'CompletedWithErrors': case 'Draft': return 'warn'
    case 'Failed': case 'Rejected': return 'bad'
    case 'Processing': case 'Received': case 'Retrying': case 'Publishing': return 'info'
    default: return 'neutral'
  }
}
export function statusLabel(status: string) {
  return status === 'CompletedWithErrors' ? 'Completed w/ errors' : status
}

// ---------- keyboard ----------
export function useHotkey(key: string, handler: (e: KeyboardEvent) => void) {
  const onKey = (e: KeyboardEvent) => {
    const combo = `${e.ctrlKey || e.metaKey ? 'mod+' : ''}${e.key.toLowerCase()}`
    if (combo === key) handler(e)
  }
  onMounted(() => window.addEventListener('keydown', onKey))
  onUnmounted(() => window.removeEventListener('keydown', onKey))
}

export const layout = reactive({ collapsed: safeGet('sidebar') === 'collapsed', mobileOpen: false })
export function toggleSidebar() {
  layout.collapsed = !layout.collapsed
  safeSet('sidebar', layout.collapsed ? 'collapsed' : 'open')
}
