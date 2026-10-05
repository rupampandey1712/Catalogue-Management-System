<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import Icon from '../components/Icon.vue'
import Pager from '../components/Pager.vue'
import EmptyState from '../components/EmptyState.vue'
import AvailabilityBadge from '../components/AvailabilityBadge.vue'
import Overlay from '../components/Overlay.vue'
import { api, errorText, type AttributeDef, type Category, type CustomerAccount, type Paged, type SellerOffer, type Supplier, type TemplateItem } from '../api'
import { session } from '../session'
import { fmt, toast } from '../ui'

const isStaff = session.can('supplierAdmin')
const suppliers = ref<Supplier[]>([])
const supplierId = ref<number | ''>(session.user?.supplierId ?? '')
const tab = ref<'offers' | 'discounts'>('offers')

// ----- offers -----
const offers = ref<SellerOffer[]>([])
const total = ref(0)
const page = ref(1)
const pageSize = ref(25)
const search = ref('')
const undercut = ref(false)
const loading = ref(true)
const editing = ref<Record<number, { price?: number; stock?: number; leadTimeDays?: number }>>({})

async function loadOffers() {
  if (!supplierId.value) { loading.value = false; return }
  loading.value = true
  try {
    const { data } = await api.get<Paged<SellerOffer>>('/seller/offers', {
      params: { supplierId: isStaff ? supplierId.value : undefined, search: search.value || undefined, undercut: undercut.value || undefined, page: page.value, pageSize: pageSize.value }
    })
    offers.value = data.items
    total.value = data.total
    editing.value = {}
  } finally { loading.value = false }
}
let debounce: ReturnType<typeof setTimeout>
watch(search, () => { clearTimeout(debounce); debounce = setTimeout(() => { page.value = 1; loadOffers() }, 300) })
watch([undercut, supplierId], () => { page.value = 1; loadOffers(); loadDiscounts() })
watch([page, pageSize], loadOffers)

function edit(o: SellerOffer) { editing.value[o.id] = { price: o.price ?? o.listPrice, stock: o.stock ?? 0, leadTimeDays: o.leadTimeDays } }
async function save(o: SellerOffer) {
  const e = editing.value[o.id]
  try {
    const { data } = await api.put(`/seller/offers/${o.id}`, {
      price: e.price, quantity: e.stock, leadTimeDays: e.leadTimeDays, minOrderQuantity: o.minOrderQuantity, packSize: o.packSize, discontinued: false
    })
    Object.assign(o, { price: e.price, stock: e.stock, leadTimeDays: e.leadTimeDays, availability: data.availability })
    delete editing.value[o.id]
    toast('good', 'Offer updated', o.supplierSku)
  } catch (err) { toast('bad', 'Could not update offer', errorText(err)) }
}
function matchCheapest(o: SellerOffer) {
  if (o.cheapestCompetitorPrice == null) return
  edit(o)
  editing.value[o.id].price = Math.max(0, Math.round((o.cheapestCompetitorPrice - 0.01) * 100) / 100)
}
const position = (o: SellerOffer) =>
  o.cheapestCompetitorPrice == null ? 'only' : (o.price ?? o.listPrice ?? 0) <= o.cheapestCompetitorPrice ? 'cheapest' : 'undercut'

// ----- discounts -----
const discounts = ref<any[]>([])
const customers = ref<CustomerAccount[]>([])
const newDiscount = ref({ customerAccountId: '' as number | '', discountPercent: 5, note: '' })
async function loadDiscounts() {
  if (!supplierId.value) return
  discounts.value = (await api.get('/seller/discounts', { params: { supplierId: isStaff ? supplierId.value : undefined } })).data
}
async function addDiscount() {
  try {
    await api.post('/seller/discounts', { ...newDiscount.value, customerAccountId: newDiscount.value.customerAccountId || null })
    toast('good', 'Discount created', `${newDiscount.value.discountPercent}% off your offers`)
    newDiscount.value = { customerAccountId: '', discountPercent: 5, note: '' }
    loadDiscounts()
  } catch (e) { toast('bad', 'Could not create discount', errorText(e)) }
}
async function endDiscount(id: number) {
  await api.delete(`/seller/discounts/${id}`)
  loadDiscounts()
}

// ----- list a product -----
const showList = ref(false)
const categories = ref<Category[]>([])
const defs = ref<AttributeDef[]>([])
const template = ref<TemplateItem[]>([])
const listing = ref({ sku: '', name: '', brand: '', manufacturerPartNumber: '', gtin: '', categoryId: '' as number | '', price: undefined as number | undefined, quantity: 0, leadTimeDays: 2 })
const listSpecs = ref<Record<string, string>>({})
const listErrors = ref<Record<string, string[]>>({})
watch(() => listing.value.categoryId, async id => {
  template.value = id ? (await api.get<TemplateItem[]>(`/categories/${id}/attributes`)).data : []
})
const leafCategories = computed(() => categories.value.filter(c => c.parentCategoryId))
async function submitListing() {
  listErrors.value = {}
  const l = listing.value
  try {
    const { data } = await api.post('/seller/products', {
      product: {
        sku: l.sku, name: l.name, description: null, price: l.price, currency: 'GBP', quantity: l.quantity, supplierId: isStaff ? supplierId.value || null : null,
        categoryIds: l.categoryId ? [l.categoryId] : [], brand: l.brand || null, manufacturerPartNumber: l.manufacturerPartNumber || null,
        gtin: l.gtin || null, attributes: Object.fromEntries(Object.entries(listSpecs.value).filter(([, v]) => v))
      },
      leadTimeDays: l.leadTimeDays
    })
    toast('good', data.consolidated ? 'Offer added to an existing product' : 'Product listed',
      data.consolidated ? 'Same GTIN already in the catalogue - your offer now competes on it.' : data.status === 'PendingApproval' ? 'Waiting for catalogue approval.' : 'It is live in the catalogue.')
    showList.value = false
    listing.value = { sku: '', name: '', brand: '', manufacturerPartNumber: '', gtin: '', categoryId: '', price: undefined, quantity: 0, leadTimeDays: 2 }
    listSpecs.value = {}
    loadOffers()
  } catch (e: any) {
    listErrors.value = e.response?.data?.errors ?? { form: [e.response?.data?.error ?? errorText(e)] }
  }
}

onMounted(async () => {
  if (isStaff) {
    suppliers.value = (await api.get<Supplier[]>('/suppliers')).data
    if (!supplierId.value) supplierId.value = suppliers.value.find(s => s.code === 'OWN')?.id ?? suppliers.value[0]?.id ?? ''
  }
  loadOffers()
  loadDiscounts()
  categories.value = (await api.get<Category[]>('/categories')).data
  defs.value = (await api.get<AttributeDef[]>('/attributes')).data
  customers.value = (await api.get<CustomerAccount[]>('/customers')).data
})
const def = (id: number) => defs.value.find(d => d.id === id)
</script>

<template>
  <div class="page-header">
    <div class="page-title-group">
      <h1 class="page-title">Seller portal</h1>
      <span class="page-subtitle">Your offers on the marketplace: price, stock and lead time, how you compare with other sellers, and the discounts you give buyers.</span>
    </div>
    <div class="page-actions">
      <select v-if="isStaff" v-model="supplierId" aria-label="Act as supplier" style="min-width: 220px">
        <option v-for="s in suppliers" :key="s.id" :value="s.id">{{ s.code }} · {{ s.name }}</option>
      </select>
      <router-link to="/orders?tab=sales" class="btn btn-secondary"><Icon name="inbox" :size="15" /> Sales orders</router-link>
      <button class="btn-primary" @click="showList = true"><Icon name="plus" :size="15" /> List a product</button>
    </div>
  </div>

  <div v-if="!supplierId" class="alert warn"><Icon name="alert" :size="16" />Your company is not set up to sell. Ask the platform to enable selling.</div>

  <template v-else>
    <div class="segmented" style="margin-bottom: 1rem">
      <button :class="{ active: tab === 'offers' }" @click="tab = 'offers'">Offers <span class="count">{{ fmt.compact(total) }}</span></button>
      <button :class="{ active: tab === 'discounts' }" @click="tab = 'discounts'">Buyer discounts <span class="count">{{ discounts.filter(d => d.isActive).length }}</span></button>
    </div>

    <div v-if="tab === 'offers'" class="card">
      <div class="toolbar">
        <div class="input-icon" style="width: 260px"><span class="icon"><Icon name="search" :size="15" /></span><input v-model="search" placeholder="Your SKU or product name…" /></div>
        <label class="row" style="margin: 0; font-weight: 500; cursor: pointer"><input v-model="undercut" type="checkbox" /> Only where a competitor is cheaper</label>
      </div>
      <div class="table-wrap">
        <table>
          <thead><tr><th>Product</th><th>Your SKU</th><th class="num">Your price</th><th class="num">Cheapest rival</th><th class="num">Stock</th><th class="num">Lead</th><th>Status</th><th></th></tr></thead>
          <tbody>
            <tr v-for="o in offers" :key="o.id">
              <td style="max-width: 280px"><div class="cell-title"><span class="cell-strong" style="white-space: nowrap; overflow: hidden; text-overflow: ellipsis">{{ o.productName }}</span>
                <small>{{ o.brand }} · <code>{{ o.sku }}</code><template v-if="o.isContentOwner"> · content owner</template></small></div></td>
              <td><code>{{ o.supplierSku }}</code></td>
              <td class="num">
                <input v-if="editing[o.id]" v-model.number="editing[o.id].price" type="number" step="0.01" min="0" style="width: 100px" aria-label="Price" />
                <div v-else class="cell-title" style="align-items: flex-end"><b>{{ fmt.money(o.price ?? o.listPrice, o.currency) }}</b>
                  <small v-if="o.cost != null">cost {{ fmt.money(o.cost, o.currency) }}</small></div>
              </td>
              <td class="num">
                <div class="cell-title" style="align-items: flex-end">
                  <span>{{ o.cheapestCompetitorPrice != null ? fmt.money(o.cheapestCompetitorPrice, o.currency) : '—' }}</span>
                  <small>
                    <span v-if="position(o) === 'cheapest'" style="color: var(--good)">You're cheapest</span>
                    <span v-else-if="position(o) === 'undercut'" style="color: var(--bad)">Undercut · {{ o.competitors }} rival(s)</span>
                    <span v-else>No competitors</span>
                  </small>
                </div>
              </td>
              <td class="num"><input v-if="editing[o.id]" v-model.number="editing[o.id].stock" type="number" min="0" style="width: 80px" aria-label="Stock" /><span v-else>{{ fmt.n(o.stock) }}</span></td>
              <td class="num"><input v-if="editing[o.id]" v-model.number="editing[o.id].leadTimeDays" type="number" min="0" style="width: 64px" aria-label="Lead time" /><span v-else>{{ o.leadTimeDays != null ? o.leadTimeDays + 'd' : '—' }}</span></td>
              <td><AvailabilityBadge :value="o.availability" /></td>
              <td class="num">
                <div class="row" style="justify-content: flex-end; gap: .25rem">
                  <template v-if="editing[o.id]">
                    <button class="btn-primary btn-sm" @click="save(o)"><Icon name="check" :size="13" /></button>
                    <button class="btn-ghost btn-sm" @click="delete editing[o.id]"><Icon name="x" :size="13" /></button>
                  </template>
                  <template v-else>
                    <button v-if="position(o) === 'undercut'" class="btn-secondary btn-sm" title="Price 1p below the cheapest rival" @click="matchCheapest(o)">Beat</button>
                    <button class="btn-ghost btn-sm" aria-label="Edit" @click="edit(o)"><Icon name="edit" :size="13" /></button>
                  </template>
                </div>
              </td>
            </tr>
            <tr v-if="!loading && !offers.length"><td colspan="8"><EmptyState icon="truck" title="No offers" text="List a product or stream your EDI catalogue to start selling." /></td></tr>
          </tbody>
        </table>
      </div>
      <Pager v-model:page="page" v-model:page-size="pageSize" :total="total" noun="offers" />
    </div>

    <div v-else class="grid grid-main-side">
      <div class="card">
        <div class="table-wrap">
          <table>
            <thead><tr><th>Applies to</th><th class="num">Discount</th><th>Note</th><th>Since</th><th>Status</th><th></th></tr></thead>
            <tbody>
              <tr v-for="d in discounts" :key="d.id">
                <td class="cell-strong">{{ d.customerName ?? 'All buyers' }}</td>
                <td class="num cell-strong" style="color: var(--good)">−{{ d.discountPercent }}%</td>
                <td class="cell-muted">{{ d.note ?? '—' }}</td>
                <td class="cell-muted">{{ fmt.date(d.validFromUtc) }}</td>
                <td><span class="badge" :class="d.isActive ? 'good' : ''"><span class="dot" />{{ d.isActive ? 'Active' : 'Ended' }}</span></td>
                <td class="num"><button v-if="d.isActive" class="btn-ghost btn-sm" @click="endDiscount(d.id)">End</button></td>
              </tr>
              <tr v-if="!discounts.length"><td colspan="6"><EmptyState icon="tag" title="No discounts" text="Discounts stack on top of the buyer's trade price list." /></td></tr>
            </tbody>
          </table>
        </div>
      </div>
      <form class="card card-pad stack" style="gap: .8rem; align-self: start" @submit.prevent="addDiscount">
        <h3 class="card-title"><Icon name="plus" :size="15" /> New discount</h3>
        <div class="field"><label for="dc">Buyer</label>
          <select id="dc" v-model="newDiscount.customerAccountId"><option value="">All buyers</option><option v-for="c in customers" :key="c.id" :value="c.id">{{ c.name }}</option></select></div>
        <div class="field"><label for="dp">Discount %</label><input id="dp" v-model.number="newDiscount.discountPercent" type="number" min="0.5" max="90" step="0.5" required /></div>
        <div class="field"><label for="dn">Note</label><input id="dn" v-model="newDiscount.note" maxlength="200" placeholder="e.g. Volume agreement 2026" /></div>
        <button type="submit" class="btn-primary">Create discount</button>
        <span class="hint">Buyers see it immediately in "Compare & buy" and their basket.</span>
      </form>
    </div>
  </template>

  <Overlay v-if="showList" title="List a product" subtitle="If the GTIN already exists, your offer is added to that product; otherwise a new product is created." @close="showList = false">
    <form id="lp" class="stack" style="gap: .8rem" @submit.prevent="submitListing">
      <div class="form-grid" style="grid-template-columns: 1fr 2fr">
        <div class="field"><label for="ls">Your SKU</label><input id="ls" v-model="listing.sku" required style="text-transform: uppercase" /></div>
        <div class="field"><label for="ln">Product name</label><input id="ln" v-model="listing.name" required /></div>
      </div>
      <div class="form-grid">
        <div class="field"><label for="lb">Brand</label><input id="lb" v-model="listing.brand" /></div>
        <div class="field"><label for="lm">MPN</label><input id="lm" v-model="listing.manufacturerPartNumber" /></div>
        <div class="field" :class="{ invalid: listErrors.gtin }"><label for="lg">GTIN</label><input id="lg" v-model="listing.gtin" inputmode="numeric" />
          <span v-if="listErrors.gtin" class="field-error">{{ listErrors.gtin[0] }}</span></div>
      </div>
      <div class="form-grid">
        <div class="field"><label for="lc">Category</label>
          <select id="lc" v-model="listing.categoryId" required><option value="">Select…</option><option v-for="c in leafCategories" :key="c.id" :value="c.id">{{ c.name }}</option></select></div>
        <div class="field" :class="{ invalid: listErrors.price }"><label for="lp2">Your price (GBP)</label><input id="lp2" v-model.number="listing.price" type="number" step="0.01" min="0" required /></div>
        <div class="field"><label for="lq">Stock</label><input id="lq" v-model.number="listing.quantity" type="number" min="0" /></div>
        <div class="field"><label for="ll">Lead time (days)</label><input id="ll" v-model.number="listing.leadTimeDays" type="number" min="0" /></div>
      </div>
      <div v-if="template.length" class="form-grid">
        <div v-for="t in template" :key="t.code" class="field" :class="{ invalid: listErrors[`attr.${t.code}`] }">
          <label :for="`ls-${t.code}`">{{ t.name }}<span v-if="t.unit" class="muted"> ({{ t.unit }})</span><span v-if="t.required" style="color: var(--bad)"> *</span></label>
          <select v-if="def(t.attributeId)?.dataType === 'Enum'" :id="`ls-${t.code}`" v-model="listSpecs[t.code]"><option value="">—</option><option v-for="v in def(t.attributeId)!.allowedValues" :key="v">{{ v }}</option></select>
          <input v-else :id="`ls-${t.code}`" v-model="listSpecs[t.code]" />
          <span v-if="listErrors[`attr.${t.code}`]" class="field-error">{{ listErrors[`attr.${t.code}`][0] }}</span>
        </div>
      </div>
      <div v-if="listErrors.form" class="alert bad"><Icon name="alertCircle" :size="16" />{{ listErrors.form.join(' ') }}</div>
    </form>
    <template #footer>
      <button class="btn-secondary" @click="showList = false">Cancel</button>
      <button class="btn-primary" type="submit" form="lp"><Icon name="check" :size="15" /> List product</button>
    </template>
  </Overlay>
</template>
