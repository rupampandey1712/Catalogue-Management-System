<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import Icon from '../components/Icon.vue'
import Pager from '../components/Pager.vue'
import StatusBadge from '../components/StatusBadge.vue'
import AvailabilityBadge from '../components/AvailabilityBadge.vue'
import EmptyState from '../components/EmptyState.vue'
import Overlay from '../components/Overlay.vue'
import {
  api, errorText, type Asset, type Category, type CustomerAccount, type Facet, type OfferQuote, type Paged, type Product, type ProductDetail, type Supplier
} from '../api'
import { basket } from '../basket'
import { session } from '../session'
import { fmt, prefs, toast } from '../ui'

const route = useRoute()
const router = useRouter()

const products = ref<Product[]>([])
const total = ref(0)
const loading = ref(true)
const elapsed = ref(0)
const error = ref('')
const categories = ref<Category[]>([])
const suppliers = ref<Supplier[]>([])
const customers = ref<CustomerAccount[]>([])
const facets = ref<Facet[]>([])
const brandFacet = ref<{ value: string; count: number }[]>([])

const search = ref((route.query.q as string) ?? '')
const deep = ref(false)
const status = ref('')
const categoryId = ref<number | ''>(route.query.category ? Number(route.query.category) : '')
const supplierId = ref<number | ''>(route.query.supplier ? Number(route.query.supplier) : '')
const brand = ref('')
const availability = ref('')
const incomplete = ref(route.query.incomplete === 'true')
const noImage = ref(route.query.noImage === 'true')
const sort = ref('id')
const page = ref(1)
const pageSize = ref(Number(prefs.get('catalogue.pageSize')) || 25)
// Spec filters: code -> { values (enum) | min/max (number) }
const specSel = ref<Record<string, { values: string[]; min?: number | ''; max?: number | '' }>>({})
// Whose prices are shown: '' = list price, else a customer account id. Buyers default to their account.
const priceAs = ref<number | ''>(session.customer?.id ?? (Number(prefs.get('catalogue.priceAs')) || ''))

const selected = ref<ProductDetail | null>(null)
const detailLoading = ref(false)
const tab = ref<'overview' | 'specs' | 'media' | 'suppliers' | 'buy' | 'pricing'>('overview')
const mainImage = ref<Asset | null>(null)
const buyQty = ref(1)
const offers = ref<OfferQuote[]>([])
const offersLoading = ref(false)
const quoteQty = ref(1)
let debounce: ReturnType<typeof setTimeout>
let seq = 0

const specParams = computed(() => Object.entries(specSel.value).flatMap(([code, s]) => {
  if (s.values.length) return [`${code}:in:${s.values.join('|')}`]
  const lo = s.min === '' || s.min == null ? '' : s.min
  const hi = s.max === '' || s.max == null ? '' : s.max
  return lo === '' && hi === '' ? [] : [`${code}:between:${lo}..${hi}`]
}))

async function load() {
  const mine = ++seq
  loading.value = true
  error.value = ''
  const started = performance.now()
  try {
    const params = new URLSearchParams()
    const add = (k: string, v: unknown) => { if (v !== '' && v != null && v !== false) params.append(k, String(v)) }
    add('search', search.value); add('deep', deep.value || ''); add('status', status.value)
    add('categoryId', categoryId.value); add('supplierId', supplierId.value); add('brand', brand.value)
    add('availability', availability.value); add('incomplete', incomplete.value || ''); if (noImage.value) params.append('hasImage', 'false'); add('customerId', priceAs.value)
    add('sort', sort.value); add('page', page.value); add('pageSize', pageSize.value)
    specParams.value.forEach(s => params.append('spec', s))
    const { data } = await api.get<Paged<Product>>(`/products?${params}`)
    if (mine !== seq) return
    products.value = data.items
    total.value = data.total
    elapsed.value = performance.now() - started
  } catch (e) {
    if (mine === seq) error.value = errorText(e, 'Failed to load products.')
  } finally {
    if (mine === seq) loading.value = false
  }
}

async function loadFacets() {
  const { data } = await api.get('/products/facets', { params: { categoryId: categoryId.value || undefined } })
  // Pre-create filter state for each facet so the template only reads it (no writes during render).
  const sel = { ...specSel.value }
  for (const f of data.facets as Facet[]) sel[f.code] ??= { values: [], min: '', max: '' }
  specSel.value = sel
  facets.value = data.facets
  brandFacet.value = data.brands
}

const reload = () => { clearTimeout(debounce); debounce = setTimeout(() => { page.value = 1; load() }, 300) }
watch([search, deep, status, supplierId, brand, availability, incomplete, noImage, priceAs], reload)
watch(specParams, reload, { deep: true })
watch(categoryId, () => { specSel.value = {}; loadFacets(); reload() })
// The facet panel resets with "Clear"; re-seed entries so inputs stay bound.
watch(specSel, v => { if (Object.keys(v).length === 0 && facets.value.length) loadFacets() })
watch([page, sort], load)
watch(pageSize, v => { prefs.set('catalogue.pageSize', String(v)); page.value = 1; load() })
watch(priceAs, v => prefs.set('catalogue.priceAs', String(v)))
watch(() => route.query.q, q => { if ((q ?? '') !== search.value) search.value = (q as string) ?? '' })

onMounted(async () => {
  load()
  loadFacets()
  categories.value = (await api.get<Category[]>('/categories')).data
  customers.value = (await api.get<CustomerAccount[]>('/customers')).data
  if (session.can('supplierAdmin')) suppliers.value = (await api.get<Supplier[]>('/suppliers')).data
})

// Category select shows the tree: parents with their children indented.
const categoryOptions = computed(() => {
  const roots = categories.value.filter(c => !c.parentCategoryId)
  return roots.flatMap(r => [{ ...r, depth: 0 }, ...categories.value.filter(c => c.parentCategoryId === r.id).map(c => ({ ...c, depth: 1 }))])
})

const statusOptions = ['', 'Active', 'Inactive', 'Draft', 'PendingApproval']
const activeFilters = computed(() =>
  [status.value, categoryId.value, supplierId.value, search.value, brand.value, availability.value, incomplete.value, noImage.value].filter(Boolean).length
  + specParams.value.length)

function clearFilters() {
  search.value = ''; status.value = ''; categoryId.value = ''; supplierId.value = ''; deep.value = false
  brand.value = ''; availability.value = ''; incomplete.value = false; noImage.value = false; specSel.value = {}
  router.replace({ query: {} })
}

const emptySpec = { values: [] as string[], min: '' as number | '', max: '' as number | '' }
function spec(code: string) {
  return specSel.value[code] ?? emptySpec
}
function toggleSpecValue(code: string, value: string) {
  const s = specSel.value[code] ?? (specSel.value[code] = { values: [], min: '', max: '' })
  s.values = s.values.includes(value) ? s.values.filter(v => v !== value) : [...s.values, value]
}

function toggleSort(field: string) {
  sort.value = sort.value === field ? `${field}_desc` : sort.value === `${field}_desc` ? 'id' : field
}
const sortIcon = (field: string) => (sort.value === field ? '↑' : sort.value === `${field}_desc` ? '↓' : '')

async function open(p: Product) {
  detailLoading.value = true
  tab.value = 'overview'
  quoteQty.value = 1
  buyQty.value = 1
  offers.value = []
  mainImage.value = null
  selected.value = { ...p, categoryIds: [], createdAtUtc: '', updatedAtUtc: p.updatedAtUtc ?? '', priceHistory: [], specs: [], missingRequired: [], offers: [], pendingChanges: [], assets: [] } as ProductDetail
  try {
    await loadDetail(p.id)
  } finally {
    detailLoading.value = false
  }
}
async function loadDetail(id: number) {
  selected.value = (await api.get<ProductDetail>(`/products/${id}`, {
    params: { customerId: priceAs.value || undefined, qty: quoteQty.value }
  })).data
  mainImage.value = selected.value.assets.find(a => a.isPrimary) ?? selected.value.assets.find(a => a.kind === 'Image') ?? null
}
const images = computed(() => selected.value?.assets?.filter(a => a.kind === 'Image') ?? [])
const documents = computed(() => selected.value?.assets?.filter(a => a.kind !== 'Image') ?? [])

// "Compare & buy": every seller's offer priced for the signed-in company (cheapest after discounts).
async function loadOffers() {
  if (!selected.value) return
  offersLoading.value = true
  try {
    offers.value = (await api.get(`/marketplace/products/${selected.value.id}/offers`, { params: { qty: buyQty.value } })).data.offers
  } catch (e) { toast('bad', 'Could not load offers', errorText(e)) }
  finally { offersLoading.value = false }
}
watch(tab, t => { if (t === 'buy') loadOffers() })
watch(buyQty, q => { if (tab.value === 'buy' && q > 0) loadOffers() })
function addToBasket(o?: OfferQuote) {
  const p = selected.value!
  basket.add({ productId: p.id, sku: p.sku, name: p.name, imageUrl: images.value[0]?.thumbnailUrl, quantity: buyQty.value, offerId: o?.offerId })
  toast('good', 'Added to basket', `${buyQty.value} × ${p.name}${o ? ` from ${o.supplierCode}` : ' (cheapest seller)'}`)
}
const docIcon = (kind: string) => kind === 'Manual' ? 'file' : kind === 'EnergyLabel' ? 'zap' : kind === 'Certificate' ? 'shield' : 'file'
watch(quoteQty, q => { if (selected.value && priceAs.value && q > 0) loadDetail(selected.value.id) })

async function toggleStatus(p: Product) {
  const action = p.status === 'Active' ? 'deactivate' : 'activate'
  try {
    const { data } = await api.post<{ status: string }>(`/products/${p.id}/${action}`)
    const row = products.value.find(x => x.id === p.id)
    if (row) row.status = data.status
    if (selected.value?.id === p.id) selected.value.status = data.status
    toast('good', `Product ${action}d`, p.sku)
  } catch (e) {
    toast('bad', `Could not ${action} product`, errorText(e))
  }
}

function copy(text?: string) {
  if (!text) return
  navigator.clipboard?.writeText(text)
  toast('info', 'Copied to clipboard', text, 2000)
}

const customerName = computed(() => customers.value.find(c => c.id === priceAs.value)?.name)
const formatGtin = (g?: string) => g ? g.replace(/^0+(?=\d{13}$)/, '') : '—'

function exportCsv() {
  const header = 'sku,brand,mpn,gtin,name,status,availability,list_price,net_price,currency,quantity,categories,specs'
  const esc = (v: unknown) => `"${String(v ?? '').replace(/"/g, '""')}"`
  const lines = products.value.map(p => [p.sku, p.brand, p.manufacturerPartNumber, formatGtin(p.gtin), p.name, p.status, p.availability,
    p.price, p.netPrice, p.currency, p.availableQuantity, p.categories.join('|'),
    (p.specs ?? []).map(s => `${s.code}=${s.display}`).join('; ')].map(esc).join(','))
  const a = document.createElement('a')
  a.href = URL.createObjectURL(new Blob([[header, ...lines].join('\n')], { type: 'text/csv' }))
  a.download = `catalogue-page-${page.value}.csv`
  a.click()
  URL.revokeObjectURL(a.href)
}
</script>

<template>
  <div class="page-header">
    <div class="page-title-group">
      <h1 class="page-title">Catalogue <span class="badge primary">{{ fmt.compact(total) }} products</span></h1>
      <span class="page-subtitle">HVAC & plumbing products from every supplier — filter by specification, brand and availability.</span>
    </div>
    <div class="page-actions">
      <div class="field" style="min-width: 240px">
        <select v-model="priceAs" aria-label="Show prices for" :disabled="!!session.customer && !session.can('catalogueAdmin')">
          <option value="">List prices</option>
          <option v-for="c in customers" :key="c.id" :value="c.id">Prices for {{ c.accountNumber }} · {{ c.name }}</option>
        </select>
      </div>
      <button class="btn-secondary" :disabled="!products.length" @click="exportCsv"><Icon name="download" :size="15" /> Export</button>
      <router-link v-if="session.can('catalogueAdmin')" to="/products/new" class="btn btn-primary"><Icon name="plus" :size="15" /> New product</router-link>
    </div>
  </div>

  <div class="grid" :style="{ gridTemplateColumns: categoryId ? 'minmax(220px, 260px) minmax(0, 1fr)' : 'minmax(0, 1fr)', alignItems: 'start' }">
    <!-- Facet panel (specifications of the chosen category) -->
    <aside v-if="categoryId" class="card" style="position: sticky; top: 76px; max-height: calc(100vh - 96px); overflow: auto">
      <div class="card-header" style="padding: .8rem 1rem">
        <h3 class="card-title" style="font-size: .9rem"><Icon name="filter" :size="15" /> Specifications</h3>
        <button v-if="specParams.length" class="btn-ghost btn-sm" @click="specSel = {}">Clear</button>
      </div>
      <div class="stack" style="padding: .9rem 1rem; gap: 1.1rem">
        <div v-for="f in facets" :key="f.code">
          <div class="row" style="font-size: .8rem; font-weight: 650; margin-bottom: .4rem">
            {{ f.name }}<span v-if="f.unit" class="muted" style="font-weight: 500">({{ f.unit }})</span>
          </div>
          <div v-if="f.dataType === 'Number'" class="row" style="gap: .35rem">
            <input v-model.number="spec(f.code).min" type="number" :placeholder="f.min != null ? String(f.min) : 'min'" style="width: 50%" aria-label="Minimum" />
            <span class="muted">–</span>
            <input v-model.number="spec(f.code).max" type="number" :placeholder="f.max != null ? String(f.max) : 'max'" style="width: 50%" aria-label="Maximum" />
          </div>
          <div v-else class="stack" style="gap: .25rem">
            <label v-for="v in f.values" :key="v.value" class="row" style="margin: 0; font-weight: 500; font-size: .82rem; cursor: pointer">
              <input type="checkbox" :checked="spec(f.code).values.includes(v.value)" @change="toggleSpecValue(f.code, v.value)" />
              <span>{{ v.value }}</span><span class="spacer" /><span class="muted tabular" style="font-size: .74rem">{{ fmt.compact(v.count) }}</span>
            </label>
            <span v-if="!f.values.length" class="muted" style="font-size: .78rem">No values yet</span>
          </div>
        </div>
        <EmptyState v-if="!facets.length" icon="layers" title="No specifications" text="This category has no attribute template yet." />
      </div>
    </aside>

    <div class="card">
      <div class="toolbar">
        <div class="input-icon" style="flex: 1; min-width: 220px; max-width: 380px">
          <span class="icon"><Icon name="search" :size="15" /></span>
          <input v-model="search" :placeholder="deep ? 'Name contains… (full scan)' : 'SKU, MPN, GTIN, brand or name…'" aria-label="Search products" />
        </div>
        <label class="row" style="margin: 0; font-weight: 500; cursor: pointer" title="Substring match on product name. Slower on millions of rows.">
          <input v-model="deep" type="checkbox" /> Deep
        </label>
        <select v-model="categoryId" aria-label="Category">
          <option value="">All categories</option>
          <option v-for="c in categoryOptions" :key="c.id" :value="c.id">{{ c.depth ? '　· ' : '' }}{{ c.name }}</option>
        </select>
        <select v-model="brand" aria-label="Brand">
          <option value="">All brands</option>
          <option v-for="b in brandFacet" :key="b.value" :value="b.value">{{ b.value }} ({{ fmt.compact(b.count) }})</option>
        </select>
        <select v-model="availability" aria-label="Availability">
          <option value="">Any availability</option>
          <option value="InStock">In stock</option><option value="LowStock">Low stock</option>
          <option value="OnOrder">On order</option><option value="Discontinued">Discontinued</option>
        </select>
        <select v-if="suppliers.length" v-model="supplierId" aria-label="Supplier">
          <option value="">Any supplier</option>
          <option v-for="s in suppliers" :key="s.id" :value="s.id">{{ s.code }} — {{ s.name }}</option>
        </select>
        <div class="segmented" role="group" aria-label="Status">
          <button v-for="s in statusOptions" :key="s" :class="{ active: status === s }" @click="status = s">{{ s === 'PendingApproval' ? 'Pending' : s || 'All' }}</button>
        </div>
        <label v-if="session.can('catalogueAdmin')" class="row" style="margin: 0; font-weight: 500; cursor: pointer" title="Products without a primary image">
          <input v-model="noImage" type="checkbox" /> No image
        </label>
        <label v-if="session.can('catalogueAdmin')" class="row" style="margin: 0; font-weight: 500; cursor: pointer" title="Products missing a required specification for their category">
          <input v-model="incomplete" type="checkbox" /> Missing specs
        </label>
        <button v-if="activeFilters" class="btn-ghost btn-sm" @click="clearFilters"><Icon name="x" :size="14" /> Clear ({{ activeFilters }})</button>
        <span class="spacer" />
        <span class="muted" style="font-size: .76rem">
          <Icon v-if="loading" name="refresh" :size="13" class="spin" style="vertical-align: -2px" />
          <template v-else>{{ fmt.duration(elapsed) }}</template>
        </span>
      </div>

      <div v-if="priceAs" class="alert info" style="margin: .75rem 1.35rem 0">
        <Icon name="tag" :size="15" /> Showing net prices for <b>{{ customerName }}</b> (customer list → tier → trade fallback).
      </div>
      <div v-if="error" class="alert bad" style="margin: 1rem 1.35rem"><Icon name="alertCircle" :size="16" />{{ error }}</div>

      <div class="table-wrap" :style="{ opacity: loading && products.length ? 0.6 : 1, transition: 'opacity 120ms' }">
        <table>
          <thead>
            <tr>
              <th class="sortable" @click="toggleSort('name')">Product <span class="sort-ind">{{ sortIcon('name') }}</span></th>
              <th class="sortable" @click="toggleSort('sku')">SKU <span class="sort-ind">{{ sortIcon('sku') }}</span></th>
              <th>Key specs</th>
              <th class="num">{{ priceAs ? 'Net price' : 'List price' }}</th>
              <th>Availability</th>
              <th>Status</th>
              <th v-if="session.can('catalogueAdmin')"></th>
            </tr>
          </thead>
          <tbody>
            <template v-if="loading && !products.length">
              <tr v-for="i in 8" :key="i"><td v-for="j in 6" :key="j"><span class="skeleton" :style="{ width: 40 + ((i * j) % 5) * 12 + '%' }" /></td></tr>
            </template>
            <tr v-for="p in products" :key="p.id" class="clickable" :class="{ selected: selected?.id === p.id }" @click="open(p)">
              <td style="max-width: 380px">
                <div class="row" style="gap: .7rem; flex-wrap: nowrap">
                <img v-if="p.imageUrl" :src="p.imageUrl" alt="" loading="lazy" width="44" height="44" style="border-radius: 9px; object-fit: cover; flex-shrink: 0; background: var(--surface-3)" />
                <span v-else class="list-icon" style="width: 44px; height: 44px" title="No image"><Icon name="box" :size="17" /></span>
                <div class="cell-title" style="min-width: 0">
                  <span class="cell-strong" style="white-space: nowrap; overflow: hidden; text-overflow: ellipsis">{{ p.name }}</span>
                  <small>
                    <b v-if="p.brand" style="color: var(--text-secondary)">{{ p.brand }}</b>
                    <template v-if="p.manufacturerPartNumber"> · {{ p.manufacturerPartNumber }}</template>
                    <template v-if="p.categories.length"> · {{ p.categories.join(', ') }}</template>
                  </small>
                </div>
                </div>
              </td>
              <td><code>{{ p.sku }}</code></td>
              <td>
                <div class="chips" style="flex-wrap: nowrap; overflow: hidden; max-width: 300px">
                  <span v-for="s in p.specs" :key="s.code" class="tag" :title="s.name">{{ s.display }}</span>
                  <span v-if="!p.specs?.length" class="muted" style="font-size: .78rem">—</span>
                </div>
              </td>
              <td class="num">
                <div class="cell-title" style="align-items: flex-end">
                  <span class="cell-strong">{{ fmt.money(priceAs ? p.netPrice ?? p.price : p.price, p.currency) }}</span>
                  <small v-if="priceAs && p.savingPercent" style="color: var(--good)">−{{ p.savingPercent }}% · {{ fmt.money(p.price, p.currency) }} list</small>
                </div>
              </td>
              <td>
                <div class="cell-title">
                  <AvailabilityBadge :value="p.availability" />
                  <small>{{ fmt.n(p.availableQuantity) }} in stock · {{ p.offerCount }} supplier{{ p.offerCount === 1 ? '' : 's' }}</small>
                </div>
              </td>
              <td><StatusBadge :status="p.status" :label="p.status === 'PendingApproval' ? 'Pending approval' : undefined" /></td>
              <td v-if="session.can('catalogueAdmin')" class="num" @click.stop>
                <div class="row" style="justify-content: flex-end; gap: .25rem">
                  <router-link :to="`/products/${p.id}`" class="btn btn-ghost btn-sm" title="Edit"><Icon name="edit" :size="14" /></router-link>
                  <button class="btn-ghost btn-sm" :title="p.status === 'Active' ? 'Deactivate' : 'Activate'" @click="toggleStatus(p)">
                    <Icon name="power" :size="14" :style="{ color: p.status === 'Active' ? 'var(--good)' : 'var(--muted)' }" />
                  </button>
                </div>
              </td>
            </tr>
            <tr v-if="!loading && !products.length">
              <td colspan="7">
                <EmptyState icon="search" title="No products match" text="Try a different search, or loosen the specification filters.">
                  <button v-if="activeFilters" class="btn-secondary btn-sm" @click="clearFilters">Clear filters</button>
                </EmptyState>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
      <Pager v-model:page="page" v-model:page-size="pageSize" :total="total" noun="products" />
    </div>
  </div>

  <!-- Product drawer -->
  <Overlay v-if="selected" kind="drawer" :title="selected.name" @close="selected = null">
    <template #subtitle>
      <span class="row wrap" style="gap: .4rem">
        <code>{{ selected.sku }}</code>
        <button class="btn-ghost btn-sm" style="padding: .1rem .3rem" aria-label="Copy SKU" @click="copy(selected.sku)"><Icon name="copy" :size="13" /></button>
        <StatusBadge :status="selected.status" :label="selected.status === 'PendingApproval' ? 'Pending approval' : undefined" />
        <AvailabilityBadge :value="selected.availability" />
      </span>
    </template>

    <div class="segmented">
      <button :class="{ active: tab === 'overview' }" @click="tab = 'overview'">Overview</button>
      <button :class="{ active: tab === 'specs' }" @click="tab = 'specs'">Specs <span class="count">{{ selected.specs.filter(s => s.value).length }}/{{ selected.specs.length }}</span></button>
      <button :class="{ active: tab === 'suppliers' }" @click="tab = 'suppliers'">Suppliers <span class="count">{{ selected.offers.length }}</span></button>
      <button :class="{ active: tab === 'media' }" @click="tab = 'media'">Media <span class="count">{{ selected.assets?.length ?? 0 }}</span></button>
      <button v-if="session.can('buyer') && session.organization?.canBuy" :class="{ active: tab === 'buy' }" @click="tab = 'buy'"><Icon name="tag" :size="12" /> Compare & buy</button>
      <button :class="{ active: tab === 'pricing' }" @click="tab = 'pricing'">Pricing</button>
    </div>

    <div v-if="detailLoading" class="skeleton" style="height: 160px" />

    <template v-else-if="tab === 'overview'">
      <div v-if="mainImage" style="border: 1px solid var(--border); border-radius: var(--radius-lg); overflow: hidden; background: var(--surface-2)">
        <img :src="mainImage.url" :alt="selected.name" style="display: block; width: 100%; max-height: 300px; object-fit: contain" />
      </div>
      <div v-if="images.length > 1" class="row" style="gap: .4rem">
        <button v-for="img in images" :key="img.id" class="btn-ghost" style="padding: 2px; border-radius: 8px"
          :style="mainImage?.id === img.id ? 'outline: 2px solid var(--primary)' : ''" :aria-label="`Show ${img.title}`" @click="mainImage = img">
          <img :src="img.thumbnailUrl ?? img.url" alt="" width="54" height="54" style="border-radius: 6px; object-fit: cover; display: block" />
        </button>
      </div>
      <div v-if="documents.length" class="chips">
        <a v-for="d in documents" :key="d.id" :href="d.url" target="_blank" rel="noopener" class="tag" style="text-decoration: none"><Icon :name="docIcon(d.kind)" :size="12" />&nbsp;{{ d.kind }}</a>
      </div>
      <div v-if="selected.pendingChanges.length" class="alert warn">
        <Icon name="clock" :size="16" />
        <span><b>{{ selected.pendingChanges.length }} change(s) awaiting approval:</b> {{ selected.pendingChanges.map(c => c.summary).join('; ') }}
          <router-link v-if="session.can('importOps')" to="/approvals"> Review</router-link></span>
      </div>
      <div v-if="selected.missingRequired.length" class="alert bad">
        <Icon name="alert" :size="16" /><span><b>Missing required specs:</b> {{ selected.missingRequired.join(', ') }}</span>
      </div>
      <div class="grid grid-2" style="gap: .75rem">
        <div class="stat-card">
          <span class="stat-label">{{ selected.quote ? 'Your net price' : 'List price' }}</span>
          <span class="stat-value" style="font-size: 1.3rem">{{ fmt.money(selected.quote?.netUnitPrice ?? selected.price, selected.currency) }}</span>
          <span v-if="selected.quote?.savingPercent" class="stat-foot"><span class="up">−{{ selected.quote.savingPercent }}%</span> vs {{ fmt.money(selected.price, selected.currency) }} list</span>
        </div>
        <div class="stat-card">
          <span class="stat-label">Stock (all suppliers)</span>
          <span class="stat-value" style="font-size: 1.3rem">{{ fmt.n(selected.availableQuantity) }}</span>
          <span class="stat-foot">{{ selected.offers.length }} supplier offer{{ selected.offers.length === 1 ? '' : 's' }}</span>
        </div>
      </div>
      <dl class="kv">
        <dt>Brand</dt><dd>{{ selected.brand ?? '—' }}</dd>
        <dt>Manufacturer part no.</dt><dd><code v-if="selected.manufacturerPartNumber">{{ selected.manufacturerPartNumber }}</code><span v-else>—</span></dd>
        <dt>GTIN / EAN</dt><dd><code v-if="selected.gtin">{{ formatGtin(selected.gtin) }}</code><span v-else>—</span></dd>
        <dt>Categories</dt><dd><div class="chips"><span v-for="c in selected.categories" :key="c" class="tag">{{ c }}</span><span v-if="!selected.categories.length" class="muted">None</span></div></dd>
        <dt>Description</dt><dd>{{ selected.description || '—' }}</dd>
        <dt>Content owner</dt><dd>{{ selected.supplierCode ?? 'Catalogue team' }}</dd>
        <dt>Last updated</dt><dd>{{ fmt.date(selected.updatedAtUtc) }}</dd>
      </dl>
      <div class="list" style="border: 1px solid var(--border); border-radius: var(--radius)">
        <div v-for="s in selected.specs.filter(s => s.value).slice(0, 6)" :key="s.code" class="list-item" style="padding: .5rem .9rem">
          <span class="list-main muted">{{ s.name }}</span><b>{{ s.display }}</b>
        </div>
        <div v-if="!selected.specs.some(s => s.value)" class="list-item muted">No specifications recorded.</div>
      </div>
    </template>

    <template v-else-if="tab === 'specs'">
      <table>
        <thead><tr><th>Specification</th><th>Value</th></tr></thead>
        <tbody>
          <tr v-for="s in selected.specs" :key="s.code">
            <td>{{ s.name }} <span v-if="s.required" class="badge warn" style="font-size: .66rem; margin-left: .3rem">required</span></td>
            <td :class="s.value ? 'cell-strong' : 'muted'">
              <template v-if="s.value">{{ s.display }}</template>
              <span v-else :style="s.required ? 'color: var(--bad)' : ''">{{ s.required ? 'Missing' : '—' }}</span>
            </td>
          </tr>
          <tr v-if="!selected.specs.length"><td colspan="2" class="muted">No attribute template for this product's categories.</td></tr>
        </tbody>
      </table>
    </template>

    <template v-else-if="tab === 'media'">
      <div v-if="images.length" class="grid" style="grid-template-columns: repeat(auto-fill, minmax(120px, 1fr)); gap: .6rem">
        <a v-for="img in images" :key="img.id" :href="img.url" target="_blank" rel="noopener" class="card" style="overflow: hidden; box-shadow: none; text-decoration: none">
          <img :src="img.thumbnailUrl ?? img.url" :alt="img.title" loading="lazy" style="display: block; width: 100%; aspect-ratio: 1; object-fit: cover" />
          <div style="padding: .35rem .5rem; font-size: .72rem" class="muted">{{ img.isPrimary ? 'Main · ' : '' }}{{ img.width }}×{{ img.height }}</div>
        </a>
      </div>
      <EmptyState v-else icon="eye" title="No images yet" text="Import a supplier media pack or upload in the product editor." />
      <h4 style="font-size: .9rem">Documents</h4>
      <div class="list" style="border: 1px solid var(--border); border-radius: var(--radius)">
        <a v-for="d in documents" :key="d.id" :href="d.url" target="_blank" rel="noopener" class="list-item" style="text-decoration: none; color: inherit">
          <span class="list-icon"><Icon :name="docIcon(d.kind)" :size="15" /></span>
          <div class="list-main"><div class="list-title">{{ d.title }}</div><div class="list-sub">{{ d.kind }} · PDF · {{ fmt.bytes(d.sizeBytes) }} · {{ d.source === 'BulkZip' ? 'media pack' : d.source === 'SupplierUrl' ? 'from supplier link' : 'uploaded' }}</div></div>
          <Icon name="download" :size="15" />
        </a>
        <div v-if="!documents.length" class="list-item muted">No datasheets or manuals.</div>
      </div>
      <router-link v-if="session.can('catalogueAdmin')" :to="`/products/${selected.id}`" class="btn btn-secondary"><Icon name="upload" :size="15" /> Manage media</router-link>
    </template>

    <template v-else-if="tab === 'buy'">
      <div class="row">
        <label for="bq" style="margin: 0">Quantity</label>
        <input id="bq" v-model.number="buyQty" type="number" min="1" style="width: 90px" />
        <span class="spacer" />
        <span class="muted" style="font-size: .8rem">Priced for {{ session.customer?.name }}</span>
      </div>
      <div v-if="offersLoading" class="skeleton" style="height: 120px" />
      <template v-else>
        <div v-for="o in offers" :key="o.offerId" class="card card-pad stack" style="gap: .45rem; box-shadow: none"
          :style="o.isCheapest ? 'border-color: var(--good)' : !o.purchasable ? 'opacity: .6' : ''">
          <div class="row">
            <b>{{ o.supplierCode }}</b><span class="muted" style="font-size: .8rem">{{ o.supplierName }}</span>
            <span v-if="o.isCheapest" class="badge good"><span class="dot" />Cheapest for you</span>
            <span class="spacer" /><AvailabilityBadge :value="o.availability" :lead-time-days="o.leadTimeDays" />
          </div>
          <div class="row" style="align-items: baseline">
            <span style="font-size: 1.25rem; font-weight: 700">{{ fmt.money(o.netUnitPrice, o.currency) }}</span>
            <span v-if="o.totalDiscountPercent" class="muted" style="text-decoration: line-through">{{ fmt.money(o.basePrice, o.currency) }}</span>
            <span v-if="o.totalDiscountPercent" class="badge good">−{{ o.totalDiscountPercent }}%</span>
            <span class="spacer" /><span class="muted">{{ o.orderQuantity }} × = <b style="color: var(--text)">{{ fmt.money(o.lineTotal, o.currency) }}</b></span>
          </div>
          <div class="muted mono" style="font-size: .72rem">{{ o.explanation }}</div>
          <div v-if="o.orderQuantity !== o.requestedQuantity" class="hint">Rounded to {{ o.orderQuantity }} (MOQ {{ o.minOrderQuantity }}, pack of {{ o.packSize }})</div>
          <div class="row">
            <span v-if="!o.purchasable" class="badge">{{ o.notPurchasableReason }}</span>
            <span class="spacer" />
            <button class="btn-primary btn-sm" :disabled="!o.purchasable" @click="addToBasket(o)"><Icon name="plus" :size="13" /> Add from {{ o.supplierCode }}</button>
          </div>
        </div>
        <EmptyState v-if="!offers.length" icon="truck" title="No sellers offer this product" />
        <div v-if="offers.some(o => o.purchasable)" class="row">
          <button class="btn-secondary" @click="addToBasket()"><Icon name="sparkles" :size="15" /> Add — always pick the cheapest</button>
          <router-link to="/basket" class="btn btn-ghost">Go to basket ({{ basket.lines.value }})</router-link>
        </div>
      </template>
    </template>

    <template v-else-if="tab === 'suppliers'">
      <p class="muted" style="font-size: .82rem">Offers matched to this product by supplier SKU or GTIN. The preferred offer's supplier owns the product content.</p>
      <div v-for="o in selected.offers" :key="o.id" class="card card-pad stack" style="gap: .55rem; box-shadow: none">
        <div class="row">
          <b>{{ o.supplierCode }}</b><span class="muted" style="font-size: .8rem">{{ o.supplierName }}</span>
          <span v-if="o.isPreferred" class="badge primary">Preferred</span>
          <span class="spacer" /><AvailabilityBadge :value="o.availability" :lead-time-days="o.leadTimeDays" />
        </div>
        <dl class="kv" style="grid-template-columns: 130px 1fr; font-size: .82rem">
          <dt>Supplier SKU</dt><dd><code>{{ o.supplierSku }}</code></dd>
          <dt v-if="session.can('supplierAdmin')">Cost</dt><dd v-if="session.can('supplierAdmin')">{{ fmt.money(o.cost, o.currency) }}</dd>
          <dt>Stock</dt><dd>{{ fmt.n(o.stockQuantity) }}</dd>
          <dt>Lead time</dt><dd>{{ o.leadTimeDays != null ? `${o.leadTimeDays} day(s)` : '—' }}</dd>
          <dt>Order rules</dt><dd>MOQ {{ o.minOrderQuantity }} · pack of {{ o.packSize }}</dd>
          <dt>Updated</dt><dd>{{ fmt.ago(o.updatedAtUtc) }}</dd>
        </dl>
      </div>
      <EmptyState v-if="!selected.offers.length" icon="truck" title="No supplier offers" text="Created manually; no supplier feeds this product yet." />
    </template>

    <template v-else>
      <div v-if="!priceAs" class="alert info"><Icon name="info" :size="16" />Choose “Prices for…” at the top of the catalogue to see a customer's net price and quantity breaks.</div>
      <template v-else-if="selected.quote">
        <div class="row">
          <label for="qq" style="margin: 0">Quantity</label>
          <input id="qq" v-model.number="quoteQty" type="number" min="1" style="width: 90px" />
          <span class="spacer" />
          <span class="muted" style="font-size: .8rem">{{ selected.quote.source }}<template v-if="selected.quote.priceListCode"> · <code>{{ selected.quote.priceListCode }}</code></template></span>
        </div>
        <div class="grid grid-2" style="gap: .75rem">
          <div class="stat-card"><span class="stat-label">Net unit price</span><span class="stat-value" style="font-size: 1.3rem">{{ fmt.money(selected.quote.netUnitPrice, selected.quote.currency) }}</span></div>
          <div class="stat-card"><span class="stat-label">Line total ({{ selected.quote.quantity }})</span><span class="stat-value" style="font-size: 1.3rem">{{ fmt.money(selected.quote.lineTotal, selected.quote.currency) }}</span></div>
        </div>
        <table>
          <thead><tr><th>From qty</th><th class="num">Unit price</th><th class="num">vs list</th></tr></thead>
          <tbody>
            <tr v-for="b in selected.quote.breaks" :key="b.minQuantity" :class="{ selected: b.minQuantity <= quoteQty && !selected.quote.breaks.some(x => x.minQuantity > b.minQuantity && x.minQuantity <= quoteQty) }">
              <td>{{ b.minQuantity }}+</td>
              <td class="num cell-strong">{{ fmt.money(b.unitPrice, selected.quote.currency) }}</td>
              <td class="num" style="color: var(--good)">{{ selected.price ? `−${(((selected.price - b.unitPrice) / selected.price) * 100).toFixed(1)}%` : '—' }}</td>
            </tr>
          </tbody>
        </table>
      </template>
      <h4 style="font-size: .9rem">List price history</h4>
      <table>
        <thead><tr><th>Amount</th><th>From</th><th>To</th></tr></thead>
        <tbody>
          <tr v-for="(h, i) in selected.priceHistory" :key="i">
            <td class="cell-strong">{{ fmt.money(h.amount, h.currency) }}</td>
            <td class="cell-muted">{{ fmt.date(h.validFromUtc) }}</td>
            <td><span v-if="!h.validToUtc" class="badge good"><span class="dot" />Current</span><span v-else class="cell-muted">{{ fmt.date(h.validToUtc) }}</span></td>
          </tr>
          <tr v-if="!selected.priceHistory.length"><td colspan="3" class="cell-muted">No prices recorded.</td></tr>
        </tbody>
      </table>
    </template>

    <div v-if="session.can('catalogueAdmin')" class="row">
      <router-link :to="`/products/${selected.id}`" class="btn btn-primary"><Icon name="edit" :size="15" /> Edit product</router-link>
      <button class="btn-secondary" @click="toggleStatus(selected)"><Icon name="power" :size="15" /> {{ selected.status === 'Active' ? 'Deactivate' : 'Activate' }}</button>
    </div>
  </Overlay>
</template>
