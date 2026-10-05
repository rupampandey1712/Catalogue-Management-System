<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import Icon from '../components/Icon.vue'
import Pager from '../components/Pager.vue'
import StatTile from '../components/StatTile.vue'
import EmptyState from '../components/EmptyState.vue'
import Overlay from '../components/Overlay.vue'
import { api, errorText, type ChangeRequest, type Paged, type Supplier } from '../api'
import { session } from '../session'
import { fmt, toast } from '../ui'

const canDecide = session.can('catalogueAdmin')
const items = ref<ChangeRequest[]>([])
const total = ref(0)
const loading = ref(true)
const status = ref<'Pending' | 'Approved' | 'Rejected'>('Pending')
const kind = ref('')
const supplierId = ref<number | ''>('')
const search = ref('')
const page = ref(1)
const pageSize = ref(25)
const selection = ref<Set<number>>(new Set())
const note = ref('')
const busy = ref(false)
const summary = ref<{ totalPending: number; decided24h: number; pending: { kind: string; supplierId: number; supplierCode: string; count: number }[] }>()
const suppliers = ref<Supplier[]>([])
const bulk = ref<{ approve: boolean; done: number; remaining: number; running: boolean } | null>(null)

async function load() {
  loading.value = true
  try {
    const { data } = await api.get<Paged<ChangeRequest>>('/approvals', {
      params: { status: status.value, kind: kind.value || undefined, supplierId: supplierId.value || undefined, search: search.value || undefined, page: page.value, pageSize: pageSize.value }
    })
    items.value = data.items
    total.value = data.total
    selection.value = new Set([...selection.value].filter(id => data.items.some(i => i.id === id)))
  } finally {
    loading.value = false
  }
  summary.value = (await api.get('/approvals/summary')).data
}

let debounce: ReturnType<typeof setTimeout>
watch([status, kind, supplierId], () => { page.value = 1; selection.value = new Set(); load() })
watch(search, () => { clearTimeout(debounce); debounce = setTimeout(() => { page.value = 1; load() }, 300) })
watch([page, pageSize], load)
onMounted(async () => {
  load()
  if (session.can('supplierAdmin')) suppliers.value = (await api.get<Supplier[]>('/suppliers')).data
})

const pendingBy = (k: string) => summary.value?.pending.filter(p => p.kind === k).reduce((a, p) => a + p.count, 0) ?? 0
const allSelected = computed(() => items.value.length > 0 && items.value.every(i => selection.value.has(i.id)))
function toggleAll() {
  selection.value = allSelected.value ? new Set() : new Set(items.value.map(i => i.id))
}
function toggle(id: number) {
  const s = new Set(selection.value)
  s.has(id) ? s.delete(id) : s.add(id)
  selection.value = s
}

async function decide(ids: number[], approve: boolean) {
  busy.value = true
  try {
    const { data } = await api.post('/approvals/decide', { ids, approve, note: note.value || null })
    toast('good', `${data.decided} change(s) ${approve ? 'approved' : 'rejected'}`)
    selection.value = new Set()
    note.value = ''
    await load()
  } catch (e) { toast('bad', 'Decision failed', errorText(e)) }
  finally { busy.value = false }
}

// "Approve all matching" can cover hundreds of thousands of rows; the API decides up to 20k per
// call and reports what is left, so the UI keeps calling and shows progress.
async function decideAll(approve: boolean) {
  bulk.value = { approve, done: 0, remaining: total.value, running: true }
  try {
    while (bulk.value.running) {
      const { data } = await api.post('/approvals/decide-matching', {
        kind: kind.value || null, supplierId: supplierId.value || null, approve, note: note.value || null
      })
      bulk.value.done += data.decided
      bulk.value.remaining = data.remaining
      if (data.remaining === 0 || data.decided === 0) break
    }
    toast('good', `${fmt.n(bulk.value.done)} change(s) ${approve ? 'approved' : 'rejected'}`)
  } catch (e) { toast('bad', 'Bulk decision stopped', errorText(e)) }
  finally {
    bulk.value = null
    await load()
  }
}

const pct = (c?: number) => (c == null ? '' : `${c > 0 ? '+' : ''}${c.toFixed(1)}%`)
</script>

<template>
  <div class="page-header">
    <div class="page-title-group">
      <h1 class="page-title">Approvals</h1>
      <span class="page-subtitle">Supplier-fed changes held for review: new products and list-price moves beyond each supplier's threshold.</span>
    </div>
    <router-link to="/suppliers" class="btn btn-secondary"><Icon name="truck" :size="15" /> Supplier policies</router-link>
  </div>

  <div class="stat-grid">
    <StatTile label="Pending review" icon="clock" tone="warn" :value="fmt.n(summary?.totalPending)" :loading="!summary" />
    <StatTile label="New products" icon="box" tone="info" :value="fmt.n(pendingBy('NewProduct'))" :loading="!summary" foot="Hidden from buyers until approved" />
    <StatTile label="Price changes" icon="tag" :value="fmt.n(pendingBy('PriceChange'))" :loading="!summary" foot="Old price stays live until approved" />
    <StatTile label="Decided (24h)" icon="checkCircle" tone="good" :value="fmt.n(summary?.decided24h)" :loading="!summary" />
  </div>

  <div class="card">
    <div class="toolbar">
      <div class="segmented">
        <button v-for="s in ['Pending', 'Approved', 'Rejected'] as const" :key="s" :class="{ active: status === s }" @click="status = s">{{ s }}</button>
      </div>
      <select v-model="kind" aria-label="Change type">
        <option value="">All changes</option><option value="NewProduct">New products</option><option value="PriceChange">Price changes</option>
      </select>
      <select v-if="suppliers.length" v-model="supplierId" aria-label="Supplier">
        <option value="">All suppliers</option>
        <option v-for="s in suppliers" :key="s.id" :value="s.id">{{ s.code }} — {{ s.name }}</option>
      </select>
      <div class="input-icon" style="width: 220px"><span class="icon"><Icon name="search" :size="15" /></span><input v-model="search" placeholder="SKU or name…" /></div>
      <span class="spacer" />
      <button class="btn-ghost btn-sm" aria-label="Refresh" @click="load"><Icon name="refresh" :size="14" :class="{ spin: loading }" /></button>
    </div>

    <div v-if="canDecide && status === 'Pending' && total > 0" class="toolbar" style="background: var(--surface-2)">
      <input v-model="note" placeholder="Decision note (optional, recorded in the audit log)" style="flex: 1; max-width: 420px" />
      <template v-if="selection.size">
        <b style="font-size: .84rem">{{ selection.size }} selected</b>
        <button class="btn-primary btn-sm" :disabled="busy" @click="decide([...selection], true)"><Icon name="check" :size="14" /> Approve</button>
        <button class="btn-danger btn-sm" :disabled="busy" @click="decide([...selection], false)"><Icon name="x" :size="14" /> Reject</button>
      </template>
      <span class="spacer" />
      <button class="btn-secondary btn-sm" :disabled="busy || !!bulk" @click="decideAll(true)">Approve all {{ fmt.compact(total) }} matching</button>
      <button class="btn-ghost btn-sm" :disabled="busy || !!bulk" @click="decideAll(false)">Reject all</button>
    </div>

    <div class="table-wrap">
      <table>
        <thead>
          <tr>
            <th v-if="canDecide && status === 'Pending'" style="width: 36px"><input type="checkbox" :checked="allSelected" aria-label="Select page" @change="toggleAll" /></th>
            <th>Product</th><th>Supplier</th><th>Change</th><th>Source</th><th>{{ status === 'Pending' ? 'Received' : 'Decided' }}</th>
            <th v-if="canDecide && status === 'Pending'"></th>
          </tr>
        </thead>
        <tbody>
          <template v-if="loading && !items.length"><tr v-for="i in 6" :key="i"><td v-for="j in 6" :key="j"><span class="skeleton" /></td></tr></template>
          <tr v-for="r in items" :key="r.id" :class="{ selected: selection.has(r.id) }">
            <td v-if="canDecide && status === 'Pending'"><input type="checkbox" :checked="selection.has(r.id)" :aria-label="`Select ${r.productSku}`" @change="toggle(r.id)" /></td>
            <td style="max-width: 320px">
              <div class="cell-title">
                <span class="cell-strong" style="white-space: nowrap; overflow: hidden; text-overflow: ellipsis">{{ r.productName }}</span>
                <small><code>{{ r.productSku }}</code><template v-if="r.brand"> · {{ r.brand }}</template></small>
              </div>
            </td>
            <td><span class="tag">{{ r.supplierCode }}</span></td>
            <td>
              <span v-if="r.kind === 'NewProduct'" class="badge info"><span class="dot" />New product</span>
              <div v-else class="cell-title">
                <span class="tabular"><span class="muted" style="text-decoration: line-through">{{ fmt.money(r.oldPrice, r.currency) }}</span> → <b>{{ fmt.money(r.newPrice, r.currency) }}</b></span>
                <small :style="{ color: (r.changePercent ?? 0) > 0 ? 'var(--bad)' : 'var(--good)', fontWeight: 600 }">
                  <Icon :name="(r.changePercent ?? 0) > 0 ? 'trendUp' : 'arrowRight'" :size="11" /> {{ pct(r.changePercent) }} list price
                </small>
              </div>
            </td>
            <td><router-link v-if="r.importId" :to="`/imports/${r.importId}`" class="mono">import #{{ r.importId }}</router-link><span v-else class="muted">—</span></td>
            <td class="cell-muted" style="white-space: nowrap">
              <template v-if="status === 'Pending'">{{ fmt.ago(r.createdAtUtc) }}</template>
              <div v-else class="cell-title"><span>{{ fmt.ago(r.decidedAtUtc) }}</span><small>by {{ r.decidedBy }}<template v-if="r.decisionNote"> · “{{ r.decisionNote }}”</template></small></div>
            </td>
            <td v-if="canDecide && status === 'Pending'" class="num">
              <div class="row" style="justify-content: flex-end; gap: .2rem">
                <button class="btn-ghost btn-sm" title="Approve" :disabled="busy" @click="decide([r.id], true)"><Icon name="check" :size="15" style="color: var(--good)" /></button>
                <button class="btn-ghost btn-sm" title="Reject" :disabled="busy" @click="decide([r.id], false)"><Icon name="x" :size="15" style="color: var(--bad)" /></button>
              </div>
            </td>
          </tr>
          <tr v-if="!loading && !items.length">
            <td colspan="7"><EmptyState icon="checkCircle" :title="status === 'Pending' ? 'Nothing waiting for review' : `No ${status.toLowerCase()} changes`"
              :text="status === 'Pending' ? 'Supplier feeds within policy are applied automatically.' : undefined" /></td>
          </tr>
        </tbody>
      </table>
    </div>
    <Pager v-model:page="page" v-model:page-size="pageSize" :total="total" noun="changes" :sizes="[25, 50, 100, 200]" />
  </div>

  <Overlay v-if="bulk" :title="bulk.approve ? 'Approving changes…' : 'Rejecting changes…'" @close="bulk && (bulk.running = false)">
    <div class="stack">
      <div class="row"><b>{{ fmt.n(bulk.done) }}</b> decided<span class="spacer" /><span class="muted">{{ fmt.n(bulk.remaining) }} remaining</span></div>
      <div class="progress"><span :style="{ width: fmt.pct(bulk.done, bulk.done + bulk.remaining) + '%' }" /></div>
      <p class="muted" style="font-size: .82rem">Processed in batches of 20,000 per request. Closing this stops after the current batch.</p>
    </div>
  </Overlay>
</template>
