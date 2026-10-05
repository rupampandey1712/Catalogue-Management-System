<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import Icon from '../components/Icon.vue'
import Pager from '../components/Pager.vue'
import StatusBadge from '../components/StatusBadge.vue'
import EmptyState from '../components/EmptyState.vue'
import { api, errorText, totalOf, type ImportItem, type Supplier } from '../api'
import { session } from '../session'
import { fmt, toast, usePolling } from '../ui'

const router = useRouter()
const imports = ref<ImportItem[]>([])
const total = ref(0)
const loading = ref(true)
const suppliers = ref<Supplier[]>([])

const tab = ref('')
const format = ref('')
const search = ref('')
const page = ref(1)
const pageSize = ref(20)
const autoRefresh = ref(true)

const supplierId = ref<number | ''>('')
const file = ref<File | null>(null)
const dragOver = ref(false)
const uploading = ref(false)
const fileInput = ref<HTMLInputElement>()
const MAX_BYTES = 25 * 1024 * 1024

const tabs = [
  { value: '', label: 'All' },
  { value: 'Received,Processing,Retrying', label: 'In progress' },
  { value: 'Completed', label: 'Completed' },
  { value: 'CompletedWithErrors', label: 'With errors' },
  { value: 'Failed,Rejected', label: 'Failed' }
]

async function load() {
  try {
    const r = await api.get<ImportItem[]>('/imports', {
      params: { status: tab.value || undefined, format: format.value || undefined, search: search.value || undefined, page: page.value, pageSize: pageSize.value }
    })
    imports.value = r.data
    total.value = totalOf(r, r.data.length)
  } finally {
    loading.value = false
  }
}
let debounce: ReturnType<typeof setTimeout>
watch([tab, format], () => { page.value = 1; load() })
watch(search, () => { clearTimeout(debounce); debounce = setTimeout(() => { page.value = 1; load() }, 300) })
watch([page, pageSize], load)
usePolling(() => (autoRefresh.value ? load() : undefined), 4000)

onMounted(async () => {
  if (session.can('supplierAdmin')) suppliers.value = (await api.get<Supplier[]>('/suppliers')).data
})

const fileProblem = computed(() => {
  if (!file.value) return ''
  const ext = file.value.name.split('.').pop()?.toLowerCase()
  if (ext !== 'csv' && ext !== 'edi') return 'Only .csv and .edi files are accepted.'
  if (file.value.size > MAX_BYTES) return ext === 'edi'
    ? 'This EDI file is over 25 MB — stream it through the Event Hub instead.'
    : 'File exceeds the 25 MB upload limit.'
  return ''
})

function pickFile(f?: File | null) {
  file.value = f ?? null
}
function onDrop(e: DragEvent) {
  dragOver.value = false
  pickFile(e.dataTransfer?.files?.[0])
}

async function upload() {
  if (!supplierId.value || !file.value || fileProblem.value) return
  const fd = new FormData()
  fd.append('supplierId', String(supplierId.value))
  fd.append('file', file.value)
  uploading.value = true
  try {
    const { data } = await api.post('/imports', fd)
    toast('good', `Import #${data.id} accepted`, `${file.value.name} is ${data.status.toLowerCase()}.`)
    file.value = null
    if (fileInput.value) fileInput.value.value = ''
    tab.value = ''
    load()
  } catch (e) {
    toast('bad', 'Upload failed', errorText(e))
  } finally {
    uploading.value = false
  }
}

async function reprocess(i: ImportItem) {
  try {
    await api.post(`/imports/${i.id}/reprocess`)
    toast('info', `Reprocessing import #${i.id}`, i.format === 'EDI-STREAM' ? 'Re-streaming through Event Hub.' : undefined)
    load()
  } catch (e) {
    toast('bad', 'Reprocess failed', errorText(e))
  }
}

const progress = (i: ImportItem) =>
  i.totalRows ? fmt.pct(i.processedRows + i.errorRows, i.totalRows) : i.status.startsWith('Completed') ? 100 : 0
const duration = (i: ImportItem) =>
  i.completedAtUtc ? fmt.duration(new Date(i.completedAtUtc + 'Z').getTime() - new Date(i.receivedAtUtc + 'Z').getTime()) : '—'
</script>

<template>
  <div class="page-header">
    <div class="page-title-group">
      <h1 class="page-title">Imports</h1>
      <span class="page-subtitle">File-based supplier feeds. Multi-million line EDI interchanges stream through the <router-link to="/event-hub">Event Hub</router-link>.</span>
    </div>
  </div>

  <div v-if="session.can('supplierAdmin')" class="card" style="margin-bottom: 1.1rem">
    <div class="card-header"><h3 class="card-title"><Icon name="upload" :size="16" /> Upload a catalogue file</h3><span class="card-sub">CSV or EDI · up to 25 MB · processed asynchronously</span></div>
    <div class="card-body">
      <div class="grid" style="grid-template-columns: minmax(220px, 300px) 1fr auto; align-items: stretch">
        <div class="field">
          <label for="sup">Supplier</label>
          <select id="sup" v-model="supplierId">
            <option value="">Select supplier…</option>
            <option v-for="s in suppliers" :key="s.id" :value="s.id">{{ s.code }} — {{ s.name }}</option>
          </select>
          <span class="hint">Same file twice for a supplier is de-duplicated by SHA-256.</span>
        </div>
        <div class="dropzone" :class="{ over: dragOver, 'has-file': file }" tabindex="0" role="button" aria-label="Choose a file"
          @click="fileInput?.click()" @keydown.enter="fileInput?.click()"
          @dragover.prevent="dragOver = true" @dragleave="dragOver = false" @drop.prevent="onDrop">
          <span class="dropzone-icon"><Icon :name="file ? 'file' : 'upload'" :size="20" /></span>
          <template v-if="file">
            <b style="color: var(--text)">{{ file.name }}</b>
            <span>{{ fmt.bytes(file.size) }} · click to change</span>
          </template>
          <template v-else>
            <b style="color: var(--text)">Drop a .csv or .edi file here</b>
            <span>or click to browse</span>
          </template>
          <input ref="fileInput" type="file" accept=".csv,.edi" hidden @change="pickFile(($event.target as HTMLInputElement).files?.[0])" />
        </div>
        <div style="display: flex; flex-direction: column; justify-content: flex-end; gap: .5rem">
          <button class="btn-primary btn-lg" :disabled="!supplierId || !file || !!fileProblem || uploading" @click="upload">
            <Icon :name="uploading ? 'refresh' : 'upload'" :size="16" :class="{ spin: uploading }" /> Upload
          </button>
        </div>
      </div>
      <div v-if="fileProblem" class="alert warn mt-2">
        <Icon name="alert" :size="16" /><span>{{ fileProblem }}</span>
        <router-link v-if="fileProblem.includes('Event Hub')" to="/event-hub" class="btn btn-secondary btn-sm" style="margin-left: auto">Open Event Hub</router-link>
      </div>
    </div>
  </div>

  <div class="card">
    <div class="toolbar">
      <div class="segmented">
        <button v-for="t in tabs" :key="t.value" :class="{ active: tab === t.value }" @click="tab = t.value">{{ t.label }}</button>
      </div>
      <select v-model="format" aria-label="Format">
        <option value="">All formats</option><option value="CSV">CSV</option><option value="EDI">EDI</option><option value="EDI-STREAM">EDI stream</option>
      </select>
      <div class="input-icon" style="width: 220px"><span class="icon"><Icon name="search" :size="15" /></span><input v-model="search" placeholder="File name…" /></div>
      <span class="spacer" />
      <label class="row" style="margin: 0; font-weight: 500; cursor: pointer"><input v-model="autoRefresh" type="checkbox" /> Live refresh</label>
      <button class="btn-ghost btn-sm" aria-label="Refresh" @click="load"><Icon name="refresh" :size="14" /></button>
    </div>
    <div class="table-wrap">
      <table>
        <thead>
          <tr><th>Import</th><th>Supplier</th><th>Format</th><th>Status</th><th style="width: 20%">Progress</th>
            <th class="num">Rows</th><th class="num">Errors</th><th>Duration</th><th></th></tr>
        </thead>
        <tbody>
          <template v-if="loading">
            <tr v-for="i in 6" :key="i"><td v-for="j in 8" :key="j"><span class="skeleton" /></td><td /></tr>
          </template>
          <tr v-for="i in imports" :key="i.id" class="clickable" @click="router.push(`/imports/${i.id}`)">
            <td>
              <div class="cell-title">
                <span class="cell-strong">{{ i.fileName }}</span>
                <small>#{{ i.id }} · {{ fmt.date(i.receivedAtUtc) }}</small>
              </div>
            </td>
            <td><span class="tag">{{ i.supplierCode ?? i.supplierId }}</span></td>
            <td><span class="badge" :class="i.format === 'EDI-STREAM' ? 'primary' : ''">{{ i.format === 'EDI-STREAM' ? 'Event Hub' : i.format }}</span></td>
            <td><StatusBadge :status="i.status" /></td>
            <td>
              <div class="row" style="gap: .5rem">
                <div class="progress sm" style="flex: 1" :class="[i.errorRows ? 'warn' : 'good', { indeterminate: i.status === 'Processing' && !i.totalRows }]">
                  <span :style="{ width: progress(i) + '%' }" />
                </div>
                <span class="muted tabular" style="font-size: .74rem; width: 2.6rem; text-align: right">{{ progress(i) }}%</span>
              </div>
            </td>
            <td class="num">{{ fmt.n(i.totalRows || i.processedRows) }}</td>
            <td class="num" :style="i.errorRows ? 'color: var(--bad); font-weight: 600' : 'color: var(--muted)'">{{ fmt.n(i.errorRows) }}</td>
            <td class="cell-muted">{{ duration(i) }}</td>
            <td class="num" @click.stop>
              <button v-if="['Failed', 'Rejected', 'CompletedWithErrors'].includes(i.status)" class="btn-ghost btn-sm" title="Reprocess" @click="reprocess(i)">
                <Icon name="refresh" :size="14" />
              </button>
            </td>
          </tr>
          <tr v-if="!loading && !imports.length">
            <td colspan="9"><EmptyState icon="upload" title="No imports here" text="Upload a CSV/EDI file above, or stream a large interchange from the Event Hub page." /></td>
          </tr>
        </tbody>
      </table>
    </div>
    <Pager v-model:page="page" v-model:page-size="pageSize" :total="total" noun="imports" :sizes="[10, 20, 50, 100]" />
  </div>
</template>
