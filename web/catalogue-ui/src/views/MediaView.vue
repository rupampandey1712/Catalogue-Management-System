<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import Icon from '../components/Icon.vue'
import StatTile from '../components/StatTile.vue'
import EmptyState from '../components/EmptyState.vue'
import { api, errorText } from '../api'
import { fmt, toast } from '../ui'

const router = useRouter()
const stats = ref<any>(null)
const fetches = ref<any[]>([])
const file = ref<File | null>(null)
const over = ref(false)
const uploading = ref(false)
const progress = ref(0)
const report = ref<any>(null)
const filter = ref('')

async function load() {
  stats.value = (await api.get('/media/stats')).data
  fetches.value = (await api.get('/media/fetches', { params: { take: 50 } })).data
}

async function upload() {
  if (!file.value) return
  uploading.value = true
  progress.value = 0
  report.value = null
  const fd = new FormData()
  fd.append('file', file.value)
  try {
    report.value = (await api.post('/media/bulk', fd, {
      timeout: 15 * 60_000,
      onUploadProgress: e => { progress.value = e.total ? Math.round((e.loaded / e.total) * 100) : 0 }
    })).data
    toast('good', 'Media pack imported', `${report.value.attached} attached, ${report.value.unmatched} unmatched`)
    file.value = null
    load()
  } catch (e) { toast('bad', 'Import failed', errorText(e)) }
  finally { uploading.value = false }
}
const count = (kind: string) => stats.value?.assets.find((a: any) => a.kind === kind)?.count ?? 0
const fetchCount = (s: string) => stats.value?.fetches.find((f: any) => f.status === s)?.count ?? 0
onMounted(load)
</script>

<template>
  <div class="page-header">
    <div class="page-title-group">
      <h1 class="page-title">Media library</h1>
      <span class="page-subtitle">Product images, datasheets and manuals. EDI price files carry links at most, so media arrives as packs, links or uploads.</span>
    </div>
    <button class="btn-secondary" @click="router.push({ path: '/catalogue', query: { noImage: 'true' } })"><Icon name="search" :size="15" /> Products without images</button>
  </div>

  <div class="stat-grid">
    <StatTile label="Products with an image" icon="checkCircle" tone="good" :value="fmt.compact(stats?.productsWithImage)" :loading="!stats" />
    <StatTile label="Images" icon="eye" :value="fmt.n(count('Image'))" :loading="!stats" />
    <StatTile label="Documents" icon="file" tone="info" :value="fmt.n((stats?.assets ?? []).filter((a: any) => a.kind !== 'Image').reduce((s: number, a: any) => s + a.count, 0))" :loading="!stats"
      :foot="`${count('Datasheet')} datasheets · ${count('Manual')} manuals`" />
    <StatTile label="Stored" icon="database" tone="warn" :value="fmt.bytes(stats?.totalBytes)" :loading="!stats"
      :foot="stats ? `${stats.storage.mode === 'Blob' ? 'Azure Blob (Azurite → ./.azurite)' : 'Disk (./.storage/media)'}` : ''" />
  </div>

  <div class="grid grid-main-side">
    <div class="stack">
      <div class="card">
        <div class="card-header"><h3 class="card-title"><Icon name="upload" :size="16" /> Import a supplier media pack</h3><span class="card-sub">.zip · up to 5,000 files</span></div>
        <div class="card-body stack">
          <label class="dropzone" :class="{ over, 'has-file': file }" style="margin: 0"
            @dragover.prevent="over = true" @dragleave="over = false" @drop.prevent="over = false; file = $event.dataTransfer?.files?.[0] ?? null">
            <span class="dropzone-icon"><Icon :name="file ? 'file' : 'upload'" :size="20" /></span>
            <b style="color: var(--text)">{{ file?.name ?? 'Drop a media pack .zip here, or click to choose' }}</b>
            <span>{{ file ? fmt.bytes(file.size) : 'Files named after the product: 5060000000009.jpg, 5060000000009_2.jpg, TH-PLUS-5_manual.pdf' }}</span>
            <input type="file" accept=".zip" hidden @change="file = ($event.target as HTMLInputElement).files?.[0] ?? null" />
          </label>
          <div v-if="uploading" class="stack" style="gap: .3rem">
            <div class="row" style="font-size: .8rem"><span class="muted">{{ progress < 100 ? 'Uploading' : 'Processing (matching, thumbnails, storing in Blob)…' }}</span><span class="spacer" /><b>{{ progress }}%</b></div>
            <div class="progress sm" :class="{ indeterminate: progress >= 100 }"><span :style="{ width: progress + '%' }" /></div>
          </div>
          <button class="btn-primary" :disabled="!file || uploading" @click="upload"><Icon :name="uploading ? 'refresh' : 'upload'" :size="15" :class="{ spin: uploading }" /> Import pack</button>
          <div class="alert info"><Icon name="info" :size="16" /><span>Matching order: <b>GTIN</b> → manufacturer part number → SKU. Files are checked by their bytes (JPEG, PNG, WebP, GIF, PDF only), de-duplicated by SHA-256 and thumbnailed. Demo pack: <code>samples/media/hvac-media-pack-300.zip</code></span></div>
        </div>
      </div>

      <div v-if="report" class="card">
        <div class="card-header">
          <h3 class="card-title">Import report</h3>
          <div class="chips">
            <span class="badge good">{{ report.attached }} attached</span><span class="badge">{{ report.duplicates }} duplicate</span>
            <span class="badge warn">{{ report.unmatched }} unmatched</span><span class="badge bad">{{ report.rejected }} rejected</span>
          </div>
        </div>
        <div class="toolbar"><div class="segmented">
          <button v-for="f in ['', 'Attached', 'Duplicate', 'Unmatched', 'Rejected']" :key="f" :class="{ active: filter === f }" @click="filter = f">{{ f || 'All' }}</button>
        </div></div>
        <div class="table-wrap" style="max-height: 380px">
          <table>
            <thead><tr><th>File</th><th>Result</th><th>Product</th><th>Kind</th></tr></thead>
            <tbody>
              <tr v-for="(i, n) in report.items.filter((x: any) => !filter || x.status === filter).slice(0, 500)" :key="n">
                <td><code>{{ i.file }}</code></td>
                <td><span class="badge" :class="i.status === 'Attached' ? 'good' : i.status === 'Rejected' ? 'bad' : i.status === 'Unmatched' ? 'warn' : ''">{{ i.status }}</span>
                  <div v-if="i.message" class="muted" style="font-size: .76rem">{{ i.message }}</div></td>
                <td>{{ i.sku ?? '—' }}</td>
                <td class="cell-muted">{{ i.kind ?? '—' }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>
    </div>

    <div class="card" style="align-self: start">
      <div class="card-header">
        <h3 class="card-title"><Icon name="link" :size="16" /> Links from supplier feeds</h3>
        <div class="chips"><span class="badge info">{{ fetchCount('Pending') }} pending</span><span class="badge good">{{ fetchCount('Done') }} done</span><span class="badge bad">{{ fetchCount('Failed') }} failed</span></div>
      </div>
      <div class="card-body" style="padding-bottom: 0"><p class="muted" style="font-size: .8rem">CSV <code>image_url</code>/<code>datasheet_url</code> and EDI <code>IMD+F+IMG</code> links are downloaded in the background with retries; private/loopback addresses are blocked (SSRF).</p></div>
      <div class="list">
        <div v-for="f in fetches" :key="f.id" class="list-item">
          <span class="list-icon"><Icon :name="f.status === 'Done' ? 'check' : f.status === 'Failed' ? 'x' : 'clock'" :size="14" /></span>
          <div class="list-main"><div class="list-title mono" style="font-size: .74rem">{{ f.url }}</div>
            <div class="list-sub">product {{ f.productId }} · {{ f.kind }} · {{ f.attempts }} attempt(s){{ f.lastError ? ' · ' + f.lastError : '' }}</div></div>
        </div>
        <EmptyState v-if="!fetches.length" icon="link" title="No media links received yet" />
      </div>
    </div>
  </div>
</template>
