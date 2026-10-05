<script setup lang="ts">
import { computed, ref } from 'vue'
import { compact, gradientFor, humanize, nextId } from './palette'

/** Donut (or full pie) with gradient slices that sweep in, a centre total and a legend with shares. */
export interface Slice { label: string; value: number }

const props = withDefaults(defineProps<{
  slices: Slice[]
  pie?: boolean
  size?: number
  centerLabel?: string
  format?: (v: number) => string
}>(), { pie: false, size: 180, centerLabel: 'total', format: (v: number) => compact(v) })

const id = nextId('donut')
const hover = ref<number | null>(null)
const data = computed(() => props.slices.filter(s => s.value > 0).sort((a, b) => b.value - a.value))
const total = computed(() => data.value.reduce((s, x) => s + x.value, 0))
const r = computed(() => props.size / 2 - 6)
const inner = computed(() => (props.pie ? 0 : r.value * 0.62))
const c = computed(() => props.size / 2)

function arc(start: number, end: number, ro: number, ri: number) {
  const large = end - start > Math.PI ? 1 : 0
  const p = (a: number, rad: number) => [c.value + rad * Math.sin(a), c.value - rad * Math.cos(a)].map(v => v.toFixed(2)).join(',')
  if (end - start >= Math.PI * 2 - 1e-6) end = start + Math.PI * 2 - 1e-4 // full circle
  return ri > 0
    ? `M${p(start, ro)}A${ro},${ro} 0 ${large} 1 ${p(end, ro)}L${p(end, ri)}A${ri},${ri} 0 ${large} 0 ${p(start, ri)}Z`
    : `M${c.value},${c.value}L${p(start, ro)}A${ro},${ro} 0 ${large} 1 ${p(end, ro)}Z`
}

const segments = computed(() => {
  let a = 0
  return data.value.map((s, i) => {
    const sweep = total.value ? (s.value / total.value) * Math.PI * 2 : 0
    const gap = data.value.length > 1 && !props.pie ? 0.012 : 0
    const seg = { s, i, d: arc(a + gap, a + sweep - gap, r.value + (hover.value === i ? 4 : 0), inner.value), share: total.value ? s.value / total.value : 0, mid: a + sweep / 2 }
    a += sweep
    return seg
  })
})
const shown = computed(() => (hover.value != null ? data.value[hover.value] : null))
</script>

<template>
  <div class="donut">
    <div class="chart" :style="{ width: size + 'px', height: size + 'px' }">
      <svg :width="size" :height="size" role="img" :aria-label="data.map(d => `${d.label} ${d.value}`).join(', ')" class="sweep">
        <defs>
          <linearGradient v-for="(s, i) in data" :id="`${id}-${i}`" :key="i" x1="0" y1="0" x2="1" y2="1">
            <stop offset="0%" :stop-color="gradientFor(s.label, i)[0]" /><stop offset="100%" :stop-color="gradientFor(s.label, i)[1]" />
          </linearGradient>
        </defs>
        <circle v-if="!data.length" :cx="c" :cy="c" :r="r" class="placeholder" :stroke-width="pie ? 0 : r - inner" :fill="pie ? 'var(--surface-3)' : 'none'" />
        <path v-for="seg in segments" :key="seg.i" :d="seg.d" :fill="`url(#${id}-${seg.i})`" class="seg"
          :opacity="hover == null || hover === seg.i ? 1 : 0.5" @pointerenter="hover = seg.i" @pointerleave="hover = null">
          <title>{{ humanize(seg.s.label) }}: {{ format(seg.s.value) }} ({{ (seg.share * 100).toFixed(1) }}%)</title>
        </path>
      </svg>
      <div v-if="!pie" class="center">
        <b>{{ shown ? format(shown.value) : format(total) }}</b>
        <span>{{ shown ? humanize(shown.label) : centerLabel }}</span>
      </div>
    </div>
    <ul class="legend">
      <li v-for="seg in segments" :key="seg.i" :class="{ dim: hover != null && hover !== seg.i }" @pointerenter="hover = seg.i" @pointerleave="hover = null">
        <span class="sw" :style="{ background: `linear-gradient(135deg, ${gradientFor(seg.s.label, seg.i)[0]}, ${gradientFor(seg.s.label, seg.i)[1]})` }" />
        <span class="name">{{ humanize(seg.s.label) }}</span>
        <span class="v">{{ format(seg.s.value) }}</span>
        <span class="p">{{ (seg.share * 100).toFixed(seg.share < 0.1 ? 1 : 0) }}%</span>
      </li>
      <li v-if="!data.length" class="none">No data in this period</li>
    </ul>
  </div>
</template>

<style scoped>
.donut { display: flex; align-items: center; gap: 1.2rem; flex-wrap: wrap; }
.chart { position: relative; flex: none; }
svg { display: block; overflow: visible; }
.sweep { animation: sweep 1s cubic-bezier(.2, .8, .2, 1) both; }
@keyframes sweep { from { transform: rotate(-90deg) scale(.6); opacity: 0; } to { transform: none; opacity: 1; } }
@media (prefers-reduced-motion: reduce) { .sweep { animation: none; } }
.seg { transition: opacity .15s, d .2s; cursor: default; }
.placeholder { stroke: var(--surface-3); }
.center { position: absolute; inset: 0; display: grid; place-content: center; text-align: center; pointer-events: none; }
.center b { font-size: 1.35rem; font-variant-numeric: tabular-nums; }
.center span { font-size: .72rem; color: var(--muted); max-width: 90px; }
.legend { list-style: none; margin: 0; padding: 0; flex: 1; min-width: 150px; display: grid; gap: .3rem; font-size: .8rem; }
.legend li { display: grid; grid-template-columns: 12px 1fr auto 3.2em; align-items: center; gap: .5rem; transition: opacity .15s; }
.legend li.dim { opacity: .45; }
.legend .sw { width: 12px; height: 12px; border-radius: 4px; }
.legend .name { color: var(--text-secondary); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.legend .v { font-weight: 600; font-variant-numeric: tabular-nums; }
.legend .p { color: var(--muted); text-align: right; font-variant-numeric: tabular-nums; }
.legend .none { display: block; color: var(--muted); }
</style>
