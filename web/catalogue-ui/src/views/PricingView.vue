<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import Icon from '../components/Icon.vue'
import Pager from '../components/Pager.vue'
import EmptyState from '../components/EmptyState.vue'
import Overlay from '../components/Overlay.vue'
import { api, errorText, type CustomerAccount, type Paged, type PriceEntry, type PriceListInfo, type PriceQuote, type Product } from '../api'
import { session } from '../session'
import { fmt, toast } from '../ui'

const canEdit = session.can('catalogueAdmin')
const lists = ref<PriceListInfo[]>([])
const customers = ref<CustomerAccount[]>([])
const selectedListId = ref<number | null>(null)
const entries = ref<PriceEntry[]>([])
const entryTotal = ref(0)
const entryPage = ref(1)
const entryPageSize = ref(25)
const entrySearch = ref('')
const newEntry = ref({ sku: '', minQuantity: 1, unitPrice: undefined as number | undefined })
const editList = ref<(PriceListInfo & { isNew?: boolean }) | null>(null)
const showCustomer = ref(false)
const newCustomer = ref({ accountNumber: '', name: '', priceListId: null as number | null })

// Quote simulator
const quoteSearch = ref('HP-8KW-DEMO')
const quoteMatches = ref<Product[]>([])
const quoteProduct = ref<Product | null>(null)
const quoteCustomer = ref<number | ''>(session.customer?.id ?? '')
const quoteQty = ref(1)
const quote = ref<{ quote: PriceQuote; chain: { code: string; name: string; kind: string; discountPercent: number }[]; customer?: { name: string } } | null>(null)

const selectedList = computed(() => lists.value.find(l => l.id === selectedListId.value) ?? null)
const kindTone: Record<string, string> = { Customer: 'primary', Tier: 'info', Trade: 'good', Promotion: 'warn' }

/** The fallback path for a list: itself, then its parents. */
function chainOf(id?: number | null) {
  const out: PriceListInfo[] = []
  const seen = new Set<number>()
  while (id != null && !seen.has(id)) {
    seen.add(id)
    const l = lists.value.find(x => x.id === id)
    if (!l) break
    out.push(l)
    id = l.parentPriceListId
  }
  return out
}

async function loadLists() {
  lists.value = (await api.get<PriceListInfo[]>('/pricelists')).data
  customers.value = (await api.get<CustomerAccount[]>('/customers')).data
  if (!selectedListId.value) selectedListId.value = lists.value.find(l => l.entryCount > 0)?.id ?? lists.value[0]?.id ?? null
}
async function loadEntries() {
  if (!selectedListId.value) return
  const { data } = await api.get<Paged<PriceEntry>>(`/pricelists/${selectedListId.value}/entries`, {
    params: { search: entrySearch.value || undefined, page: entryPage.value, pageSize: entryPageSize.value }
  })
  entries.value = data.items
  entryTotal.value = data.total
}
let debounce: ReturnType<typeof setTimeout>
watch(selectedListId, () => { entryPage.value = 1; loadEntries() })
watch(entrySearch, () => { clearTimeout(debounce); debounce = setTimeout(() => { entryPage.value = 1; loadEntries() }, 300) })
watch([entryPage, entryPageSize], loadEntries)

async function addEntry() {
  if (!selectedListId.value || !newEntry.value.sku || newEntry.value.unitPrice == null) return
  try {
    await api.post(`/pricelists/${selectedListId.value}/entries`, newEntry.value)
    toast('good', 'Price break added', `${newEntry.value.sku.toUpperCase()} ${newEntry.value.minQuantity}+ @ ${fmt.money(newEntry.value.unitPrice)}`)
    newEntry.value = { sku: '', minQuantity: 1, unitPrice: undefined }
    await Promise.all([loadEntries(), loadLists()])
    runQuote()
  } catch (e) { toast('bad', 'Could not add entry', errorText(e)) }
}
async function removeEntry(e: PriceEntry) {
  try {
    await api.delete(`/pricelists/${selectedListId.value}/entries/${e.id}`)
    toast('info', 'Price break removed', e.sku)
    await Promise.all([loadEntries(), loadLists()])
  } catch (err) { toast('bad', 'Could not remove entry', errorText(err)) }
}

function startNewList() {
  editList.value = { id: 0, code: '', name: '', kind: 'Tier', currency: 'GBP', discountPercent: 10, isActive: true, entryCount: 0, customerCount: 0, isNew: true }
}
async function saveList() {
  const l = editList.value!
  try {
    const body = { code: l.code, name: l.name, kind: l.kind, discountPercent: l.discountPercent, parentPriceListId: l.parentPriceListId ?? null, isActive: l.isActive }
    if (l.isNew) await api.post('/pricelists', body)
    else await api.put(`/pricelists/${l.id}`, body)
    toast('good', 'Price list saved', l.name)
    editList.value = null
    await loadLists()
    runQuote()
  } catch (e) { toast('bad', 'Could not save price list', errorText(e)) }
}

async function assignList(c: CustomerAccount, priceListId: number | null) {
  try {
    await api.put(`/customers/${c.id}/pricelist`, null, { params: { priceListId: priceListId ?? undefined } })
    toast('good', 'Customer price list updated', c.name)
    await loadLists()
    runQuote()
  } catch (e) { toast('bad', 'Could not update customer', errorText(e)) }
}
async function createCustomer() {
  try {
    await api.post('/customers', newCustomer.value)
    toast('good', 'Customer created', newCustomer.value.name)
    showCustomer.value = false
    newCustomer.value = { accountNumber: '', name: '', priceListId: null }
    await loadLists()
  } catch (e) { toast('bad', 'Could not create customer', errorText(e)) }
}

// --- quote simulator ---
let qDebounce: ReturnType<typeof setTimeout>
watch(quoteSearch, q => {
  clearTimeout(qDebounce)
  qDebounce = setTimeout(async () => {
    if (!q || quoteProduct.value?.sku === q) return
    quoteMatches.value = (await api.get<Paged<Product>>('/products', { params: { search: q, pageSize: 6 } })).data.items
  }, 250)
})
function pickProduct(p: Product) {
  quoteProduct.value = p
  quoteSearch.value = p.sku
  quoteMatches.value = []
  runQuote()
}
async function runQuote() {
  if (!quoteProduct.value) return
  quote.value = (await api.get('/pricing/quote', {
    params: { productId: quoteProduct.value.id, customerId: quoteCustomer.value || undefined, qty: quoteQty.value }
  })).data
}
watch([quoteCustomer, quoteQty], runQuote)

onMounted(async () => {
  await loadLists()
  const found = (await api.get<Paged<Product>>('/products', { params: { search: quoteSearch.value, pageSize: 1 } })).data.items[0]
  if (found) pickProduct(found)
})
</script>

<template>
  <div class="page-header">
    <div class="page-title-group">
      <h1 class="page-title">Trade pricing</h1>
      <span class="page-subtitle">Customer → tier → trade price lists with quantity breaks. Explicit prices win; otherwise each list's discount applies to list price.</span>
    </div>
    <div class="page-actions">
      <button v-if="canEdit" class="btn-secondary" @click="showCustomer = true"><Icon name="user" :size="15" /> New customer</button>
      <button v-if="canEdit" class="btn-primary" @click="startNewList"><Icon name="plus" :size="15" /> New price list</button>
    </div>
  </div>

  <!-- Quote simulator -->
  <div class="card" style="margin-bottom: 1.1rem">
    <div class="card-header"><h3 class="card-title"><Icon name="sparkles" :size="16" /> Price a product</h3><span class="card-sub">What a customer pays and why</span></div>
    <div class="card-body">
      <div class="grid" style="grid-template-columns: minmax(220px, 2fr) minmax(200px, 2fr) 110px; align-items: end">
        <div class="field" style="position: relative">
          <label for="qs">Product</label>
          <div class="input-icon"><span class="icon"><Icon name="search" :size="15" /></span><input id="qs" v-model="quoteSearch" placeholder="SKU, MPN, GTIN or name" autocomplete="off" /></div>
          <div v-if="quoteMatches.length" class="menu" style="left: 0; right: auto; width: 100%">
            <button v-for="m in quoteMatches" :key="m.id" type="button" @click="pickProduct(m)">
              <div class="cell-title" style="text-align: left"><b>{{ m.name }}</b><small><code>{{ m.sku }}</code> · {{ fmt.money(m.price, m.currency) }}</small></div>
            </button>
          </div>
        </div>
        <div class="field">
          <label for="qc">Customer</label>
          <select id="qc" v-model="quoteCustomer">
            <option value="">No account (list price)</option>
            <option v-for="c in customers" :key="c.id" :value="c.id">{{ c.accountNumber }} · {{ c.name }}</option>
          </select>
        </div>
        <div class="field"><label for="qq">Quantity</label><input id="qq" v-model.number="quoteQty" type="number" min="1" /></div>
      </div>

      <div v-if="quote" class="grid mt-2" style="grid-template-columns: minmax(0, 1.2fr) minmax(0, 1fr); align-items: start">
        <div class="stack" style="gap: .8rem">
          <div class="row wrap" style="gap: .4rem; font-size: .82rem">
            <span class="muted">Resolution path:</span>
            <template v-for="(l, i) in quote.chain" :key="l.code">
              <span class="badge" :class="kindTone[l.kind] ?? ''" :style="quote.quote.priceListCode === l.code ? 'outline: 2px solid currentColor' : ''">{{ l.code }} · {{ l.discountPercent }}%</span>
              <Icon v-if="i < quote.chain.length - 1" name="arrowRight" :size="12" />
            </template>
            <Icon v-if="quote.chain.length" name="arrowRight" :size="12" />
            <span class="badge" :style="!quote.quote.priceListCode ? 'outline: 2px solid currentColor' : ''">List price</span>
          </div>
          <div class="grid grid-3" style="gap: .7rem">
            <div class="stat-card"><span class="stat-label">List price</span><span class="stat-value" style="font-size: 1.25rem">{{ fmt.money(quote.quote.listPrice, quote.quote.currency) }}</span></div>
            <div class="stat-card"><span class="stat-label">Net unit price</span><span class="stat-value" style="font-size: 1.25rem; color: var(--good)">{{ fmt.money(quote.quote.netUnitPrice, quote.quote.currency) }}</span>
              <span v-if="quote.quote.savingPercent" class="stat-foot"><span class="up">−{{ quote.quote.savingPercent }}%</span></span></div>
            <div class="stat-card"><span class="stat-label">Line total</span><span class="stat-value" style="font-size: 1.25rem">{{ fmt.money(quote.quote.lineTotal, quote.quote.currency) }}</span></div>
          </div>
          <div class="alert info"><Icon name="info" :size="16" /><span>Priced by <b>{{ quote.quote.source }}</b><template v-if="quote.quote.priceListCode"> (<code>{{ quote.quote.priceListCode }}</code>)</template>.</span></div>
        </div>
        <table>
          <thead><tr><th>From qty</th><th class="num">Unit price</th></tr></thead>
          <tbody>
            <tr v-for="b in quote.quote.breaks" :key="b.minQuantity"><td>{{ b.minQuantity }}+</td><td class="num cell-strong">{{ fmt.money(b.unitPrice, quote.quote.currency) }}</td></tr>
            <tr v-if="!quote.quote.breaks.length"><td colspan="2" class="muted">No price.</td></tr>
          </tbody>
        </table>
      </div>
    </div>
  </div>

  <div class="grid" style="grid-template-columns: minmax(280px, 360px) minmax(0, 1fr); align-items: start">
    <div class="stack">
      <div class="card">
        <div class="card-header"><h3 class="card-title"><Icon name="layers" :size="16" /> Price lists</h3></div>
        <div class="list">
          <div v-for="l in lists" :key="l.id" class="list-item" style="cursor: pointer" :style="selectedListId === l.id ? 'background: var(--primary-soft)' : ''" @click="selectedListId = l.id">
            <div class="list-main">
              <div class="list-title">{{ l.name }} <span v-if="!l.isActive" class="badge">Inactive</span></div>
              <div class="list-sub">
                <span class="badge" :class="kindTone[l.kind] ?? ''" style="font-size: .66rem">{{ l.kind }}</span>
                {{ l.discountPercent }}% off list<template v-if="l.parentCode"> · falls back to {{ l.parentCode }}</template>
              </div>
              <div class="list-sub">{{ l.entryCount }} explicit price{{ l.entryCount === 1 ? '' : 's' }} · {{ l.customerCount }} customer{{ l.customerCount === 1 ? '' : 's' }}</div>
            </div>
            <button v-if="canEdit" class="btn-ghost btn-sm" aria-label="Edit price list" @click.stop="editList = { ...l }"><Icon name="edit" :size="14" /></button>
          </div>
        </div>
      </div>

      <div class="card">
        <div class="card-header"><h3 class="card-title"><Icon name="user" :size="16" /> Customer accounts</h3></div>
        <div class="list">
          <div v-for="c in customers" :key="c.id" class="list-item">
            <div class="list-main">
              <div class="list-title">{{ c.name }}</div>
              <div class="list-sub"><code>{{ c.accountNumber }}</code> · {{ chainOf(c.priceListId).map(l => l.code).join(' → ') || 'list price' }}</div>
            </div>
            <select v-if="canEdit" :value="c.priceListId ?? ''" class="btn-sm" style="max-width: 130px; font-size: .78rem" aria-label="Price list"
              @change="assignList(c, ($event.target as HTMLSelectElement).value ? Number(($event.target as HTMLSelectElement).value) : null)">
              <option value="">List price</option>
              <option v-for="l in lists" :key="l.id" :value="l.id">{{ l.code }}</option>
            </select>
          </div>
        </div>
      </div>
    </div>

    <div class="card">
      <div class="card-header">
        <div>
          <h3 class="card-title">{{ selectedList?.name ?? 'Entries' }}</h3>
          <div class="card-sub">Explicit net prices and quantity breaks; products without one get {{ selectedList?.discountPercent ?? 0 }}% off list<template v-if="selectedList?.parentCode">, after checking {{ selectedList.parentCode }}</template>.</div>
        </div>
        <div class="input-icon" style="width: 220px"><span class="icon"><Icon name="search" :size="15" /></span><input v-model="entrySearch" placeholder="SKU or name…" /></div>
      </div>
      <form v-if="canEdit && selectedList" class="toolbar" style="background: var(--surface-2)" @submit.prevent="addEntry">
        <input v-model="newEntry.sku" placeholder="SKU" required style="width: 170px; text-transform: uppercase" aria-label="SKU" />
        <input v-model.number="newEntry.minQuantity" type="number" min="1" style="width: 90px" aria-label="Minimum quantity" title="From quantity" />
        <input v-model.number="newEntry.unitPrice" type="number" min="0" step="0.01" placeholder="Unit price" required style="width: 120px" aria-label="Unit price" />
        <button class="btn-primary btn-sm" type="submit"><Icon name="plus" :size="14" /> Add price break</button>
      </form>
      <div class="table-wrap">
        <table>
          <thead><tr><th>Product</th><th class="num">From qty</th><th class="num">Net price</th><th class="num">List</th><th class="num">Discount</th><th>Valid from</th><th v-if="canEdit"></th></tr></thead>
          <tbody>
            <tr v-for="e in entries" :key="e.id">
              <td><div class="cell-title"><span class="cell-strong">{{ e.productName }}</span><small><code>{{ e.sku }}</code></small></div></td>
              <td class="num">{{ e.minQuantity }}+</td>
              <td class="num cell-strong">{{ fmt.money(e.unitPrice) }}</td>
              <td class="num cell-muted">{{ fmt.money(e.listPrice) }}</td>
              <td class="num" style="color: var(--good)">{{ e.listPrice ? `−${(((e.listPrice - e.unitPrice) / e.listPrice) * 100).toFixed(1)}%` : '—' }}</td>
              <td class="cell-muted">{{ fmt.date(e.validFromUtc) }}</td>
              <td v-if="canEdit" class="num"><button class="btn-ghost btn-sm" aria-label="Remove" @click="removeEntry(e)"><Icon name="trash" :size="14" style="color: var(--bad)" /></button></td>
            </tr>
            <tr v-if="!entries.length"><td colspan="7"><EmptyState icon="tag" title="No explicit prices" :text="`Every product gets ${selectedList?.discountPercent ?? 0}% off list on this price list.`" /></td></tr>
          </tbody>
        </table>
      </div>
      <Pager v-if="entryTotal > 0" v-model:page="entryPage" v-model:page-size="entryPageSize" :total="entryTotal" noun="entries" />
    </div>
  </div>

  <Overlay v-if="editList" :title="editList.isNew ? 'New price list' : `Edit ${editList.code}`" @close="editList = null">
    <form id="pl" class="stack" @submit.prevent="saveList">
      <div class="form-grid" style="grid-template-columns: 1fr 2fr">
        <div class="field"><label for="plc">Code</label><input id="plc" v-model="editList.code" :disabled="!editList.isNew" required style="text-transform: uppercase" /></div>
        <div class="field"><label for="pln">Name</label><input id="pln" v-model="editList.name" required /></div>
      </div>
      <div class="form-grid">
        <div class="field">
          <label for="plk">Kind</label>
          <select id="plk" v-model="editList.kind" :disabled="!editList.isNew"><option>Trade</option><option>Tier</option><option>Customer</option><option>Promotion</option></select>
        </div>
        <div class="field"><label for="pld">Discount off list (%)</label><input id="pld" v-model.number="editList.discountPercent" type="number" min="0" max="100" step="0.5" required /></div>
        <div class="field">
          <label for="plp">Falls back to</label>
          <select id="plp" v-model="editList.parentPriceListId">
            <option :value="undefined">— None (list price) —</option>
            <option v-for="l in lists.filter(x => x.id !== editList!.id)" :key="l.id" :value="l.id">{{ l.code }}</option>
          </select>
        </div>
      </div>
      <label class="row" style="font-weight: 500"><input v-model="editList.isActive" type="checkbox" /> Active</label>
    </form>
    <template #footer>
      <button class="btn-secondary" @click="editList = null">Cancel</button>
      <button class="btn-primary" type="submit" form="pl"><Icon name="check" :size="15" /> Save</button>
    </template>
  </Overlay>

  <Overlay v-if="showCustomer" title="New customer account" @close="showCustomer = false">
    <form id="nc" class="stack" @submit.prevent="createCustomer">
      <div class="form-grid" style="grid-template-columns: 1fr 2fr">
        <div class="field"><label for="nca">Account no.</label><input id="nca" v-model="newCustomer.accountNumber" required placeholder="ACC-2001" style="text-transform: uppercase" /></div>
        <div class="field"><label for="ncn">Name</label><input id="ncn" v-model="newCustomer.name" required placeholder="Installer Ltd" /></div>
      </div>
      <div class="field">
        <label for="ncp">Price list</label>
        <select id="ncp" v-model="newCustomer.priceListId"><option :value="null">List price</option><option v-for="l in lists" :key="l.id" :value="l.id">{{ l.code }} · {{ l.name }}</option></select>
      </div>
    </form>
    <template #footer>
      <button class="btn-secondary" @click="showCustomer = false">Cancel</button>
      <button class="btn-primary" type="submit" form="nc"><Icon name="plus" :size="15" /> Create</button>
    </template>
  </Overlay>
</template>
