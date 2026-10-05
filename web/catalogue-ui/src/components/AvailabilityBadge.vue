<script setup lang="ts">
import { computed } from 'vue'

const props = defineProps<{ value?: string; leadTimeDays?: number | null }>()

const meta: Record<string, { label: string; tone: string }> = {
  InStock: { label: 'In stock', tone: 'good' },
  LowStock: { label: 'Low stock', tone: 'warn' },
  OnOrder: { label: 'On order', tone: 'info' },
  Discontinued: { label: 'Discontinued', tone: 'bad' },
  Unknown: { label: 'Unknown', tone: '' }
}
const m = computed(() => meta[props.value ?? 'Unknown'] ?? meta.Unknown)
</script>

<template>
  <span class="badge" :class="m.tone" :title="leadTimeDays != null ? `Lead time ${leadTimeDays} day(s)` : undefined">
    <span class="dot" />{{ m.label }}<template v-if="value === 'OnOrder' && leadTimeDays != null"> · {{ leadTimeDays }}d</template>
  </span>
</template>
