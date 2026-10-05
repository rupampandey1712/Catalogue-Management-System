<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import Icon from '../components/Icon.vue'
import TimeSeriesChart from '../components/TimeSeriesChart.vue'
import StatusBadge from '../components/StatusBadge.vue'
import EmptyState from '../components/EmptyState.vue'
import { api, errorText, type EdiSource, type HubOverview, type StreamImport, type StreamRun, type Supplier } from '../api'
import { session } from '../session'
import { fmt, toast, usePolling } from '../ui'

const router = useRouter()
const overview = ref<HubOverview | null>(null)
const streams = ref<StreamImport[]>([])
const sources = ref<EdiSource[]>([])
const sourceRoot = ref('')
const suppliers = ref<Supplier[]>([])
const loadError = ref('')
const rangeSec = ref(120)

// stream form
const supplierId = ref<number | ''>('')
const sourceMode = ref<'library' | 'upload'>('library')
const source = ref('')
const uploadFile = ref<File | null>(null)
const uploadPct = ref<number | null>(null)
const chunkSize = ref(1000)
const starting = ref(false)
const genItems = ref(250000)
const generating = ref(false)
const showHelp = ref(false)

usePolling(async () => {
  try {
    overview.value = (await api.get<HubOverview>('/eventhub/overview')).data
    loadError.value = ''
  } catch (e) { loadError.value = errorText(e, 'Event Hub telemetry unavailable.') }
}, 1500)
usePolling(async () => { streams.value = (await api.get<StreamImport[]>('/eventhub/streams')).data }, 3000)

async function loadSources() {
  const { data } = await api.get('/eventhub/sources')
  sources.value = data.files
  sourceRoot.value = data.root
  if (!source.value && sources.value.length) source.value = [...sources.value].sort((a, b) => b.estimatedLineItems - a.estimatedLineItems)
    .find(s => s.estimatedLineItems >= 100000 && s.estimatedLineItems <= 1000000)?.name ?? sources.value[0].name
}
onMounted(async () => {
  loadSources()
  if (session.can('supplierAdmin')) {
    suppliers.value = (await api.get<Supplier[]>('/suppliers')).data
    supplierId.value = suppliers.value.find(s => s.integrationType === 'Edi')?.id ?? suppliers.value[0]?.id ?? ''
  }
})

const hub = computed(() => overview.value?.hub)
const isEmulator = computed(() => hub.value?.mode === 'EventHubs')
const timeline = computed(() => (overview.value?.timeline ?? []).slice(-rangeSec.value))
const selectedSource = computed(() => sources.value.find(s => s.name === source.value))
const plannedItems = computed(() => sourceMode.value === 'library' ? selectedSource.value?.estimatedLineItems ?? 0 : (uploadFile.value?.size ?? 0) / 100)
const plannedEvents = computed(() => Math.ceil(plannedItems.value / chunkSize.value))
const approxEventKb = computed(() => (chunkSize.value * 95) / 1024)
const activeRuns = computed(() => (overview.value?.runs ?? []).filter(r => r.state === 'Publishing' || runImport(r)?.status === 'Processing'))
const maxLag = computed(() => Math.max(1, ...(overview.value?.partitions ?? []).map(p => p.lag)))

function runImport(r: StreamRun) { return streams.value.find(s => s.id === r.importId) }
function runEstimatedTotal(r: StreamRun) {
  const imp = runImport(r)
  if (imp?.totalRows) return imp.totalRows
  if (r.state === 'Published') return r.publishedRows
  return r.totalBytes && r.bytesRead ? Math.round(r.publishedRows / (r.bytesRead / r.totalBytes)) : 0
}
function runEta(r: StreamRun) {
  const total = runEstimatedTotal(r)
  const done = r.processedRows + r.errorRows
  const rate = overview.value?.rates.rowsPerSec ?? 0
  if (!total || rate <= 0 || done >= total) return ''
  return fmt.duration(((total - done) / rate) * 1000)
}

async function start() {
  if (!supplierId.value) return
  starting.value = true
  try {
    let data: any
    if (sourceMode.value === 'library') {
      data = (await api.post('/eventhub/streams', { supplierId: supplierId.value, source: source.value, chunkSize: chunkSize.value })).data
    } else {
      if (!uploadFile.value) return
      const fd = new FormData()
      fd.append('supplierId', String(supplierId.value))
      fd.append('chunkSize', String(chunkSize.value))
      fd.append('file', uploadFile.value)
      uploadPct.value = 0
      data = (await api.post('/eventhub/streams/upload', fd, {
        onUploadProgress: e => { uploadPct.value = e.total ? Math.round((e.loaded / e.total) * 100) : null }
      })).data
    }
    if (data.existing) {
      toast('info', `Already imported as #${data.id}`, 'This file was streamed for this supplier before. Use Re-stream to run it again.')
    } else {
      toast('good', `Streaming started · import #${data.id}`, `Chunks of ${fmt.n(chunkSize.value)} line items are being published.`)
    }
  } catch (e) {
    toast('bad', 'Could not start stream', errorText(e))
  } finally {
    starting.value = false
    uploadPct.value = null
  }
}

async function restream(id: number) {
  try {
    await api.post(`/eventhub/streams/${id}/restream`, { chunkSize: chunkSize.value })
    toast('info', `Re-streaming import #${id}`)
  } catch (e) { toast('bad', 'Re-stream failed', errorText(e)) }
}

async function generate() {
  generating.value = true
  try {
    const { data } = await api.post<EdiSource>('/eventhub/sources/generate', { lineItems: genItems.value })
    await loadSources()
    if (data) source.value = data.name
    toast('good', 'Sample interchange generated', `${data?.name} · ${fmt.bytes(data?.sizeBytes)}`)
  } catch (e) { toast('bad', 'Generation failed', errorText(e)) }
  finally { generating.value = false }
}

const fmtT = (t: number) => new Date(t * 1000).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })
</script>

<template>
  <div class="page-header">
    <div class="page-title-group">
      <h1 class="page-title">
        Event Hub ingestion
        <span v-if="hub" class="badge" :class="hub.connected ? (isEmulator ? 'primary' : 'info') : 'bad'">
          <span class="dot" />{{ !hub.connected ? 'Disconnected' : isEmulator ? 'Event Hubs emulator' : 'In-memory hub' }}
        </span>
      </h1>
      <span class="page-subtitle">Stream multi-million line PRICAT interchanges through a partitioned hub into the catalogue.</span>
    </div>
    <div class="page-actions">
      <button class="btn-secondary" @click="showHelp = !showHelp"><Icon name="info" :size="15" /> How it works</button>
    </div>
  </div>

  <div v-if="loadError" class="alert bad" style="margin-bottom: 1rem"><Icon name="alertCircle" :size="16" />{{ loadError }}</div>
  <div v-if="hub && !hub.connected" class="alert warn" style="margin-bottom: 1rem">
    <Icon name="alert" :size="16" />
    <span><b>Cannot reach the Event Hub.</b> {{ hub.connectionError }} — start the emulator with <code>docker compose up eventhubs azurite</code>.</span>
  </div>

  <div v-if="showHelp" class="card card-pad" style="margin-bottom: 1.1rem">
    <div class="grid" style="grid-template-columns: repeat(auto-fit, minmax(190px, 1fr)); gap: .9rem">
      <div v-for="(s, i) in [
        { icon: 'file', title: 'Producer', text: 'Reads the interchange segment-by-segment (constant memory) and cuts it into self-contained UNH…UNT chunks of N line items.' },
        { icon: 'zap', title: 'Event Hub', text: 'Chunks are sent in ≤1 MB batches and spread round-robin across partitions. Back-pressure shows up as consumer lag.' },
        { icon: 'cpu', title: 'Partition consumers', text: 'One processor per partition (load-balanced across instances in the consumer group) parses and validates each chunk.' },
        { icon: 'database', title: 'Bulk writer', text: 'SqlBulkCopy + set-based MERGE in one transaction per chunk, with a chunk ledger so redelivery is a no-op.' }
      ]" :key="s.title" class="row" style="align-items: flex-start; gap: .7rem">
        <span class="stat-icon"><Icon :name="s.icon" :size="16" /></span>
        <div><b style="font-size: .86rem">{{ i + 1 }}. {{ s.title }}</b><p class="muted" style="font-size: .8rem; margin-top: .15rem">{{ s.text }}</p></div>
      </div>
    </div>
  </div>

  <!-- Hero metrics -->
  <section class="hub-hero" style="margin-bottom: 1.1rem">
    <div class="row wrap" style="gap: .5rem 1rem; font-size: .8rem; color: #c5c9ef">
      <span class="row" style="gap: .35rem"><span class="pulse-dot" :class="{ idle: !(overview?.rates.rowsPerSec) }" /> {{ overview?.rates.rowsPerSec ? 'Ingesting' : 'Idle' }}</span>
      <span>Namespace <code>{{ hub?.namespace ?? '…' }}</code></span>
      <span>Hub <code>{{ hub?.name ?? '…' }}</code></span>
      <span>Consumer group <code>{{ hub?.consumerGroup ?? '…' }}</code></span>
      <span>{{ hub?.partitionCount ?? '–' }} partitions</span>
    </div>
    <div class="hero-grid">
      <div><div class="hero-label">Rows / sec</div><div class="hero-value">{{ fmt.compact(overview?.rates.rowsPerSec ?? 0) }}</div></div>
      <div><div class="hero-label">Events in / out</div><div class="hero-value">{{ overview?.rates.eventsInPerSec ?? 0 }}<span class="hero-unit">/</span>{{ overview?.rates.eventsOutPerSec ?? 0 }}<span class="hero-unit">per s</span></div></div>
      <div><div class="hero-label">Consumer lag</div><div class="hero-value">{{ fmt.n(overview?.totalLag ?? 0) }}<span class="hero-unit">events</span></div></div>
      <div><div class="hero-label">Latency p50 / p95</div><div class="hero-value">{{ fmt.duration(overview?.rates.latencyP50Ms ?? 0) }}<span class="hero-unit">/ {{ fmt.duration(overview?.rates.latencyP95Ms ?? 0) }}</span></div></div>
      <div><div class="hero-label">Rows this session</div><div class="hero-value">{{ fmt.compact(overview?.totals.processedRows ?? 0) }}</div></div>
      <div><div class="hero-label">Rejected rows</div><div class="hero-value">{{ fmt.compact(overview?.totals.errorRows ?? 0) }}</div></div>
    </div>
  </section>

  <div class="grid grid-main-side">
    <div class="stack">
      <div class="card">
        <div class="card-header flush">
          <div>
            <h3 class="card-title">Throughput — rows written per second</h3>
            <div class="card-sub">Peak {{ fmt.compact(overview?.rates.peakRowsPerSec ?? 0) }} rows/s in the last 3 minutes</div>
          </div>
          <div class="segmented">
            <button v-for="w in [60, 120, 180]" :key="w" :class="{ active: rangeSec === w }" @click="rangeSec = w">{{ w / 60 }}m</button>
          </div>
        </div>
        <div class="card-body" style="padding-top: .5rem">
          <TimeSeriesChart :x="timeline.map(p => p.t)" :values="[timeline.map(p => p.rowsOut)]" :format-x="fmtT"
            :series="[{ name: 'Rows / s', color: 'var(--series-1)', area: true }]" :format-y="fmt.compact" :height="210" />
        </div>
      </div>

      <div class="card">
        <div class="card-header flush">
          <div>
            <h3 class="card-title">Events published vs consumed</h3>
            <div class="card-sub">When published outpaces consumed, the gap accumulates as partition lag</div>
          </div>
          <div class="legend">
            <span><span class="swatch line" style="background: var(--series-2)" />Published / s</span>
            <span><span class="swatch line" style="background: var(--series-1)" />Consumed / s</span>
          </div>
        </div>
        <div class="card-body" style="padding-top: .5rem">
          <TimeSeriesChart :x="timeline.map(p => p.t)" :values="[timeline.map(p => p.eventsIn), timeline.map(p => p.eventsOut)]" :format-x="fmtT"
            :series="[{ name: 'Published / s', color: 'var(--series-2)' }, { name: 'Consumed / s', color: 'var(--series-1)' }]" :height="170" />
        </div>
      </div>
    </div>

    <!-- Stream launcher -->
    <div class="card" style="align-self: start">
      <div class="card-header"><h3 class="card-title"><Icon name="play" :size="15" /> Stream an EDI interchange</h3></div>
      <div v-if="!session.can('supplierAdmin')" class="card-body"><div class="alert info"><Icon name="info" :size="16" />Starting streams requires the Admin or SupplierManager role. You can still watch telemetry.</div></div>
      <div v-else class="card-body stack" style="gap: 1rem">
        <div class="field">
          <label for="sup">Supplier</label>
          <select id="sup" v-model="supplierId"><option v-for="s in suppliers" :key="s.id" :value="s.id">{{ s.code }} — {{ s.name }}</option></select>
        </div>

        <div>
          <div class="segmented" style="margin-bottom: .6rem">
            <button :class="{ active: sourceMode === 'library' }" @click="sourceMode = 'library'"><Icon name="database" :size="13" /> Server library</button>
            <button :class="{ active: sourceMode === 'upload' }" @click="sourceMode = 'upload'"><Icon name="upload" :size="13" /> Upload (≤ 2 GB)</button>
          </div>

          <template v-if="sourceMode === 'library'">
            <div class="card" style="box-shadow: none; max-height: 250px; overflow: auto">
              <div class="list">
                <label v-for="s in sources" :key="s.name" class="list-item" style="cursor: pointer; margin: 0; font-weight: 400" :class="{ selected: source === s.name }"
                  :style="source === s.name ? 'background: var(--primary-soft)' : ''">
                  <input v-model="source" type="radio" name="src" :value="s.name" />
                  <div class="list-main">
                    <div class="list-title mono" style="font-size: .8rem">{{ s.name }}</div>
                    <div class="list-sub">{{ fmt.bytes(s.sizeBytes) }} · {{ fmt.compact(s.estimatedLineItems) }} line items</div>
                  </div>
                  <span v-if="s.estimatedLineItems >= 1000000" class="badge warn">{{ fmt.compact(s.estimatedLineItems) }}</span>
                </label>
                <EmptyState v-if="!sources.length" icon="file" title="Library is empty" text="Generate a sample below." />
              </div>
            </div>
            <div class="hint" :title="sourceRoot">Folder: <code style="font-size: .7rem">{{ sourceRoot.split(/[\\/]/).slice(-2).join('/') }}</code></div>
            <div class="row mt-1" style="gap: .4rem">
              <select v-model.number="genItems" aria-label="Line items to generate" style="flex: 1">
                <option :value="100000">100K line items (~9 MB)</option>
                <option :value="250000">250K line items (~23 MB)</option>
                <option :value="1000000">1M line items (~90 MB)</option>
                <option :value="2500000">2.5M line items (~225 MB)</option>
                <option :value="5000000">5M line items (~450 MB)</option>
              </select>
              <button class="btn-secondary btn-sm" :disabled="generating" @click="generate">
                <Icon :name="generating ? 'refresh' : 'sparkles'" :size="14" :class="{ spin: generating }" /> Generate
              </button>
            </div>
          </template>

          <template v-else>
            <label class="dropzone" :class="{ 'has-file': uploadFile }" style="margin: 0">
              <span class="dropzone-icon"><Icon :name="uploadFile ? 'file' : 'upload'" :size="20" /></span>
              <b style="color: var(--text)">{{ uploadFile?.name ?? 'Choose a .edi interchange' }}</b>
              <span>{{ uploadFile ? fmt.bytes(uploadFile.size) : 'Stored, hashed and streamed — never held in memory' }}</span>
              <input type="file" accept=".edi" hidden @change="uploadFile = ($event.target as HTMLInputElement).files?.[0] ?? null" />
            </label>
            <div v-if="uploadPct !== null" class="mt-1">
              <div class="row" style="font-size: .76rem"><span class="muted">Uploading</span><span class="spacer" /><b>{{ uploadPct }}%</b></div>
              <div class="progress sm"><span :style="{ width: uploadPct + '%' }" /></div>
            </div>
          </template>
        </div>

        <div class="field">
          <label for="chunk" class="row">Chunk size <span class="spacer" /><b style="color: var(--text)">{{ fmt.n(chunkSize) }} line items / event</b></label>
          <input id="chunk" v-model.number="chunkSize" type="range" min="250" max="5000" step="250" />
          <span class="hint">≈ {{ approxEventKb.toFixed(0) }} KB per event · ≈ {{ fmt.n(plannedEvents) }} events across {{ hub?.partitionCount ?? '–' }} partitions</span>
        </div>

        <button class="btn-primary btn-lg" :disabled="starting || !supplierId || (sourceMode === 'library' ? !source : !uploadFile)" @click="start">
          <Icon :name="starting ? 'refresh' : 'zap'" :size="16" :class="{ spin: starting }" />
          {{ starting ? 'Starting…' : `Stream ${fmt.compact(plannedItems)} line items` }}
        </button>
      </div>
    </div>
  </div>

  <!-- Active runs -->
  <div v-if="activeRuns.length" class="card mt-2">
    <div class="card-header"><h3 class="card-title"><span class="pulse-dot" /> Active streams</h3></div>
    <div class="card-body stack">
      <div v-for="r in activeRuns" :key="r.importId" class="stack" style="gap: .45rem">
        <div class="row wrap">
          <router-link :to="`/imports/${r.importId}`" class="cell-strong">{{ r.fileName }}</router-link>
          <span class="tag">{{ r.supplier }}</span>
          <span class="muted" style="font-size: .78rem">#{{ r.importId }} · chunk {{ fmt.n(r.chunkSize) }}</span>
          <span class="spacer" />
          <span v-if="runEta(r)" class="muted" style="font-size: .78rem">ETA {{ runEta(r) }}</span>
          <StatusBadge :status="r.state === 'Publishing' ? 'Publishing' : 'Processing'" :label="r.state === 'Publishing' ? 'Publishing' : 'Consuming'" />
        </div>
        <div class="grid grid-2" style="gap: .9rem">
          <div>
            <div class="row" style="font-size: .76rem"><span class="swatch" style="background: var(--series-2)" /> Published <span class="spacer" /><b class="tabular">{{ fmt.n(r.publishedRows) }}</b></div>
            <div class="progress sm"><span :style="{ width: (r.totalBytes ? fmt.pct(r.bytesRead, r.totalBytes) : 0) + '%', background: 'var(--series-2)' }" /></div>
          </div>
          <div>
            <div class="row" style="font-size: .76rem"><span class="swatch" style="background: var(--series-1)" /> Written <span class="spacer" /><b class="tabular">{{ fmt.n(r.processedRows) }}</b><span class="muted">&nbsp;· {{ fmt.n(r.errorRows) }} rejected</span></div>
            <div class="progress sm"><span :style="{ width: fmt.pct(r.processedRows + r.errorRows, runEstimatedTotal(r) || 1) + '%', background: 'var(--series-1)' }" /></div>
          </div>
        </div>
      </div>
    </div>
  </div>

  <!-- Partitions -->
  <div class="card mt-2">
    <div class="card-header">
      <div>
        <h3 class="card-title"><Icon name="server" :size="16" /> Partitions</h3>
        <div class="card-sub">{{ hub?.consumerInProcess ? 'Consumers run in this API instance' : 'Consumers run in worker instances' }} · ownership is load-balanced across the consumer group</div>
      </div>
    </div>
    <div class="partition-grid">
      <div v-for="p in overview?.partitions ?? []" :key="p.partitionId" class="partition" :class="{ owned: p.owned }">
        <div class="partition-head">
          <span class="partition-id"><span class="pulse-dot" :class="{ idle: !p.lag && (!p.lastProcessedUtc || Date.now() - new Date(p.lastProcessedUtc).getTime() > 3000) }" /> P{{ p.partitionId }}</span>
          <span class="badge" :class="p.owned ? 'good' : ''" style="font-size: .68rem">{{ p.owned ? 'Owned' : 'Not owned' }}</span>
        </div>
        <div class="partition-stats">
          <span>Events</span><b>{{ fmt.compact(p.events) }}</b>
          <span>Rows</span><b>{{ fmt.compact(p.rows) }}</b>
          <span>Errors</span><b :style="p.errorRows ? 'color: var(--bad)' : ''">{{ fmt.compact(p.errorRows) }}</b>
          <span>Seq</span><b>{{ p.lastProcessedSequenceNumber }} / {{ p.lastEnqueuedSequenceNumber }}</b>
        </div>
        <div>
          <div class="row" style="font-size: .72rem"><span class="muted">Lag</span><span class="spacer" /><b :style="p.lag ? 'color: var(--warn)' : ''">{{ fmt.n(p.lag) }}</b></div>
          <div class="progress sm" :class="p.lag ? 'warn' : 'good'"><span :style="{ width: (p.lag / maxLag) * 100 + '%' }" /></div>
        </div>
      </div>
      <EmptyState v-if="overview && !overview.partitions.length" icon="server" title="No partitions reported" style="grid-column: 1 / -1" />
    </div>
  </div>

  <div class="grid grid-2 mt-2">
    <!-- Live feed -->
    <div class="card">
      <div class="card-header"><h3 class="card-title"><Icon name="activity" :size="16" /> Live event feed</h3><span class="card-sub">last {{ overview?.recent.length ?? 0 }} events</span></div>
      <div class="table-wrap" style="max-height: 420px">
        <table>
          <thead><tr><th>Partition · seq</th><th>Chunk</th><th class="num">Rows</th><th class="num">Errors</th><th class="num">Apply</th><th class="num">E2E</th></tr></thead>
          <tbody>
            <tr v-for="e in overview?.recent ?? []" :key="`${e.partitionId}-${e.sequenceNumber}`">
              <td><span class="tag">P{{ e.partitionId }}</span> <span class="mono muted">#{{ e.sequenceNumber }}</span></td>
              <td><router-link :to="`/imports/${e.importId}`" class="mono">{{ e.importId }}/{{ e.chunkIndex }}</router-link>
                <span v-if="e.duplicate" class="badge warn" style="margin-left: .3rem">dup</span></td>
              <td class="num">{{ fmt.n(e.rows) }}</td>
              <td class="num" :style="e.errors ? 'color: var(--bad)' : 'color: var(--muted)'">{{ e.errors }}</td>
              <td class="num cell-muted">{{ fmt.duration(e.durationMs) }}</td>
              <td class="num cell-muted">{{ fmt.duration(e.latencyMs) }}</td>
            </tr>
            <tr v-if="!overview?.recent.length"><td colspan="6"><EmptyState icon="activity" title="Waiting for events" text="Start a stream to see chunks flow through the partitions." /></td></tr>
          </tbody>
        </table>
      </div>
    </div>

    <!-- History -->
    <div class="card">
      <div class="card-header"><h3 class="card-title"><Icon name="clock" :size="16" /> Stream history</h3><span class="card-sub">persisted imports</span></div>
      <div class="table-wrap" style="max-height: 420px">
        <table>
          <thead><tr><th>Import</th><th>Status</th><th class="num">Rows</th><th></th></tr></thead>
          <tbody>
            <tr v-for="s in streams" :key="s.id" class="clickable" @click="router.push(`/imports/${s.id}`)">
              <td>
                <div class="cell-title">
                  <span class="cell-strong">{{ s.fileName }}</span>
                  <small>#{{ s.id }} · {{ s.supplierCode }} · {{ fmt.ago(s.receivedAtUtc) }}</small>
                </div>
              </td>
              <td><StatusBadge :status="s.status" /></td>
              <td class="num">
                <div class="cell-title" style="align-items: flex-end">
                  <span class="tabular">{{ fmt.compact(s.processedRows) }}</span>
                  <small v-if="s.errorRows" style="color: var(--bad)">{{ fmt.n(s.errorRows) }} rejected</small>
                </div>
              </td>
              <td class="num" @click.stop>
                <button v-if="session.can('importOps') && s.status !== 'Processing'" class="btn-ghost btn-sm" title="Re-stream" @click="restream(s.id)"><Icon name="refresh" :size="14" /></button>
              </td>
            </tr>
            <tr v-if="!streams.length"><td colspan="4"><EmptyState icon="zap" title="No streams yet" /></td></tr>
          </tbody>
        </table>
      </div>
    </div>
  </div>
</template>
