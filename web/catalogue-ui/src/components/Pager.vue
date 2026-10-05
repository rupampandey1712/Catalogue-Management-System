<script setup lang="ts">
import { computed } from 'vue'
import Icon from './Icon.vue'
import { fmt } from '../ui'

const props = withDefaults(defineProps<{
  page: number; pageSize: number; total: number; noun?: string; sizes?: number[]
}>(), { noun: 'items', sizes: () => [10, 25, 50, 100] })
const emit = defineEmits<{ 'update:page': [number]; 'update:pageSize': [number] }>()

const pages = computed(() => Math.max(1, Math.ceil(props.total / props.pageSize)))
const from = computed(() => (props.total === 0 ? 0 : (props.page - 1) * props.pageSize + 1))
const to = computed(() => Math.min(props.total, props.page * props.pageSize))

// Compact page list: 1 … 4 5 [6] 7 8 … 400000
const items = computed<(number | '…')[]>(() => {
  const n = pages.value, p = props.page
  if (n <= 7) return Array.from({ length: n }, (_, i) => i + 1)
  const out: (number | '…')[] = [1]
  const start = Math.max(2, p - 1), end = Math.min(n - 1, p + 1)
  if (start > 2) out.push('…')
  for (let i = start; i <= end; i++) out.push(i)
  if (end < n - 1) out.push('…')
  out.push(n)
  return out
})

function go(p: number) {
  emit('update:page', Math.min(pages.value, Math.max(1, p)))
}
</script>

<template>
  <div class="pagination">
    <div class="row">
      <span class="pagination-info">
        {{ fmt.n(from) }}–{{ fmt.n(to) }} of <b>{{ fmt.n(total) }}</b> {{ noun }}
      </span>
      <select :value="pageSize" class="btn-sm" style="padding: .25rem .5rem; font-size: .8rem"
        aria-label="Rows per page" @change="emit('update:pageSize', Number(($event.target as HTMLSelectElement).value))">
        <option v-for="s in sizes" :key="s" :value="s">{{ s }} / page</option>
      </select>
    </div>
    <div class="pager">
      <button :disabled="page <= 1" aria-label="Previous page" @click="go(page - 1)"><Icon name="chevronLeft" :size="16" /></button>
      <template v-for="(it, i) in items" :key="i">
        <span v-if="it === '…'" class="gap">…</span>
        <button v-else :class="{ active: it === page }" @click="go(it)">{{ fmt.n(it) }}</button>
      </template>
      <button :disabled="page >= pages" aria-label="Next page" @click="go(page + 1)"><Icon name="chevronRight" :size="16" /></button>
    </div>
  </div>
</template>
