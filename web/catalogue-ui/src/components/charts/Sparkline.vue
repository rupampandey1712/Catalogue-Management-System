<script setup lang="ts">
import { computed } from 'vue'
import { nextId, smoothPath } from './palette'

/** Tiny gradient area for KPI tiles. */
const props = withDefaults(defineProps<{ values: number[]; color?: string; width?: number; height?: number }>(),
  { color: 'var(--series-1)', width: 120, height: 34 })
const id = nextId('spark')
const paths = computed(() => {
  const v = props.values
  if (v.length < 2) return null
  const max = Math.max(...v), min = Math.min(0, ...v)
  const span = max - min || 1
  const pts = v.map((x, i) => [(i / (v.length - 1)) * props.width, props.height - 2 - ((x - min) / span) * (props.height - 4)] as [number, number])
  const line = smoothPath(pts)
  return { line, area: `${line}L${props.width},${props.height}L0,${props.height}Z` }
})
</script>

<template>
  <svg v-if="paths" :width="width" :height="height" class="spark" aria-hidden="true">
    <defs>
      <linearGradient :id="id" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0%" :style="{ stopColor: color, stopOpacity: 0.35 }" /><stop offset="100%" :style="{ stopColor: color, stopOpacity: 0 }" />
      </linearGradient>
    </defs>
    <path :d="paths.area" :fill="`url(#${id})`" />
    <path :d="paths.line" fill="none" :stroke="color" stroke-width="1.8" stroke-linecap="round" />
  </svg>
</template>

<style scoped>
.spark { display: block; overflow: visible; }
</style>
