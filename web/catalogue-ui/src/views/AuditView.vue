<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import Icon from '../components/Icon.vue'
import Pager from '../components/Pager.vue'
import EmptyState from '../components/EmptyState.vue'
import { api, errorText, type AuditEvent, type Paged } from '../api'
import { fmt, toast } from '../ui'

const items = ref<AuditEvent[]>([])
const total = ref(0)
const loading = ref(true)
const actions = ref<string[]>([])
const action = ref('')
const entityType = ref('')
const search = ref('')
const page = ref(1)
const pageSize = ref(25)
const expanded = ref<number | null>(null)

async function load() {
  loading.value = true
  try {
    const { data } = await api.get<Paged<AuditEvent>>('/audit', {
      params: { action: action.value || undefined, entityType: entityType.value || undefined, search: search.value || undefined, page: page.value, pageSize: pageSize.value }
    })
    items.value = data.items
    total.value = data.total
  } catch (e) {
    toast('bad', 'Failed to load audit log', errorText(e))
  } finally {
    loading.value = false
  }
}
let debounce: ReturnType<typeof setTimeout>
watch([action, entityType], () => { page.value = 1; load() })
watch(search, () => { clearTimeout(debounce); debounce = setTimeout(() => { page.value = 1; load() }, 300) })
watch([page, pageSize], load)
onMounted(async () => {
  load()
  actions.value = (await api.get<string[]>('/audit/actions')).data
})

const tone = (a: string) =>
  /Failed|Deactivated|Rejected/.test(a) ? 'bad' : /Created|Completed|Activated|Generated/.test(a) ? 'good' : /Reprocess|Restart/.test(a) ? 'warn' : 'info'
const pretty = (a: string) => a.replace(/([a-z])([A-Z])/g, '$1 $2')
</script>

<template>
  <div class="page-header">
    <div class="page-title-group">
      <h1 class="page-title">Audit log</h1>
      <span class="page-subtitle">Every change to products, suppliers and imports — who, what and with which correlation ID.</span>
    </div>
  </div>

  <div class="card">
    <div class="toolbar">
      <div class="input-icon" style="flex: 1; max-width: 360px"><span class="icon"><Icon name="search" :size="15" /></span><input v-model="search" placeholder="Actor, entity, details or correlation ID…" /></div>
      <select v-model="action" aria-label="Action"><option value="">All actions</option><option v-for="a in actions" :key="a" :value="a">{{ pretty(a) }}</option></select>
      <select v-model="entityType" aria-label="Entity">
        <option value="">All entities</option><option>Product</option><option>Supplier</option><option>Category</option><option>ImportFile</option><option>EdiSource</option>
      </select>
      <span class="spacer" />
      <button class="btn-ghost btn-sm" aria-label="Refresh" @click="load"><Icon name="refresh" :size="14" :class="{ spin: loading }" /></button>
    </div>
    <div class="table-wrap">
      <table>
        <thead><tr><th>When</th><th>Action</th><th>Actor</th><th>Entity</th><th>Details</th><th>Correlation</th></tr></thead>
        <tbody>
          <template v-if="loading && !items.length"><tr v-for="i in 8" :key="i"><td v-for="j in 6" :key="j"><span class="skeleton" /></td></tr></template>
          <tr v-for="a in items" :key="a.id" class="clickable" @click="expanded = expanded === a.id ? null : a.id">
            <td style="white-space: nowrap"><div class="cell-title"><span>{{ fmt.ago(a.timestampUtc) }}</span><small>{{ fmt.date(a.timestampUtc) }}</small></div></td>
            <td><span class="badge" :class="tone(a.action)"><span class="dot" />{{ pretty(a.action) }}</span></td>
            <td><span class="row" style="gap: .4rem"><span class="avatar" style="width: 22px; height: 22px; font-size: .6rem">{{ a.actorId.slice(0, 2).toUpperCase() }}</span>{{ a.actorId }}</span></td>
            <td><span class="tag">{{ a.entityType }}</span> <span class="mono">{{ a.entityId }}</span></td>
            <td :class="{ 'cell-truncate': expanded !== a.id }" class="cell-muted" style="max-width: 320px">{{ a.details ?? '—' }}</td>
            <td><code v-if="a.correlationId" style="font-size: .72rem">{{ expanded === a.id ? a.correlationId : a.correlationId.slice(0, 8) + '…' }}</code></td>
          </tr>
          <tr v-if="!loading && !items.length"><td colspan="6"><EmptyState icon="shield" title="No audit events match" /></td></tr>
        </tbody>
      </table>
    </div>
    <Pager v-model:page="page" v-model:page-size="pageSize" :total="total" noun="events" />
  </div>
</template>
