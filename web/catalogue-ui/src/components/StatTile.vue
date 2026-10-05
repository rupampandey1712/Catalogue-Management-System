<script setup lang="ts">
import Icon from './Icon.vue'

defineProps<{
  label: string
  value: string | number
  icon?: string
  tone?: 'good' | 'warn' | 'bad' | 'info' | ''
  foot?: string
  loading?: boolean
}>()
</script>

<template>
  <div class="stat-card">
    <div class="stat-top">
      <span class="stat-label">{{ label }}</span>
      <span v-if="icon" class="stat-icon" :class="tone"><Icon :name="icon" :size="16" /></span>
    </div>
    <span v-if="loading" class="skeleton" style="height: 1.6rem; width: 60%" />
    <span v-else class="stat-value">{{ value }}</span>
    <span v-if="foot || $slots.foot" class="stat-foot"><slot name="foot">{{ foot }}</slot></span>
  </div>
</template>
