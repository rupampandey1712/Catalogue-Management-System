<script setup lang="ts">
import { computed } from 'vue'
import { nextId } from './palette'

/** Gradient ring gauge for a percentage (e.g. share of products with an image). */
const props = withDefaults(defineProps<{ value: number; label: string; sub?: string; size?: number; from?: string; to?: string }>(),
  { size: 150, from: '#6366f1', to: '#22d3ee' })
const id = nextId('gauge')
const pct = computed(() => Math.max(0, Math.min(100, props.value)))
const r = computed(() => props.size / 2 - 12)
const circ = computed(() => 2 * Math.PI * r.value)
</script>

<template>
  <div class="gauge" :style="{ width: size + 'px' }">
    <div class="ring" :style="{ width: size + 'px', height: size + 'px' }">
      <svg :width="size" :height="size" role="img" :aria-label="`${label}: ${pct.toFixed(1)}%`">
        <defs>
          <linearGradient :id="id" x1="0" y1="0" x2="1" y2="1"><stop offset="0%" :stop-color="from" /><stop offset="100%" :stop-color="to" /></linearGradient>
        </defs>
        <circle :cx="size / 2" :cy="size / 2" :r="r" class="track" stroke-width="12" fill="none" />
        <circle :cx="size / 2" :cy="size / 2" :r="r" :stroke="`url(#${id})`" stroke-width="12" fill="none" stroke-linecap="round"
          :stroke-dasharray="`${(pct / 100) * circ} ${circ}`" :transform="`rotate(-90 ${size / 2} ${size / 2})`" class="fill" />
      </svg>
      <div class="center"><b>{{ pct < 10 ? pct.toFixed(1) : pct.toFixed(0) }}%</b><span>{{ label }}</span></div>
    </div>
    <div v-if="sub" class="sub">{{ sub }}</div>
  </div>
</template>

<style scoped>
.gauge { text-align: center; margin: 0 auto; }
.ring { position: relative; }
svg { display: block; }
.track { stroke: var(--surface-3); }
.fill { animation: fill 1.2s cubic-bezier(.2, .8, .2, 1) both; }
@keyframes fill { from { stroke-dasharray: 0 1000; } }
@media (prefers-reduced-motion: reduce) { .fill { animation: none; } }
.center { position: absolute; inset: 0; display: grid; place-content: center; }
.center b { font-size: 1.5rem; font-variant-numeric: tabular-nums; }
.center span { font-size: .72rem; color: var(--muted); }
.sub { font-size: .76rem; color: var(--muted); margin-top: .3rem; }
</style>
