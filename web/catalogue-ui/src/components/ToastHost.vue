<script setup lang="ts">
import Icon from './Icon.vue'
import { dismiss, toasts } from '../ui'

const icons = { good: 'check', bad: 'alertCircle', info: 'info' } as const
</script>

<template>
  <div class="toast-host" aria-live="polite">
    <div v-for="t in toasts" :key="t.id" class="toast" :class="t.kind" role="status">
      <span class="toast-icon"><Icon :name="icons[t.kind]" :size="15" /></span>
      <div style="min-width: 0">
        <div class="toast-title">{{ t.title }}</div>
        <div v-if="t.message" class="toast-msg">{{ t.message }}</div>
      </div>
      <button class="toast-close" aria-label="Dismiss" @click="dismiss(t.id)"><Icon name="x" :size="14" /></button>
    </div>
  </div>
</template>
