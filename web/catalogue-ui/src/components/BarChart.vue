<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'

export interface Bar { label: string; value: number; detail?: string }

const props = withDefaults(defineProps<{
  bars: Bar[]
  color?: string
  height?: number
  name?: string
  formatY?: (v: number) => string
}>(), { color: 'var(--series-1)', height: 200, name: 'Value', formatY: (v: number) => String(Math.round(v)) })

const root = ref<HTMLDivElement>()
const width = ref(600)
const hover = ref<number | null>(null)
let ro: ResizeObserver | undefined
onMounted(() => {
  ro = new ResizeObserver(([e]) => { width.value = Math.max(240, e.contentRect.width) })
  if (root.value) ro.observe(root.value)
})
onUnmounted(() => ro?.disconnect())

const pad = { top: 10, right: 8, bottom: 24, left: 48 }
const innerW = computed(() => width.value - pad.left - pad.right)
const innerH = computed(() => props.height - pad.top - pad.bottom)
const yMax = computed(() => {
  const v = Math.max(0, ...props.bars.map(b => b.value)) * 1.1
  if (v <= 0) return 1
  const exp = Math.pow(10, Math.floor(Math.log10(v)))
  const f = v / exp
  return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10) * exp
})
const band = computed(() => innerW.value / Math.max(1, props.bars.length))
const barW = computed(() => Math.min(24, band.value * 0.6))
const sy = (v: number) => pad.top + innerH.value - (v / yMax.value) * innerH.value

/** Column with a 4px rounded data end, square at the baseline. */
function barPath(i: number, v: number) {
  const x = pad.left + i * band.value + (band.value - barW.value) / 2
  const y = sy(v), b = sy(0), w = barW.value
  const r = Math.min(4, w / 2, Math.max(0, b - y))
  if (b - y <= 0) return ''
  return `M${x},${b}V${y + r}Q${x},${y} ${x + r},${y}H${x + w - r}Q${x + w},${y} ${x + w},${y + r}V${b}Z`
}
const labelEvery = computed(() => Math.ceil(props.bars.length / Math.max(2, Math.floor(innerW.value / 56))))
</script>

<template>
  <div ref="root" class="chart">
    <svg :height="height" :viewBox="`0 0 ${width} ${height}`" role="img" :aria-label="name">
      <template v-for="f in [0, 0.5, 1]" :key="f">
        <line class="grid-line" :class="{ baseline: f === 0 }" :x1="pad.left" :x2="pad.left + innerW" :y1="sy(f * yMax)" :y2="sy(f * yMax)" />
        <text :x="pad.left - 8" :y="sy(f * yMax) + 4" text-anchor="end">{{ formatY(f * yMax) }}</text>
      </template>
      <g v-for="(b, i) in bars" :key="b.label">
        <!-- hit target spans the whole band, larger than the mark -->
        <rect :x="pad.left + i * band" :y="pad.top" :width="band" :height="innerH" fill="transparent"
          @pointerenter="hover = i" @pointerleave="hover = null" />
        <path :d="barPath(i, b.value)" :fill="color" :opacity="hover === null || hover === i ? 1 : 0.45" style="pointer-events: none; transition: opacity 120ms" />
        <text v-if="i % labelEvery === 0" :x="pad.left + i * band + band / 2" :y="height - 6" text-anchor="middle">{{ b.label }}</text>
      </g>
    </svg>
    <div v-if="hover !== null" class="chart-tooltip"
      :style="{ left: `${Math.min(width - 80, Math.max(80, pad.left + hover * band + band / 2))}px`, top: `${sy(bars[hover].value)}px` }">
      <div class="tt-title">{{ bars[hover].label }}</div>
      <div class="tt-row"><span class="swatch" :style="{ background: color }" />{{ name }}<b>{{ formatY(bars[hover].value) }}</b></div>
      <div v-if="bars[hover].detail" class="tt-row" style="color: var(--muted)">{{ bars[hover].detail }}</div>
    </div>
  </div>
</template>
