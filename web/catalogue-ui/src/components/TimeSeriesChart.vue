<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'

export interface Series { name: string; color: string; area?: boolean }

const props = withDefaults(defineProps<{
  /** x values (any monotonically increasing number, e.g. unix seconds) */
  x: number[]
  /** one array per series, aligned with x */
  values: number[][]
  series: Series[]
  height?: number
  formatY?: (v: number) => string
  formatX?: (v: number) => string
  /** label the latest value of each series at the line end */
  directLabels?: boolean
}>(), {
  height: 220,
  formatY: (v: number) => String(Math.round(v)),
  formatX: (v: number) => new Date(v * 1000).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' }),
  directLabels: true
})

const root = ref<HTMLDivElement>()
const width = ref(600)
const hover = ref<number | null>(null)
let ro: ResizeObserver | undefined

onMounted(() => {
  ro = new ResizeObserver(([e]) => { width.value = Math.max(240, e.contentRect.width) })
  if (root.value) ro.observe(root.value)
})
onUnmounted(() => ro?.disconnect())

const pad = { top: 12, right: 64, bottom: 24, left: 48 }
const innerW = computed(() => width.value - pad.left - pad.right)
const innerH = computed(() => props.height - pad.top - pad.bottom)

function niceMax(v: number) {
  if (v <= 0) return 1
  const exp = Math.pow(10, Math.floor(Math.log10(v)))
  const f = v / exp
  return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10) * exp
}
const yMax = computed(() => niceMax(Math.max(0, ...props.values.flat()) * 1.1))
const ticks = computed(() => [0, 0.25, 0.5, 0.75, 1].map(f => f * yMax.value))

const n = computed(() => props.x.length)
const sx = (i: number) => pad.left + (n.value <= 1 ? 0 : (i / (n.value - 1)) * innerW.value)
const sy = (v: number) => pad.top + innerH.value - (v / yMax.value) * innerH.value

const paths = computed(() => props.values.map(vals => {
  if (!vals.length) return { line: '', area: '' }
  const line = vals.map((v, i) => `${i ? 'L' : 'M'}${sx(i).toFixed(1)},${sy(v).toFixed(1)}`).join('')
  const area = `${line}L${sx(vals.length - 1).toFixed(1)},${sy(0)}L${sx(0).toFixed(1)},${sy(0)}Z`
  return { line, area }
}))

const xTicks = computed(() => {
  const count = Math.max(2, Math.min(6, Math.floor(innerW.value / 110)))
  return Array.from({ length: count }, (_, k) => Math.round((k / (count - 1)) * (n.value - 1)))
})

// Direct labels at the right end; nudge apart when they would collide.
const endLabels = computed(() => {
  const labels = props.series.map((s, i) => ({
    s, y: sy(props.values[i]?.[n.value - 1] ?? 0), v: props.values[i]?.[n.value - 1] ?? 0
  })).sort((a, b) => a.y - b.y)
  for (let i = 1; i < labels.length; i++)
    if (labels[i].y - labels[i - 1].y < 14) labels[i].y = labels[i - 1].y + 14
  return labels
})

function onMove(e: PointerEvent) {
  const rect = (e.currentTarget as SVGElement).getBoundingClientRect()
  const px = e.clientX - rect.left
  if (n.value === 0) return
  const i = Math.round(((px - pad.left) / innerW.value) * (n.value - 1))
  hover.value = Math.min(n.value - 1, Math.max(0, i))
}
</script>

<template>
  <div ref="root" class="chart">
    <svg :height="height" :viewBox="`0 0 ${width} ${height}`" role="img"
      :aria-label="`${series.map(s => s.name).join(' and ')} over time`"
      @pointermove="onMove" @pointerleave="hover = null">
      <g>
        <template v-for="t in ticks" :key="t">
          <line class="grid-line" :x1="pad.left" :x2="pad.left + innerW" :y1="sy(t)" :y2="sy(t)" :class="{ baseline: t === 0 }" />
          <text :x="pad.left - 8" :y="sy(t) + 4" text-anchor="end">{{ formatY(t) }}</text>
        </template>
        <text v-for="i in xTicks" :key="'x' + i" :x="sx(i)" :y="height - 6"
          :text-anchor="i === 0 ? 'start' : i === n - 1 ? 'end' : 'middle'">{{ n ? formatX(x[i]) : '' }}</text>
      </g>
      <g v-for="(p, i) in paths" :key="series[i].name">
        <path v-if="series[i].area" :d="p.area" :fill="series[i].color" opacity="0.1" />
        <path :d="p.line" fill="none" :stroke="series[i].color" stroke-width="2" stroke-linejoin="round" stroke-linecap="round" />
      </g>
      <g v-if="directLabels && n">
        <g v-for="l in endLabels" :key="l.s.name">
          <circle :cx="sx(n - 1)" :cy="sy(l.v)" r="4" :fill="l.s.color" stroke="var(--surface)" stroke-width="2" />
          <text :x="sx(n - 1) + 9" :y="l.y + 4" style="fill: var(--text-secondary); font-weight: 600">{{ formatY(l.v) }}</text>
        </g>
      </g>
      <g v-if="hover !== null">
        <line :x1="sx(hover)" :x2="sx(hover)" :y1="pad.top" :y2="pad.top + innerH" stroke="var(--axis)" stroke-width="1" />
        <circle v-for="(s, i) in series" :key="s.name" :cx="sx(hover)" :cy="sy(values[i][hover] ?? 0)" r="4.5"
          :fill="s.color" stroke="var(--surface)" stroke-width="2" />
      </g>
      <rect :x="pad.left" :y="pad.top" :width="innerW" :height="innerH" fill="transparent" />
    </svg>
    <div v-if="hover !== null" class="chart-tooltip"
      :style="{ left: `${Math.min(width - 90, Math.max(90, sx(hover)))}px`, top: `${pad.top + 8}px`, transform: 'translate(-50%, 0)' }">
      <div class="tt-title">{{ formatX(x[hover]) }}</div>
      <div v-for="(s, i) in series" :key="s.name" class="tt-row">
        <span class="swatch line" :style="{ background: s.color }" />{{ s.name }}<b>{{ formatY(values[i][hover] ?? 0) }}</b>
      </div>
    </div>
  </div>
</template>
