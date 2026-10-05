<script setup lang="ts">
import { computed, ref } from 'vue'
import Icon from '../components/Icon.vue'
import StatTile from '../components/StatTile.vue'
import StatusBadge from '../components/StatusBadge.vue'
import BarChart from '../components/BarChart.vue'
import TimeSeriesChart from '../components/TimeSeriesChart.vue'
import EmptyState from '../components/EmptyState.vue'
import { api, errorText, type Dashboard, type HubOverview } from '../api'
import { session } from '../session'
import { fmt, statusLabel, usePolling } from '../ui'

const data = ref<Dashboard | null>(null)
const hub = ref<HubOverview | null>(null)
const error = ref('')

usePolling(async () => {
  try {
    data.value = (await api.get<Dashboard>('/dashboard')).data
    error.value = ''
  } catch (e) { error.value = errorText(e, 'Failed to load dashboard.') }
}, 15000)

usePolling(async () => {
  if (!session.can('importOps')) return
  hub.value = (await api.get<HubOverview>('/eventhub/overview')).data
}, 3000)

const greeting = computed(() => {
  const h = new Date().getHours()
  return h < 12 ? 'Good morning' : h < 18 ? 'Good afternoon' : 'Good evening'
})

const statusOrder = ['Completed', 'CompletedWithErrors', 'Processing', 'Received', 'Failed', 'Rejected']
const statusColor: Record<string, string> = {
  Completed: 'var(--good)', CompletedWithErrors: 'var(--warn)', Processing: 'var(--info)', Received: 'var(--info)',
  Failed: 'var(--bad)', Rejected: 'var(--bad)'
}
const importBreakdown = computed(() => {
  const by = data.value?.imports.byStatus ?? {}
  const total = Object.values(by).reduce((a, b) => a + b, 0)
  return statusOrder.filter(s => by[s]).map(s => ({ status: s, count: by[s], pct: total ? (by[s] / total) * 100 : 0 }))
})

const dailyBars = computed(() => (data.value?.daily ?? []).map(d => ({
  label: new Date(d.day).toLocaleDateString(undefined, { day: 'numeric', month: 'short' }),
  value: d.rows,
  detail: `${fmt.n(d.imports)} imports · ${fmt.n(d.errors)} row errors`
})))

const lastMinutes = computed(() => (hub.value?.timeline ?? []).slice(-90))
const maxCat = computed(() => Math.max(1, ...(data.value?.topCategories ?? []).map(c => c.count)))

const actionIcon = (a: string) =>
  a.includes('Import') || a.includes('Stream') ? 'upload' : a.includes('Product') ? 'box' : a.includes('Supplier') ? 'truck' : a.includes('Edi') ? 'file' : 'activity'
</script>

<template>
  <div class="page-header">
    <div class="page-title-group">
      <h1 class="page-title">{{ greeting }}, {{ session.user?.username }}</h1>
      <span class="page-subtitle">Here is what is happening across your catalogue right now.</span>
    </div>
    <div class="page-actions">
      <router-link v-if="session.can('importOps')" to="/event-hub" class="btn btn-secondary"><Icon name="zap" :size="15" /> Event Hub</router-link>
      <router-link v-if="session.can('supplierAdmin')" to="/imports" class="btn btn-primary"><Icon name="upload" :size="15" /> Import data</router-link>
    </div>
  </div>

  <div v-if="error" class="alert bad" style="margin-bottom: 1rem"><Icon name="alertCircle" :size="16" />{{ error }}</div>
  <div v-if="data?.pendingApprovals && session.can('importOps')" class="alert warn" style="margin-bottom: 1rem">
    <Icon name="clock" :size="16" />
    <span><b>{{ fmt.n(data.pendingApprovals) }} supplier change(s) awaiting approval</b>
      — {{ fmt.n(data.pendingProducts) }} new product(s) are hidden from buyers until reviewed.</span>
    <router-link to="/approvals" class="btn btn-secondary btn-sm" style="margin-left: auto">Review</router-link>
  </div>

  <div class="stat-grid">
    <StatTile label="Products" icon="box" :value="fmt.compact(data?.products.total)" :loading="!data">
      <template #foot><span class="up">{{ fmt.n(data?.products.active) }}</span> active · {{ fmt.n(data?.products.inactive) }} inactive</template>
    </StatTile>
    <StatTile label="Suppliers" icon="truck" tone="info" :value="fmt.n(data?.suppliers.total)" :loading="!data"
      :foot="`${fmt.compact(data?.multiSourcedProducts)} products offered by 2+ suppliers`" />
    <StatTile label="Rows ingested (14 days)" icon="database" tone="good" :value="fmt.compact(data?.imports.rowsLast14Days)" :loading="!data"
      :foot="`${fmt.n(data?.imports.errorsLast14Days)} rows rejected by validation`" />
    <StatTile v-if="session.can('importOps')" label="Live throughput" icon="zap" tone="warn" :value="`${fmt.compact(hub?.rates.rowsPerSec ?? 0)}/s`" :loading="!hub">
      <template #foot>
        <span v-if="hub && hub.totalLag > 0">{{ fmt.n(hub.totalLag) }} events in backlog</span>
        <span v-else>Peak {{ fmt.compact(hub?.rates.peakRowsPerSec ?? 0) }}/s in last 3 min</span>
      </template>
    </StatTile>
    <StatTile v-else label="Categories" icon="tag" tone="warn" :value="fmt.n(data?.categories)" :loading="!data" />
  </div>

  <div class="grid grid-main-side">
    <div class="stack">
      <div class="card">
        <div class="card-header flush">
          <div>
            <h3 class="card-title">Rows ingested per day</h3>
            <div class="card-sub">Successfully processed rows across all imports and streams</div>
          </div>
        </div>
        <div class="card-body" style="padding-top: .5rem">
          <BarChart v-if="data" :bars="dailyBars" name="Rows processed" :format-y="fmt.compact" :height="220" />
          <span v-else class="skeleton" style="height: 220px" />
        </div>
      </div>

      <div v-if="session.can('importOps')" class="card">
        <div class="card-header flush">
          <div>
            <h3 class="card-title">Event Hub ingestion — rows per second</h3>
            <div class="card-sub">Last 90 seconds · {{ hub?.hub.mode === 'EventHubs' ? 'Azure Event Hubs (emulator)' : 'In-memory hub' }} · {{ hub?.hub.partitionCount ?? '–' }} partitions</div>
          </div>
          <router-link to="/event-hub" class="btn btn-ghost btn-sm">Open console <Icon name="arrowRight" :size="14" /></router-link>
        </div>
        <div class="card-body" style="padding-top: .5rem">
          <TimeSeriesChart :x="lastMinutes.map(p => p.t)" :values="[lastMinutes.map(p => p.rowsOut)]"
            :series="[{ name: 'Rows processed / s', color: 'var(--series-1)', area: true }]" :format-y="fmt.compact" :height="180" />
        </div>
      </div>

      <div class="card">
        <div class="card-header">
          <h3 class="card-title">Recent imports</h3>
          <router-link v-if="session.can('importOps')" to="/imports" class="btn btn-ghost btn-sm">View all <Icon name="arrowRight" :size="14" /></router-link>
        </div>
        <div class="table-wrap">
          <table>
            <thead><tr><th>Import</th><th>Supplier</th><th>Status</th><th style="width: 28%">Progress</th><th class="num">Rows</th></tr></thead>
            <tbody>
              <tr v-for="i in data?.recentImports ?? []" :key="i.id">
                <td>
                  <div class="cell-title">
                    <router-link :to="`/imports/${i.id}`" class="cell-strong">{{ i.fileName }}</router-link>
                    <small>#{{ i.id }} · {{ i.format }} · {{ fmt.ago(i.receivedAtUtc) }}</small>
                  </div>
                </td>
                <td><span class="tag">{{ i.supplierCode }}</span></td>
                <td><StatusBadge :status="i.status" /></td>
                <td>
                  <div class="progress sm" :class="i.errorRows ? 'warn' : 'good'">
                    <span :style="{ width: (i.totalRows ? fmt.pct(i.processedRows + i.errorRows, i.totalRows) : (i.status.startsWith('Completed') ? 100 : 0)) + '%' }" />
                  </div>
                </td>
                <td class="num">{{ fmt.compact(i.totalRows) }}</td>
              </tr>
              <tr v-if="data && !data.recentImports.length"><td colspan="5"><EmptyState title="No imports yet" text="Upload a file or stream an EDI interchange to get started." /></td></tr>
            </tbody>
          </table>
        </div>
      </div>
    </div>

    <div class="stack">
      <div class="card">
        <div class="card-header"><h3 class="card-title">Import health</h3><span class="card-sub">{{ fmt.n(data?.imports.total) }} total</span></div>
        <div class="card-body">
          <div class="progress-stack" role="img" aria-label="Imports by status">
            <span v-for="b in importBreakdown" :key="b.status" :style="{ width: b.pct + '%', background: statusColor[b.status] }" :title="`${statusLabel(b.status)}: ${b.count}`" />
          </div>
          <div class="list mt-2" style="margin: .75rem -1.35rem -1.15rem">
            <div v-for="b in importBreakdown" :key="b.status" class="list-item" style="padding-top: .5rem; padding-bottom: .5rem">
              <span class="swatch" :style="{ background: statusColor[b.status] }" />
              <span class="list-main">{{ statusLabel(b.status) }}</span>
              <b class="tabular">{{ fmt.n(b.count) }}</b>
              <span class="muted tabular" style="width: 3.2rem; text-align: right">{{ b.pct.toFixed(0) }}%</span>
            </div>
          </div>
        </div>
      </div>

      <div class="card">
        <div class="card-header"><h3 class="card-title">Top categories</h3><span class="card-sub">by product count</span></div>
        <div class="card-body stack" style="gap: .7rem">
          <div v-for="c in data?.topCategories ?? []" :key="c.code">
            <div class="row" style="font-size: .82rem; margin-bottom: .25rem">
              <span class="secondary-text" style="font-weight: 500">{{ c.name }}</span><span class="spacer" /><b class="tabular">{{ fmt.compact(c.count) }}</b>
            </div>
            <div class="progress sm"><span :style="{ width: (c.count / maxCat) * 100 + '%', background: 'var(--series-1)' }" /></div>
          </div>
          <EmptyState v-if="data && !data.topCategories.length" icon="tag" title="No categorised products" />
        </div>
      </div>

      <div class="card">
        <div class="card-header">
          <h3 class="card-title">Recent activity</h3>
          <router-link v-if="session.can('importOps')" to="/audit" class="btn btn-ghost btn-sm">Audit log <Icon name="arrowRight" :size="14" /></router-link>
        </div>
        <div class="list">
          <div v-for="a in data?.activity ?? []" :key="a.id" class="list-item">
            <span class="list-icon"><Icon :name="actionIcon(a.action)" :size="15" /></span>
            <div class="list-main">
              <div class="list-title">{{ a.action.replace(/([a-z])([A-Z])/g, '$1 $2') }}</div>
              <div class="list-sub">{{ a.actorId }} · {{ a.entityType }} {{ a.entityId }}</div>
            </div>
            <span class="muted" style="font-size: .74rem; white-space: nowrap">{{ fmt.ago(a.timestampUtc) }}</span>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>
