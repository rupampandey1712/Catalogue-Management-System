<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { compact, nextId, niceMax, palette, smoothPath } from './palette'

/**
 * "Breathing" line chart: smooth lines over gradient areas that draw in, a soft glow that pulses on the
 * lines and a beating dot on the latest value. Hover shows every series at that point.
 */
export interface PulseSeries { label: string; values: number[]; color?: string; area?: boolean }

const props = withDefaults(defineProps<{
  labels: string[]
  series: PulseSeries[]
  height?: number
  format?: (v: number) => string
  formatLabel?: (iso: string) => string
  /** right axis for the second series (different units, e.g. count vs money) */
  dualAxis?: boolean
}>(), { height: 240, format: (v: number) => compact(v), formatLabel: (s: string) => s, dualAxis: false })

const root = ref<HTMLDivElement>()
const width = ref(640)
const hover = ref<number | null>(null)
const id = nextId('pulse')
let ro: ResizeObserver | undefined
onMounted(() => {
  ro = new ResizeObserver(([e]) => { width.value = Math.max(260, e.contentRect.width) })
  if (root.value) ro.observe(root.value)
})
onUnmounted(() => ro?.disconnect())

const pad = computed(() => ({ top: 16, right: props.dualAxis ? 52 : 16, bottom: 28, left: 50 }))
const innerW = computed(() => width.value - pad.value.left - pad.value.right)
const innerH = computed(() => props.height - pad.value.top - pad.value.bottom)
const n = computed(() => props.labels.length)
const colorOf = (i: number) => props.series[i].color ?? palette[i % palette.length]

const maxFor = (i: number) => niceMax(Math.max(0, ...props.series[i].values) * 1.12)
const sharedMax = computed(() => niceMax(Math.max(0, ...props.series.flatMap(s => s.values)) * 1.12))
const yMax = (i: number) => (props.dualAxis && i > 0 ? maxFor(i) : props.dualAxis ? maxFor(0) : sharedMax.value)
const sx = (i: number) => pad.value.left + (n.value <= 1 ? innerW.value / 2 : (i / (n.value - 1)) * innerW.value)
const sy = (v: number, s: number) => pad.value.top + innerH.value - (v / yMax(s)) * innerH.value

const paths = computed(() => props.series.map((s, si) => {
  const pts = s.values.map((v, i) => [sx(i), sy(v, si)] as [number, number])
  const line = smoothPath(pts)
  const base = pad.value.top + innerH.value
  const area = pts.length ? `${line}L${pts[pts.length - 1][0].toFixed(1)},${base}L${pts[0][0].toFixed(1)},${base}Z` : ''
  return { line, area, last: pts[pts.length - 1] }
}))

const lastIndex = computed(() => {
  // Latest bucket that has data in any series (hourly "today" has empty future hours).
  for (let i = n.value - 1; i >= 0; i--) if (props.series.some(s => (s.values[i] ?? 0) > 0)) return i
  return n.value - 1
})

const yTicks = computed(() => [0, 0.25, 0.5, 0.75, 1].map(f => ({ f, left: f * yMax(0), right: props.dualAxis && props.series.length > 1 ? f * yMax(1) : null })))
const xTicks = computed(() => {
  const count = Math.max(2, Math.min(n.value, Math.floor(innerW.value / 90)))
  return Array.from(new Set(Array.from({ length: count }, (_, k) => Math.round((k / (count - 1)) * (n.value - 1)))))
})

function onMove(e: PointerEvent) {
  const rect = (e.currentTarget as SVGElement).getBoundingClientRect()
  if (!n.value) return
  const i = Math.round(((e.clientX - rect.left - pad.value.left) / innerW.value) * (n.value - 1))
  hover.value = Math.min(n.value - 1, Math.max(0, i))
}
const tipLeft = computed(() => (hover.value == null ? 0 : Math.min(width.value - 170, Math.max(0, sx(hover.value) + 12))))
const empty = computed(() => props.series.every(s => s.values.every(v => !v)))
</script>

<template>
  <div ref="root" class="pulse" :style="{ height: height + 'px' }">
    <svg :width="width" :height="height" role="img" :aria-label="series.map(s => s.label).join(', ')" @pointermove="onMove" @pointerleave="hover = null">
      <defs>
        <linearGradient v-for="(s, i) in series" :id="`${id}-g${i}`" :key="i" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" :style="{ stopColor: colorOf(i), stopOpacity: 0.38 }" />
          <stop offset="70%" :style="{ stopColor: colorOf(i), stopOpacity: 0.06 }" />
          <stop offset="100%" :style="{ stopColor: colorOf(i), stopOpacity: 0 }" />
        </linearGradient>
        <filter :id="`${id}-glow`" x="-10%" y="-30%" width="120%" height="160%"><feGaussianBlur stdDeviation="4" /></filter>
      </defs>

      <g class="grid">
        <line v-for="t in yTicks" :key="t.f" :x1="pad.left" :x2="width - pad.right" :y1="pad.top + innerH - t.f * innerH" :y2="pad.top + innerH - t.f * innerH" />
      </g>
      <g class="axis">
        <text v-for="t in yTicks" :key="'l' + t.f" :x="pad.left - 8" :y="pad.top + innerH - t.f * innerH + 4" text-anchor="end">{{ format(t.left) }}</text>
        <template v-if="dualAxis && series.length > 1">
          <text v-for="t in yTicks" :key="'r' + t.f" :x="width - pad.right + 8" :y="pad.top + innerH - t.f * innerH + 4" :fill="colorOf(1)">{{ format(t.right ?? 0) }}</text>
        </template>
        <text v-for="i in xTicks" :key="'x' + i" :x="sx(i)" :y="height - 8" text-anchor="middle">{{ formatLabel(labels[i]) }}</text>
      </g>

      <g v-for="(p, i) in paths" :key="'s' + i">
        <path v-if="series[i].area !== false" :d="p.area" :fill="`url(#${id}-g${i})`" class="area" />
        <path :d="p.line" fill="none" :stroke="colorOf(i)" stroke-width="7" :filter="`url(#${id}-glow)`" class="breath" :style="{ animationDelay: i * 0.6 + 's' }" />
        <path :d="p.line" fill="none" :stroke="colorOf(i)" stroke-width="2.4" stroke-linecap="round" class="draw" pathLength="1" />
      </g>

      <g v-if="!empty && lastIndex >= 0">
        <g v-for="(s, i) in series" :key="'d' + i">
          <circle :cx="sx(lastIndex)" :cy="sy(s.values[lastIndex] ?? 0, i)" r="9" :fill="colorOf(i)" class="beat" />
          <circle :cx="sx(lastIndex)" :cy="sy(s.values[lastIndex] ?? 0, i)" r="4" :fill="colorOf(i)" stroke="var(--surface)" stroke-width="2" />
        </g>
      </g>

      <g v-if="hover != null">
        <line class="cross" :x1="sx(hover)" :x2="sx(hover)" :y1="pad.top" :y2="pad.top + innerH" />
        <circle v-for="(s, i) in series" :key="'h' + i" :cx="sx(hover)" :cy="sy(s.values[hover] ?? 0, i)" r="4.5" :fill="colorOf(i)" stroke="var(--surface)" stroke-width="2" />
      </g>
      <text v-if="empty" :x="width / 2" :y="height / 2" text-anchor="middle" class="empty">No activity in this period</text>
    </svg>
    <div v-if="hover != null" class="tip" :style="{ left: tipLeft + 'px' }">
      <div class="tip-title">{{ formatLabel(labels[hover]) }}</div>
      <div v-for="(s, i) in series" :key="i" class="tip-row"><span class="sw" :style="{ background: colorOf(i) }" />{{ s.label }}<b>{{ format(s.values[hover] ?? 0) }}</b></div>
    </div>
    <div class="legend">
      <span v-for="(s, i) in series" :key="i"><span class="sw" :style="{ background: colorOf(i) }" />{{ s.label }}</span>
    </div>
  </div>
</template>

<style scoped>
.pulse { position: relative; width: 100%; padding-bottom: 1.4rem; }
svg { display: block; overflow: visible; touch-action: none; }
.grid line { stroke: var(--grid); stroke-dasharray: 3 4; }
.axis text { font-size: 11px; fill: var(--muted); font-variant-numeric: tabular-nums; }
.cross { stroke: var(--axis); stroke-dasharray: 4 3; }
.empty { fill: var(--muted); font-size: 13px; }
.area { animation: fade-in 0.9s ease both; }
.draw { stroke-dasharray: 1; stroke-dashoffset: 1; animation: draw 1.4s cubic-bezier(.4, 0, .2, 1) forwards; }
.breath { opacity: 0.15; animation: breathe 3.2s ease-in-out infinite; }
.beat { transform-box: fill-box; transform-origin: center; animation: beat 2s ease-out infinite; }
@keyframes draw { to { stroke-dashoffset: 0; } }
@keyframes fade-in { from { opacity: 0; } to { opacity: 1; } }
@keyframes breathe { 0%, 100% { opacity: 0.08; } 50% { opacity: 0.45; } }
@keyframes beat { 0% { transform: scale(0.5); opacity: 0.55; } 100% { transform: scale(2.2); opacity: 0; } }
@media (prefers-reduced-motion: reduce) { .draw, .breath, .beat, .area { animation: none; stroke-dashoffset: 0; } }
.tip { position: absolute; top: 4px; min-width: 150px; background: var(--surface); border: 1px solid var(--border); border-radius: 10px; box-shadow: var(--shadow-md); padding: .45rem .6rem; font-size: .78rem; pointer-events: none; z-index: 2; }
.tip-title { color: var(--muted); margin-bottom: .2rem; }
.tip-row { display: flex; align-items: center; gap: .4rem; }
.tip-row b { margin-left: auto; font-variant-numeric: tabular-nums; }
.legend { position: absolute; bottom: 0; left: 50px; display: flex; gap: .9rem; flex-wrap: wrap; font-size: .76rem; color: var(--text-secondary); }
.legend > span { display: inline-flex; align-items: center; gap: .35rem; }
.sw { width: 10px; height: 10px; border-radius: 3px; display: inline-block; }
</style>
