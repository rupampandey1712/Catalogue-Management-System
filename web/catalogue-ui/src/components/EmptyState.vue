<script setup lang="ts">
import Icon from './Icon.vue'
withDefaults(defineProps<{ icon?: string; title: string; text?: string }>(), { icon: 'inbox' })
</script>

<template>
  <div class="empty-state">
    <span class="empty-icon"><Icon :name="icon" :size="22" /></span>
    <span class="empty-title">{{ title }}</span>
    <span v-if="text">{{ text }}</span>
    <div v-if="$slots.default" class="mt-1"><slot /></div>
  </div>
</template>
