<script setup lang="ts">
import { computed } from 'vue'
import { statusLabel, statusTone } from '../ui'

const props = defineProps<{ status: string; label?: string }>()
const tone = computed(() => statusTone(props.status))
const live = computed(() => ['Processing', 'Publishing', 'Received'].includes(props.status))
</script>

<template>
  <span class="badge" :class="[tone, { live }]">
    <span class="dot" />{{ label ?? statusLabel(status) }}
  </span>
</template>
