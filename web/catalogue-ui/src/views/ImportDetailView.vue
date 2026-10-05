<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import Icon from '../components/Icon.vue'
import Pager from '../components/Pager.vue'
import StatTile from '../components/StatTile.vue'
import StatusBadge from '../components/StatusBadge.vue'
import EmptyState from '../components/EmptyState.vue'
import { api, errorText, totalOf, type ImportError, type ImportItem } from '../api'
import { session } from '../session'
import { fmt, toast, usePolling } from '../ui'

const route = useRoute()
const id = route.params.id as string
const detail = ref<ImportItem | null>(null)
const notFound = ref(false)
const errors = ref<ImportError[]>([])
const errorTotal = ref(0)
const summary = ref<{ field: string; count: number }[]>([])
const field = ref('')
const search = ref('')
const page = ref(1)
const pageSize = ref(50)
const expanded = ref<number | null>(null)
const busy = ref(false)

const live = computed(() => !!detail.value && ['Received', 'Processing', 'Retrying'].includes(detail.value.status))
const done = computed(() => (detail.value ? detail.value.processedRows + detail.value.errorRows : 0))
const progress = computed(() => {
  const d = detail.value
  if (!d) return 0
  return d.totalRows ? fmt.pct(done.value, d.totalRows) : d.status.startsWith('Completed') ? 100 : 0
})
const errorRate = computed(() => (detail.value && done.value ? (detail.value.errorRows / done.value) * 100 : 0))
const elapsedMs = computed(() => {
  const d = detail.value
  if (!d) return 0
  const end = d.completedAtUtc ? new Date(d.completedAtUtc + 'Z').getTime() : Date.now()
  return end - new Date(d.receivedAtUtc + 'Z').getTime()
})
const rowsPerSec = computed(() => (elapsedMs.value > 0 ? done.value / (elapsedMs.value / 1000) : 0))

async function loadDetail() {
  try {
    detail.value = (await api.get<ImportItem>(`/imports/${id}`)).data
  } catch (e: any) {
    if (e.response?.status === 404) notFound.value = true
  }
}
async function loadErrors() {
  const r = await api.get<ImportError[]>(`/imports/${id}/errors`, {
    params: { page: page.value, pageSize: pageSize.value, field: field.value || undefined, search: search.value || undefined }
  })
  errors.value = r.data
  errorTotal.value = totalOf(r, r.data.length)
  summary.value = (await api.get(`/imports/${id}/errors/summary`)).data
}

let lastStatus = ''
usePolling(async () => {
  if (detail.value && !live.value && lastStatus === detail.value.status) return
  await loadDetail()
  if (detail.value && (detail.value.status !== lastStatus || live.value)) {
    lastStatus = detail.value.status
    await loadErrors()
  }
}, 2000)

let debounce: ReturnType<typeof setTimeout>
watch([field], () => { page.value = 1; loadErrors() })
watch(search, () => { clearTimeout(debounce); debounce = setTimeout(() => { page.value = 1; loadErrors() }, 300) })
watch([page, pageSize], loadErrors)

async function reprocess() {
  busy.value = true
  try {
    await api.post(`/imports/${id}/reprocess`)
    toast('info', 'Reprocess accepted', detail.value?.format === 'EDI-STREAM' ? 'The file is being re-streamed through Event Hub.' : 'The file was queued again.')
    lastStatus = ''
    setTimeout(loadDetail, 800)
  } catch (e) {
    toast('bad', 'Reprocess failed', errorText(e))
  } finally {
    busy.value = false
  }
}

async function exportErrors() {
  const r = await api.get<ImportError[]>(`/imports/${id}/errors`, { params: { pageSize: 5000, field: field.value || undefined, search: search.value || undefined } })
  const esc = (v: unknown) => `"${String(v ?? '').replace(/"/g, '""')}"`
  const csv = ['row,field,message,raw', ...r.data.map(e => [e.rowNumber, e.field, e.message, e.rawValue].map(esc).join(','))].join('\n')
  const a = document.createElement('a')
  a.href = URL.createObjectURL(new Blob([csv], { type: 'text/csv' }))
  a.download = `import-${id}-errors.csv`
  a.click()
  URL.revokeObjectURL(a.href)
  if (totalOf(r) > 5000) toast('info', 'Exported first 5,000 errors', `${fmt.n(totalOf(r))} match in total.`)
}

function copy(text?: string) {
  if (!text) return
  navigator.clipboard?.writeText(text)
  toast('info', 'Copied', text, 1800)
}

const steps = computed(() => {
  const d = detail.value
  if (!d) return []
  const failed = ['Failed', 'Rejected'].includes(d.status)
  return [
    { label: 'Received', at: d.receivedAtUtc, state: 'done' },
    { label: d.format === 'EDI-STREAM' ? 'Streaming via Event Hub' : 'Processing', at: undefined, state: live.value ? 'active' : 'done' },
    { label: failed ? d.status : 'Completed', at: d.completedAtUtc, state: live.value ? 'todo' : failed ? 'bad' : 'done' }
  ]
})
</script>

<template>
  <div v-if="notFound" class="card"><EmptyState icon="search" title="Import not found" text="It may have been removed."><router-link to="/imports" class="btn btn-secondary btn-sm">Back to imports</router-link></EmptyState></div>

  <template v-else>
    <div class="page-header">
      <div class="page-title-group">
        <router-link to="/imports" class="back-link"><Icon name="arrowLeft" :size="14" /> Imports</router-link>
        <h1 class="page-title">
          <span v-if="detail">{{ detail.fileName }}</span><span v-else class="skeleton" style="width: 260px; height: 1.6rem" />
          <StatusBadge v-if="detail" :status="detail.status" />
        </h1>
        <span v-if="detail" class="page-subtitle">
          Import #{{ detail.id }} · {{ detail.supplierCode }} · {{ detail.format === 'EDI-STREAM' ? 'EDI streamed through Event Hub' : detail.format }}
        </span>
      </div>
      <div class="page-actions">
        <router-link v-if="detail?.format === 'EDI-STREAM'" to="/event-hub" class="btn btn-secondary"><Icon name="zap" :size="15" /> Live telemetry</router-link>
        <button v-if="detail && session.can('importOps')" class="btn-primary" :disabled="live || busy" @click="reprocess">
          <Icon name="refresh" :size="15" :class="{ spin: busy }" /> {{ detail.format === 'EDI-STREAM' ? 'Re-stream' : 'Reprocess' }}
        </button>
      </div>
    </div>

    <div class="stat-grid">
      <StatTile label="Total rows" icon="layers" :value="fmt.n(detail?.totalRows || null)" :loading="!detail" :foot="detail && !detail.totalRows && live ? 'Counting while the producer reads…' : undefined" />
      <StatTile label="Processed" icon="checkCircle" tone="good" :value="fmt.n(detail?.processedRows)" :loading="!detail" :foot="`${fmt.compact(rowsPerSec)} rows/s average`" />
      <StatTile label="Row errors" icon="alert" :tone="detail?.errorRows ? 'bad' : ''" :value="fmt.n(detail?.errorRows)" :loading="!detail" :foot="`${errorRate.toFixed(2)}% error rate`" />
      <StatTile label="Duration" icon="clock" tone="info" :value="fmt.duration(elapsedMs)" :loading="!detail" :foot="live ? 'Running…' : detail?.completedAtUtc ? `Finished ${fmt.ago(detail.completedAtUtc)}` : ''" />
    </div>

    <div v-if="detail" class="card" style="margin-bottom: 1.1rem">
      <div class="card-body">
        <div class="row" style="margin-bottom: .55rem; font-size: .84rem">
          <b>{{ live ? 'Processing' : 'Processed' }}</b>
          <span class="muted">{{ fmt.n(done) }} of {{ detail.totalRows ? fmt.n(detail.totalRows) : '…' }} rows</span>
          <span class="spacer" /><b class="tabular">{{ progress }}%</b>
        </div>
        <div class="progress" :class="[detail.errorRows ? 'warn' : 'good', { indeterminate: live && !detail.totalRows }]" style="height: 10px"><span :style="{ width: progress + '%' }" /></div>

        <div class="row wrap" style="margin-top: 1.25rem; gap: 0">
          <template v-for="(s, i) in steps" :key="s.label">
            <div class="row" style="gap: .55rem">
              <span class="stat-icon" :class="s.state === 'done' ? 'good' : s.state === 'bad' ? 'bad' : s.state === 'active' ? 'info' : ''" style="width: 28px; height: 28px; border-radius: 99px">
                <Icon :name="s.state === 'done' ? 'check' : s.state === 'bad' ? 'x' : s.state === 'active' ? 'refresh' : 'clock'" :size="14" :class="{ spin: s.state === 'active' }" />
              </span>
              <div class="cell-title"><b style="font-size: .84rem">{{ s.label }}</b><small>{{ s.at ? fmt.date(s.at) : s.state === 'active' ? 'In progress' : '—' }}</small></div>
            </div>
            <div v-if="i < steps.length - 1" style="flex: 1; min-width: 30px; height: 2px; background: var(--border); margin: 0 .9rem" />
          </template>
        </div>

        <div class="row wrap mt-2" style="font-size: .8rem; color: var(--muted)">
          <span>Correlation ID</span><code>{{ detail.correlationId }}</code>
          <button class="btn-ghost btn-sm" style="padding: .1rem .3rem" aria-label="Copy correlation id" @click="copy(detail.correlationId)"><Icon name="copy" :size="13" /></button>
        </div>
        <div v-if="detail.failureReason" class="alert bad mt-2"><Icon name="alertCircle" :size="16" /><span><b>Failure:</b> {{ detail.failureReason }}</span></div>
      </div>
    </div>

    <div class="card">
      <div class="card-header">
        <div>
          <h3 class="card-title"><Icon name="alert" :size="16" /> Row diagnostics</h3>
          <div class="card-sub">Rows rejected by validation — the rest of the file still processed.</div>
        </div>
        <button class="btn-secondary btn-sm" :disabled="!errorTotal" @click="exportErrors"><Icon name="download" :size="14" /> Export CSV</button>
      </div>
      <div class="toolbar">
        <div class="chips">
          <span class="chip" :class="{ active: field === '' }" @click="field = ''">All <b>{{ fmt.n(summary.reduce((a, s) => a + s.count, 0)) }}</b></span>
          <span v-for="s in summary" :key="s.field" class="chip" :class="{ active: field === s.field }" @click="field = s.field">{{ s.field }} <b>{{ fmt.n(s.count) }}</b></span>
        </div>
        <span class="spacer" />
        <div class="input-icon" style="width: 240px"><span class="icon"><Icon name="search" :size="15" /></span><input v-model="search" placeholder="Search message or raw value…" /></div>
      </div>
      <div class="table-wrap">
        <table>
          <thead><tr><th class="num" style="width: 110px">Row</th><th style="width: 120px">Field</th><th>Message</th><th>Raw segment</th></tr></thead>
          <tbody>
            <tr v-for="e in errors" :key="e.id" class="clickable" @click="expanded = expanded === e.id ? null : e.id">
              <td class="num cell-strong">{{ fmt.n(e.rowNumber) }}</td>
              <td><span class="badge bad">{{ e.field }}</span></td>
              <td>{{ e.message }}</td>
              <td>
                <code v-if="e.rawValue" :class="{ 'cell-truncate': expanded !== e.id }" style="display: block; white-space: pre-wrap; word-break: break-all">{{ expanded === e.id ? e.rawValue.replace(/'/g, `'\n`) : e.rawValue }}</code>
                <span v-else class="muted">—</span>
              </td>
            </tr>
            <tr v-if="!errors.length">
              <td colspan="4">
                <EmptyState :icon="detail?.errorRows ? 'search' : 'checkCircle'" :title="detail?.errorRows ? 'No errors match' : 'No row errors'"
                  :text="detail?.errorRows ? 'Change the field filter or search.' : 'Every row passed validation.'" />
              </td>
            </tr>
          </tbody>
        </table>
      </div>
      <Pager v-if="errorTotal > 0" v-model:page="page" v-model:page-size="pageSize" :total="errorTotal" noun="errors" :sizes="[25, 50, 100, 250]" />
    </div>
  </template>
</template>
