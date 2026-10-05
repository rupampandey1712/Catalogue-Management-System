/** Chart colours: theme series tokens first (they adapt to dark mode), then extra hues. */
export const palette = [
  'var(--series-1)', 'var(--series-2)', 'var(--series-3)',
  '#8b5cf6', '#ec4899', '#f59e0b', '#14b8a6', '#64748b', '#ef4444', '#84cc16'
]

/** Gradient pairs for bars and slices (start, end). */
export const gradients: [string, string][] = [
  ['#6366f1', '#22d3ee'], ['#f97316', '#facc15'], ['#10b981', '#a3e635'], ['#8b5cf6', '#ec4899'],
  ['#ec4899', '#f43f5e'], ['#f59e0b', '#ef4444'], ['#14b8a6', '#3b82f6'], ['#64748b', '#94a3b8'],
  ['#ef4444', '#f97316'], ['#84cc16', '#22c55e']
]

/** Semantic colours for well-known statuses so the same state looks the same in every chart. */
const status: Record<string, [string, string]> = {
  Submitted: ['#3b82f6', '#60a5fa'], Accepted: ['#6366f1', '#a78bfa'], Shipped: ['#10b981', '#34d399'],
  Rejected: ['#ef4444', '#f87171'], Cancelled: ['#94a3b8', '#cbd5e1'],
  Succeeded: ['#10b981', '#34d399'], Pending: ['#f59e0b', '#fbbf24'], Failed: ['#ef4444', '#f87171'], Refunded: ['#6366f1', '#818cf8'],
  Completed: ['#10b981', '#34d399'], CompletedWithErrors: ['#f59e0b', '#fbbf24'], Processing: ['#3b82f6', '#60a5fa'],
  Received: ['#0ea5e9', '#38bdf8'], Retrying: ['#f97316', '#fb923c'],
  Approved: ['#10b981', '#34d399'], Active: ['#10b981', '#34d399'], PendingVerification: ['#f59e0b', '#fbbf24'], Suspended: ['#ef4444', '#f87171'],
  InStock: ['#10b981', '#34d399'], LowStock: ['#f59e0b', '#fbbf24'], OnOrder: ['#3b82f6', '#60a5fa'], Discontinued: ['#94a3b8', '#cbd5e1'], Unknown: ['#64748b', '#94a3b8'],
  Draft: ['#94a3b8', '#cbd5e1'], Inactive: ['#64748b', '#94a3b8'], PendingApproval: ['#f59e0b', '#fbbf24'],
  Image: ['#6366f1', '#22d3ee'], Datasheet: ['#f97316', '#facc15'], Manual: ['#10b981', '#a3e635'], Simulated: ['#8b5cf6', '#ec4899'], Stripe: ['#6366f1', '#818cf8']
}

export function gradientFor(label: string, index: number): [string, string] {
  return status[label] ?? gradients[index % gradients.length]
}

let uid = 0
/** Unique ids for SVG gradients/filters (several charts share a page). */
export const nextId = (prefix: string) => `${prefix}-${++uid}`

/** Human labels: "CompletedWithErrors" → "Completed with errors". */
export const humanize = (s: string) => s.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/^./, c => c.toUpperCase()).replace(/ ([A-Z])(?=[a-z])/g, (_, c) => ' ' + c.toLowerCase())

/** Smooth path through points (monotone cubic, no overshoot below zero). */
export function smoothPath(pts: [number, number][]): string {
  if (pts.length === 0) return ''
  if (pts.length === 1) return `M${pts[0][0]},${pts[0][1]}`
  const n = pts.length
  const dx: number[] = [], m: number[] = [], t: number[] = []
  for (let i = 0; i < n - 1; i++) { dx[i] = pts[i + 1][0] - pts[i][0]; m[i] = (pts[i + 1][1] - pts[i][1]) / (dx[i] || 1) }
  t[0] = m[0]; t[n - 1] = m[n - 2]
  for (let i = 1; i < n - 1; i++) t[i] = m[i - 1] * m[i] <= 0 ? 0 : (m[i - 1] + m[i]) / 2
  for (let i = 0; i < n - 1; i++) {
    if (m[i] === 0) { t[i] = 0; t[i + 1] = 0; continue }
    const a = t[i] / m[i], b = t[i + 1] / m[i], s = a * a + b * b
    if (s > 9) { const k = 3 / Math.sqrt(s); t[i] = k * a * m[i]; t[i + 1] = k * b * m[i] }
  }
  let d = `M${pts[0][0].toFixed(1)},${pts[0][1].toFixed(1)}`
  for (let i = 0; i < n - 1; i++) {
    const h = dx[i] / 3
    d += `C${(pts[i][0] + h).toFixed(1)},${(pts[i][1] + h * t[i]).toFixed(1)} ${(pts[i + 1][0] - h).toFixed(1)},${(pts[i + 1][1] - h * t[i + 1]).toFixed(1)} ${pts[i + 1][0].toFixed(1)},${pts[i + 1][1].toFixed(1)}`
  }
  return d
}

export function niceMax(v: number) {
  if (v <= 0) return 1
  const exp = Math.pow(10, Math.floor(Math.log10(v)))
  const f = v / exp
  return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10) * exp
}

export function compact(v: number) {
  const a = Math.abs(v)
  if (a >= 1e9) return (v / 1e9).toFixed(a >= 1e10 ? 0 : 1) + 'B'
  if (a >= 1e6) return (v / 1e6).toFixed(a >= 1e7 ? 0 : 1) + 'M'
  if (a >= 1e3) return (v / 1e3).toFixed(a >= 1e4 ? 0 : 1) + 'k'
  return Number.isInteger(v) ? String(v) : v.toFixed(1)
}
