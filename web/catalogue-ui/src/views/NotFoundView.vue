<script setup lang="ts">
import EmptyState from '../components/EmptyState.vue'
</script>

<template>
  <div class="card" style="max-width: 560px; margin: 3rem auto">
    <EmptyState icon="search" title="Page not found" text="The page you were looking for does not exist or you do not have access to it.">
      <router-link to="/dashboard" class="btn btn-primary btn-sm">Go to dashboard</router-link>
    </EmptyState>
  </div>
</template>
