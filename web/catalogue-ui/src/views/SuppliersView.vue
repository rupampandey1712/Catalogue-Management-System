<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import Icon from '../components/Icon.vue'
import EmptyState from '../components/EmptyState.vue'
import Overlay from '../components/Overlay.vue'
import { api, errorText, type Supplier } from '../api'
import { fmt, toast } from '../ui'

const suppliers = ref<Supplier[]>([])
const loading = ref(true)
const showAdd = ref(false)
const form = ref({ code: '', name: '', integrationType: 'Csv' })
const saving = ref(false)
const filter = ref('')
const editing = ref<{ id: number; code: string; name: string; mappingVersion: string; isActive: boolean;
  requireApprovalForNewProducts: boolean; threshold: number | '' } | null>(null)

function edit(s: Supplier) {
  editing.value = { id: s.id, code: s.code, name: s.name, mappingVersion: s.mappingVersion ?? '', isActive: s.isActive,
    requireApprovalForNewProducts: s.requireApprovalForNewProducts, threshold: s.priceChangeApprovalThresholdPercent ?? '' }
}
async function savePolicy() {
  const e = editing.value!
  saving.value = true
  try {
    await api.put(`/suppliers/${e.id}`, {
      name: e.name, mappingVersion: e.mappingVersion || null, isActive: e.isActive,
      requireApprovalForNewProducts: e.requireApprovalForNewProducts,
      priceChangeApprovalThresholdPercent: e.threshold === '' ? null : e.threshold
    })
    toast('good', 'Supplier updated', `${e.code}: policy applies to the next feed immediately`)
    editing.value = null
    load()
  } catch (err) {
    toast('bad', 'Could not update supplier', errorText(err))
  } finally {
    saving.value = false
  }
}
const view = ref<'grid' | 'table'>('grid')

const integrations = [
  { value: 'Csv', label: 'CSV file', icon: 'file', text: 'Spreadsheet exports uploaded through Imports.' },
  { value: 'Edi', label: 'EDI / PRICAT', icon: 'zap', text: 'EDIFACT interchanges — streamed via Event Hub at scale.' },
  { value: 'Rest', label: 'REST API', icon: 'link', text: 'Pulled from the supplier API through an adapter.' },
  { value: 'Manual', label: 'Manual', icon: 'edit', text: 'Products maintained by hand in the catalogue.' }
]
const meta = (t: string) => integrations.find(i => i.value === t) ?? integrations[3]

const filtered = computed(() => {
  const q = filter.value.toLowerCase()
  return suppliers.value.filter(s => !q || s.code.toLowerCase().includes(q) || s.name.toLowerCase().includes(q))
})
const totals = computed(() => ({
  products: suppliers.value.reduce((a, s) => a + s.productCount, 0),
  imports: suppliers.value.reduce((a, s) => a + s.importCount, 0)
}))

async function load() {
  loading.value = true
  try { suppliers.value = (await api.get<Supplier[]>('/suppliers')).data }
  finally { loading.value = false }
}

async function add() {
  saving.value = true
  try {
    await api.post('/suppliers', form.value)
    toast('good', 'Supplier registered', form.value.code.toUpperCase())
    form.value = { code: '', name: '', integrationType: 'Csv' }
    showAdd.value = false
    load()
  } catch (e) {
    toast('bad', 'Could not register supplier', errorText(e))
  } finally {
    saving.value = false
  }
}
onMounted(load)
</script>

<template>
  <div class="page-header">
    <div class="page-title-group">
      <h1 class="page-title">Suppliers</h1>
      <span class="page-subtitle">{{ suppliers.length }} partners · {{ fmt.compact(totals.products) }} products · {{ fmt.n(totals.imports) }} imports</span>
    </div>
    <div class="page-actions">
      <div class="input-icon" style="width: 240px"><span class="icon"><Icon name="search" :size="15" /></span><input v-model="filter" placeholder="Filter suppliers…" /></div>
      <div class="segmented">
        <button :class="{ active: view === 'grid' }" aria-label="Card view" @click="view = 'grid'"><Icon name="grid" :size="15" /></button>
        <button :class="{ active: view === 'table' }" aria-label="Table view" @click="view = 'table'"><Icon name="menu" :size="15" /></button>
      </div>
      <button class="btn-primary" @click="showAdd = true"><Icon name="plus" :size="15" /> Add supplier</button>
    </div>
  </div>

  <div v-if="loading" class="grid grid-3"><div v-for="i in 3" :key="i" class="card card-pad"><span class="skeleton" style="height: 120px" /></div></div>

  <div v-else-if="view === 'grid'" class="grid" style="grid-template-columns: repeat(auto-fill, minmax(290px, 1fr))">
    <div v-for="s in filtered" :key="s.id" class="card card-pad stack" style="gap: .9rem">
      <div class="row">
        <span class="stat-icon" :class="s.integrationType === 'Edi' ? 'warn' : s.integrationType === 'Rest' ? 'info' : ''" style="width: 40px; height: 40px">
          <Icon :name="meta(s.integrationType).icon" :size="18" />
        </span>
        <div style="min-width: 0">
          <div class="cell-strong" style="white-space: nowrap; overflow: hidden; text-overflow: ellipsis">{{ s.name }}</div>
          <div class="row" style="gap: .4rem"><code>{{ s.code }}</code><span class="muted" style="font-size: .76rem">{{ meta(s.integrationType).label }}</span></div>
        </div>
        <span class="spacer" />
        <span class="badge" :class="s.isActive ? 'good' : ''"><span class="dot" />{{ s.isActive ? 'Active' : 'Inactive' }}</span>
      </div>
      <div class="chips">
        <span v-if="s.requireApprovalForNewProducts" class="tag" title="New products from this supplier wait for approval"><Icon name="shield" :size="11" />&nbsp;Approve new products</span>
        <span v-if="s.priceChangeApprovalThresholdPercent != null" class="tag" title="List-price moves beyond this are held for approval"><Icon name="tag" :size="11" />&nbsp;Price moves &gt; {{ s.priceChangeApprovalThresholdPercent }}%</span>
        <span v-if="!s.requireApprovalForNewProducts && s.priceChangeApprovalThresholdPercent == null" class="tag">Auto-apply feed</span>
        <router-link v-if="s.pendingApprovals" to="/approvals" class="badge warn"><span class="dot" />{{ fmt.n(s.pendingApprovals) }} pending</router-link>
      </div>
      <div class="grid grid-2" style="gap: .6rem">
        <div><div class="stat-label">Products</div><div class="cell-strong tabular" style="font-size: 1.15rem">{{ fmt.compact(s.productCount) }}</div></div>
        <div><div class="stat-label">Offers</div><div class="cell-strong tabular" style="font-size: 1.15rem">{{ fmt.compact(s.offerCount) }}</div></div>
      </div>
      <div class="row" style="font-size: .78rem; color: var(--muted); border-top: 1px solid var(--border); padding-top: .75rem">
        <Icon name="clock" :size="13" /> Last import {{ fmt.ago(s.lastImportUtc) }}
        <span class="spacer" />
        <span v-if="s.mappingVersion" class="tag">{{ s.mappingVersion }}</span>
      </div>
      <div class="row" style="gap: .4rem">
        <button class="btn-secondary btn-sm" style="flex: 1" @click="edit(s)"><Icon name="edit" :size="13" /> Policy</button>
        <router-link :to="{ path: '/catalogue', query: { supplier: s.id } }" class="btn btn-secondary btn-sm" style="flex: 1">Products</router-link>
        <router-link :to="s.integrationType === 'Edi' ? '/event-hub' : '/imports'" class="btn btn-secondary btn-sm" style="flex: 1">
          {{ s.integrationType === 'Edi' ? 'Stream EDI' : 'Import' }}
        </router-link>
      </div>
    </div>
    <div v-if="!filtered.length" class="card" style="grid-column: 1 / -1"><EmptyState icon="truck" title="No suppliers found" text="Adjust the filter or register a new supplier." /></div>
  </div>

  <div v-else class="card">
    <div class="table-wrap">
      <table>
        <thead><tr><th>Supplier</th><th>Integration</th><th>Mapping</th><th class="num">Products</th><th class="num">Offers</th><th>Approval policy</th><th>Last import</th><th>Status</th><th></th></tr></thead>
        <tbody>
          <tr v-for="s in filtered" :key="s.id">
            <td><div class="cell-title"><span class="cell-strong">{{ s.name }}</span><small><code>{{ s.code }}</code></small></div></td>
            <td><span class="row" style="gap: .4rem"><Icon :name="meta(s.integrationType).icon" :size="14" />{{ meta(s.integrationType).label }}</span></td>
            <td class="cell-muted">{{ s.mappingVersion ?? '—' }}</td>
            <td class="num">{{ fmt.n(s.productCount) }}</td>
            <td class="num">{{ fmt.n(s.offerCount) }}</td>
            <td class="cell-muted" style="font-size: .8rem">{{ [s.requireApprovalForNewProducts ? 'new products' : '', s.priceChangeApprovalThresholdPercent != null ? `price > ${s.priceChangeApprovalThresholdPercent}%` : ''].filter(Boolean).join(', ') || 'auto-apply' }}</td>
            <td class="cell-muted">{{ fmt.ago(s.lastImportUtc) }}</td>
            <td><span class="badge" :class="s.isActive ? 'good' : ''"><span class="dot" />{{ s.isActive ? 'Active' : 'Inactive' }}</span></td>
            <td class="num"><button class="btn-ghost btn-sm" aria-label="Edit policy" @click="edit(s)"><Icon name="edit" :size="14" /></button></td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>

  <Overlay v-if="editing" :title="`${editing.code} settings`" subtitle="Approval policy is applied by both import pipelines on the next row they process." @close="editing = null">
    <form id="sp" class="stack" @submit.prevent="savePolicy">
      <div class="form-grid" style="grid-template-columns: 2fr 1fr">
        <div class="field"><label for="spn">Name</label><input id="spn" v-model="editing.name" required /></div>
        <div class="field"><label for="spm">Mapping version</label><input id="spm" v-model="editing.mappingVersion" placeholder="e.g. SUPA-v3" /></div>
      </div>
      <label class="row" style="font-weight: 500"><input v-model="editing.isActive" type="checkbox" /> Active (inactive suppliers cannot import)</label>
      <div class="card card-pad stack" style="gap: .8rem; box-shadow: none; background: var(--surface-2)">
        <b style="font-size: .88rem"><Icon name="shield" :size="14" /> Approval policy</b>
        <label class="row" style="font-weight: 500; margin: 0">
          <input v-model="editing.requireApprovalForNewProducts" type="checkbox" />
          New products from this supplier wait for catalogue-admin approval (hidden from buyers until then)
        </label>
        <div class="field">
          <label for="spt">Hold list-price changes larger than (%)</label>
          <input id="spt" v-model.number="editing.threshold" type="number" min="0" max="1000" step="0.5" placeholder="Blank = apply all price changes automatically" style="max-width: 320px" />
          <span class="hint">The current price stays live until the change is approved.</span>
        </div>
      </div>
    </form>
    <template #footer>
      <button class="btn-secondary" @click="editing = null">Cancel</button>
      <button class="btn-primary" type="submit" form="sp" :disabled="saving"><Icon name="check" :size="15" /> Save</button>
    </template>
  </Overlay>

  <Overlay v-if="showAdd" title="Register supplier" subtitle="Add a trading partner and choose how its catalogue arrives." @close="showAdd = false">
    <form id="add-supplier" class="stack" @submit.prevent="add">
      <div class="form-grid" style="grid-template-columns: 1fr 2fr">
        <div class="field"><label for="code">Code</label><input id="code" v-model="form.code" required maxlength="32" placeholder="SUP-01" style="text-transform: uppercase" /></div>
        <div class="field"><label for="sname">Name</label><input id="sname" v-model="form.name" required placeholder="Supplier name" /></div>
      </div>
      <div>
        <label>Integration</label>
        <div class="grid grid-2" style="gap: .5rem">
          <button v-for="i in integrations" :key="i.value" type="button" class="role-option" :class="{ active: form.integrationType === i.value }" @click="form.integrationType = i.value">
            <span class="row" style="gap: .4rem"><Icon :name="i.icon" :size="14" />{{ i.label }}</span><small>{{ i.text }}</small>
          </button>
        </div>
      </div>
    </form>
    <template #footer>
      <button class="btn-secondary" @click="showAdd = false">Cancel</button>
      <button class="btn-primary" type="submit" form="add-supplier" :disabled="saving"><Icon name="plus" :size="15" /> Register</button>
    </template>
  </Overlay>
</template>
