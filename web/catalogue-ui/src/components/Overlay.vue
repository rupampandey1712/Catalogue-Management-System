<script setup lang="ts">
import { onMounted, onUnmounted } from 'vue'
import Icon from './Icon.vue'

withDefaults(defineProps<{ title: string; subtitle?: string; kind?: 'modal' | 'drawer' }>(), { kind: 'modal' })
const emit = defineEmits<{ close: [] }>()

const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') emit('close') }
onMounted(() => window.addEventListener('keydown', onKey))
onUnmounted(() => window.removeEventListener('keydown', onKey))
</script>

<template>
  <Teleport to="body">
    <div class="overlay" @mousedown.self="emit('close')">
      <div :class="kind" role="dialog" aria-modal="true" :aria-label="title">
        <div :class="kind === 'modal' ? 'modal-head' : 'drawer-head'">
          <div>
            <h3 style="font-size: 1.05rem">{{ title }}</h3>
            <div v-if="subtitle || $slots.subtitle" class="page-subtitle" style="margin-top: .2rem"><slot name="subtitle">{{ subtitle }}</slot></div>
          </div>
          <button class="icon-btn" aria-label="Close" @click="emit('close')"><Icon name="x" :size="16" /></button>
        </div>
        <div :class="kind === 'modal' ? 'modal-body' : 'drawer-body'"><slot /></div>
        <div v-if="$slots.footer" class="modal-foot"><slot name="footer" /></div>
      </div>
    </div>
  </Teleport>
</template>
