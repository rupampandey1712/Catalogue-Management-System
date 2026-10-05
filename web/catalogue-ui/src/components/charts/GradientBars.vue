<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { compact, gradientFor, humanize, nextId, niceMax } from './palette'

/** Gradient bars that grow in. Horizontal for rankings (long labels), vertical for time buckets. */
export interface Bar { label: string; value: number }

const props = withDefaults(defineProps<{
  bars: Bar[]
  horizontal?: boolean
  height?: number
  format?: (v: number) => string
  formatLabel?: (s: string) => string
  /** one gradient for all bars (time series) instead of one per bar (rankings) */
  single?: boolean
  gradient?: [string, string]
  max?: number
}>(), { horizontal: false, height: 240, format: (v: number) => compact(v), formatLabel: (s: string) => s, single: false })

const root = ref<HTMLDivElement>()
const width = ref(520)
const hover = ref<number | null>(null)
const id = nextId('bars')
let ro: ResizeObserver | undefined
onMounted(() => {
  ro = new ResizeObserver(([e]) => { width.value = Math.max(240, e.contentRect.width) })
  if (root.value) ro.observe(root.value)
})
onUnmounted(() => ro?.disconnect())

const top = computed(() => props.bars.slice(0, props.max ?? props.bars.length))
const yMax = computed(() => niceMax(Math.max(0, ...top.value.map(b => b.value)) * 1.08))
const grad = (i: number) => props.gradient ?? (props.single ? gradientFor('', 0) : gradientFor(top.value[i].label, i))

// vertical
const vpad = { top: 18, right: 10, bottom: 30, left: 46 }
const innerW = computed(() => width.value - vpad.left - vpad.right)
const innerH = computed(() => props.height - vpad.top - vpad.bottom)
const slot = computed(() => innerW.value / Math.max(1, top.value.length))
const barW = computed(() => Math.max(3, Math.min(46, slot.value * 0.62)))
const labelEvery = computed(() => Math.max(1, Math.ceil(top.value.length / Math.max(2, Math.floor(innerW.value / 70)))))

// horizontal
const rowH = 30
const hHeight = computed(() => Math.max(60, top.value.length * rowH + 8))
const labelW = computed(() => Math.min(180, width.value * 0.38))
const hInner = computed(() => width.value - labelW.value - 70)
const empty = computed(() => top.value.every(b => !b.value))
</script>

<template>
  <div ref="root" class="bars">
    <svg v-if="!horizontal" :width="width" :height="height" role="img" aria-label="Bar chart">
      <defs>
        <linearGradient v-for="(b, i) in top" :id="`${id}-${i}`" :key="i" x1="0" y1="1" x2="0" y2="0">
          <stop offset="0%" :stop-color="grad(i)[0]" /><stop offset="100%" :stop-color="grad(i)[1]" />
        </linearGradient>
      </defs>
      <g class="grid">
        <line v-for="f in [0, 0.25, 0.5, 0.75, 1]" :key="f" :x1="vpad.left" :x2="width - vpad.right" :y1="vpad.top + innerH - f * innerH" :y2="vpad.top + innerH - f * innerH" />
      </g>
      <g class="axis">
        <text v-for="f in [0, 0.5, 1]" :key="'t' + f" :x="vpad.left - 8" :y="vpad.top + innerH - f * innerH + 4" text-anchor="end">{{ format(f * yMax) }}</text>
      </g>
      <g v-for="(b, i) in top" :key="i" @pointerenter="hover = i" @pointerleave="hover = null">
        <rect :x="vpad.left + i * slot" :y="vpad.top" :width="slot" :height="innerH" fill="transparent" />
        <rect class="grow" :style="{ animationDelay: i * 25 + 'ms' }" :x="vpad.left + i * slot + (slot - barW) / 2" :y="vpad.top + innerH - (b.value / yMax) * innerH"
          :width="barW" :height="Math.max(b.value > 0 ? 2 : 0, (b.value / yMax) * innerH)" :rx="Math.min(6, barW / 3)" :fill="`url(#${id}-${i})`"
          :opacity="hover == null || hover === i ? 1 : 0.45" />
        <text v-if="i % labelEvery === 0" class="axis-x" :x="vpad.left + i * slot + slot / 2" :y="height - 10" text-anchor="middle">{{ formatLabel(b.label) }}</text>
        <text v-if="hover === i" class="val" :x="vpad.left + i * slot + slot / 2" :y="vpad.top + innerH - (b.value / yMax) * innerH - 6" text-anchor="middle">{{ format(b.value) }}</text>
      </g>
      <text v-if="empty" :x="width / 2" :y="height / 2" text-anchor="middle" class="empty">Nothing in this period</text>
    </svg>

    <svg v-else :width="width" :height="hHeight" role="img" aria-label="Ranking">
      <defs>
        <linearGradient v-for="(b, i) in top" :id="`${id}-${i}`" :key="i" x1="0" y1="0" x2="1" y2="0">
          <stop offset="0%" :stop-color="grad(i)[0]" /><stop offset="100%" :stop-color="grad(i)[1]" />
        </linearGradient>
      </defs>
      <g v-for="(b, i) in top" :key="i" :transform="`translate(0, ${i * rowH + 4})`" @pointerenter="hover = i" @pointerleave="hover = null">
        <text class="hlabel" :x="labelW - 10" y="16" text-anchor="end">{{ humanize(b.label).length > 26 ? humanize(b.label).slice(0, 25) + '…' : humanize(b.label) }}<title>{{ b.label }}</title></text>
        <rect :x="labelW" y="4" :width="hInner" height="18" rx="9" class="track" />
        <rect class="grow-x" :style="{ animationDelay: i * 60 + 'ms' }" :x="labelW" y="4" :width="Math.max(b.value > 0 ? 6 : 0, (b.value / yMax) * hInner)" height="18" rx="9"
          :fill="`url(#${id}-${i})`" :opacity="hover == null || hover === i ? 1 : 0.5" />
        <text class="hval" :x="labelW + Math.max(6, (b.value / yMax) * hInner) + 8" y="17">{{ format(b.value) }}</text>
      </g>
      <text v-if="!top.length" :x="width / 2" y="34" text-anchor="middle" class="empty">Nothing to rank yet</text>
    </svg>
  </div>
</template>

<style scoped>
.bars { width: 100%; }
svg { display: block; overflow: visible; }
.grid line { stroke: var(--grid); stroke-dasharray: 3 4; }
.axis text, .axis-x { font-size: 11px; fill: var(--muted); font-variant-numeric: tabular-nums; }
.val, .hval { font-size: 11.5px; fill: var(--text); font-weight: 600; font-variant-numeric: tabular-nums; }
.hlabel { font-size: 12px; fill: var(--text-secondary); }
.track { fill: var(--surface-3); }
.empty { fill: var(--muted); font-size: 13px; }
.grow { transform-box: fill-box; transform-origin: bottom; animation: grow-y .8s cubic-bezier(.2, .8, .2, 1) both; transition: opacity .15s; }
.grow-x { transform-box: fill-box; transform-origin: left; animation: grow-x .9s cubic-bezier(.2, .8, .2, 1) both; transition: opacity .15s; }
@keyframes grow-y { from { transform: scaleY(0); } }
@keyframes grow-x { from { transform: scaleX(0); } }
@media (prefers-reduced-motion: reduce) { .grow, .grow-x { animation: none; } }
</style>
