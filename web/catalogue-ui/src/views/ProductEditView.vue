<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import Icon from '../components/Icon.vue'
import StatusBadge from '../components/StatusBadge.vue'
import MediaManager from '../components/MediaManager.vue'
import { api, errorText, type AttributeDef, type Category, type ProductDetail, type TemplateItem } from '../api'
import { fmt, toast } from '../ui'

const route = useRoute()
const router = useRouter()
const id = route.params.id as string | undefined
const isEdit = !!id

const form = ref({
  sku: '', name: '', description: '', brand: '', manufacturerPartNumber: '', gtin: '',
  price: undefined as number | undefined, currency: 'GBP', quantity: undefined as number | undefined,
  categoryIds: [] as number[]
})
const specValues = ref<Record<string, string>>({})
const existing = ref<ProductDetail | null>(null)
const categories = ref<Category[]>([])
const attributeDefs = ref<AttributeDef[]>([])
const templates = ref<Record<number, TemplateItem[]>>({})
const serverErrors = ref<Record<string, string>>({})
const touched = ref(false)
const saving = ref(false)
const loading = ref(isEdit)

/** GS1 mod-10 check, mirrored from the server so mistakes show while typing. */
function gtinValid(g: string) {
  if (!/^(\d{8}|\d{12}|\d{13}|\d{14})$/.test(g)) return false
  const digits = g.slice(0, -1).split('').reverse()
  const sum = digits.reduce((acc, d, i) => acc + Number(d) * (i % 2 === 0 ? 3 : 1), 0)
  return (10 - (sum % 10)) % 10 === Number(g[g.length - 1])
}

// The union of the selected categories' templates (required wins).
const template = computed(() => {
  const merged = new Map<string, { item: TemplateItem; def?: AttributeDef }>()
  for (const cid of form.value.categoryIds)
    for (const t of templates.value[cid] ?? []) {
      const prev = merged.get(t.code)
      if (!prev || (t.required && !prev.item.required)) merged.set(t.code, { item: t, def: attributeDefs.value.find(d => d.id === t.attributeId) })
    }
  return [...merged.values()].sort((a, b) => Number(b.item.required) - Number(a.item.required) || a.item.sortOrder - b.item.sortOrder)
})

const errors = computed(() => {
  const e: Record<string, string> = { ...serverErrors.value }
  if (!form.value.sku.trim()) e.sku = 'SKU is required.'
  else if (form.value.sku.length > 64) e.sku = 'SKU must be 64 characters or fewer.'
  if (!form.value.name.trim()) e.name = 'Name is required.'
  if (form.value.gtin && !gtinValid(form.value.gtin.trim())) e.gtin = 'Not a valid GTIN-8/12/13/14 (check digit).'
  if (form.value.price != null && form.value.price < 0) e.price = 'Price cannot be negative.'
  if (form.value.quantity != null && form.value.quantity < 0) e.quantity = 'Quantity cannot be negative.'
  for (const { item, def } of template.value) {
    const v = (specValues.value[item.code] ?? '').trim()
    const key = `attr.${item.code}`
    if (!v) { if (item.required) e[key] = `${item.name} is required for this category.`; continue }
    if (def?.dataType === 'Number') {
      const n = Number(v)
      if (Number.isNaN(n)) e[key] = 'Must be a number.'
      else if (def.minValue != null && n < def.minValue) e[key] = `Minimum ${def.minValue}${def.unit ? ' ' + def.unit : ''}.`
      else if (def.maxValue != null && n > def.maxValue) e[key] = `Maximum ${def.maxValue}${def.unit ? ' ' + def.unit : ''}.`
    }
  }
  return e
})
const valid = computed(() => Object.keys(errors.value).length === 0)
const requiredFilled = computed(() => template.value.filter(t => t.item.required && (specValues.value[t.item.code] ?? '').trim()).length)
const requiredTotal = computed(() => template.value.filter(t => t.item.required).length)

async function loadTemplate(cid: number) {
  if (templates.value[cid]) return
  templates.value = { ...templates.value, [cid]: (await api.get<TemplateItem[]>(`/categories/${cid}/attributes`)).data }
}
watch(() => [...form.value.categoryIds], ids => ids.forEach(loadTemplate))
watch([form, specValues], () => { serverErrors.value = {} }, { deep: true })

async function save() {
  touched.value = true
  if (!valid.value) {
    toast('bad', 'Fix the highlighted fields', `${Object.keys(errors.value).length} problem(s)`)
    return
  }
  saving.value = true
  const attributes = Object.fromEntries(template.value
    .map(t => [t.item.code, (specValues.value[t.item.code] ?? '').trim()]).filter(([, v]) => v))
  const body = { ...form.value, gtin: form.value.gtin.trim() || null, attributes }
  try {
    if (isEdit) await api.put(`/products/${id}`, body)
    else await api.post('/products', body)
    toast('good', isEdit ? 'Product updated' : 'Product created', form.value.sku.toUpperCase())
    router.push('/catalogue')
  } catch (e: any) {
    const problems = e.response?.data?.errors as Record<string, string[]> | undefined
    if (problems) {
      serverErrors.value = Object.fromEntries(Object.entries(problems).map(([k, v]) => [k, v[0]]))
      toast('bad', 'The server rejected some values', Object.values(serverErrors.value)[0])
    } else toast('bad', 'Save failed', errorText(e))
  } finally {
    saving.value = false
  }
}

function toggleCategory(cid: number) {
  const i = form.value.categoryIds.indexOf(cid)
  if (i >= 0) form.value.categoryIds.splice(i, 1)
  else form.value.categoryIds.push(cid)
}
const roots = computed(() => categories.value.filter(c => !c.parentCategoryId))
const childrenOf = (pid: number) => categories.value.filter(c => c.parentCategoryId === pid)

onMounted(async () => {
  const [cats, defs] = await Promise.all([api.get<Category[]>('/categories'), api.get<AttributeDef[]>('/attributes')])
  categories.value = cats.data
  attributeDefs.value = defs.data
  if (isEdit) {
    try {
      const { data } = await api.get<ProductDetail>(`/products/${id}`)
      existing.value = data
      form.value = {
        sku: data.sku, name: data.name, description: data.description ?? '', brand: data.brand ?? '',
        manufacturerPartNumber: data.manufacturerPartNumber ?? '', gtin: data.gtin?.replace(/^0+(?=\d{13}$)/, '') ?? '',
        price: data.price, currency: data.currency ?? 'GBP', quantity: data.availableQuantity, categoryIds: data.categoryIds
      }
      specValues.value = Object.fromEntries(data.specs.filter(s => s.value != null).map(s => [s.code, s.value!]))
    } finally {
      loading.value = false
    }
  }
})
</script>

<template>
  <div class="page-header">
    <div class="page-title-group">
      <router-link to="/catalogue" class="back-link"><Icon name="arrowLeft" :size="14" /> Catalogue</router-link>
      <h1 class="page-title">
        {{ isEdit ? form.name || 'Edit product' : 'New product' }}
        <StatusBadge v-if="existing" :status="existing.status" />
      </h1>
      <span class="page-subtitle">{{ isEdit ? `Last updated ${fmt.ago(existing?.updatedAtUtc)}` : 'Add a product manually. Bulk loads belong in Imports or Event Hub.' }}</span>
    </div>
  </div>

  <form class="grid grid-main-side" @submit.prevent="save">
    <div class="stack">
      <div class="card">
        <div class="card-header"><h3 class="card-title"><Icon name="file" :size="16" /> Details</h3></div>
        <div class="card-body stack">
          <div v-if="loading" class="skeleton" style="height: 120px" />
          <template v-else>
            <div class="form-grid" style="grid-template-columns: 1fr 2fr">
              <div class="field" :class="{ invalid: touched && errors.sku }">
                <label for="sku">SKU</label>
                <input id="sku" v-model="form.sku" :disabled="isEdit" placeholder="e.g. HP-8KW-001" style="text-transform: uppercase" />
                <span v-if="touched && errors.sku" class="field-error">{{ errors.sku }}</span>
                <span v-else class="hint">{{ isEdit ? 'SKU is immutable.' : 'Stored upper-case; unique per supplier.' }}</span>
              </div>
              <div class="field" :class="{ invalid: touched && errors.name }">
                <label for="name">Name</label>
                <input id="name" v-model="form.name" placeholder="e.g. Thermora Aero 8kW Air Source Heat Pump" maxlength="256" />
                <span v-if="touched && errors.name" class="field-error">{{ errors.name }}</span>
              </div>
            </div>
            <div class="form-grid">
              <div class="field">
                <label for="brand">Brand</label>
                <input id="brand" v-model="form.brand" maxlength="100" placeholder="Manufacturer brand" />
              </div>
              <div class="field">
                <label for="mpn">Manufacturer part no.</label>
                <input id="mpn" v-model="form.manufacturerPartNumber" maxlength="64" placeholder="MPN" style="text-transform: uppercase" />
              </div>
              <div class="field" :class="{ invalid: errors.gtin }">
                <label for="gtin">GTIN / EAN</label>
                <input id="gtin" v-model="form.gtin" inputmode="numeric" placeholder="8, 12, 13 or 14 digits" />
                <span v-if="errors.gtin" class="field-error">{{ errors.gtin }}</span>
                <span v-else-if="form.gtin" class="hint" style="color: var(--good)"><Icon name="check" :size="12" /> Valid check digit · matches the same item from other suppliers</span>
                <span v-else class="hint">Used to consolidate offers from different suppliers.</span>
              </div>
            </div>
            <div class="field">
              <label for="desc">Description</label>
              <textarea id="desc" v-model="form.description" rows="3" style="resize: vertical" placeholder="Optional description shown to buyers" />
            </div>
          </template>
        </div>
      </div>

      <div class="card">
        <div class="card-header">
          <h3 class="card-title"><Icon name="layers" :size="16" /> Specifications</h3>
          <span v-if="template.length" class="card-sub">{{ requiredFilled }}/{{ requiredTotal }} required filled</span>
        </div>
        <div class="card-body">
          <div v-if="!form.categoryIds.length" class="alert info"><Icon name="info" :size="16" />Pick a category to see its specification template.</div>
          <div v-else-if="!template.length" class="alert info"><Icon name="info" :size="16" />The selected categories have no specification template.
            <router-link to="/admin/categories">Configure templates</router-link></div>
          <div v-else class="form-grid">
            <div v-for="{ item, def } in template" :key="item.code" class="field" :class="{ invalid: touched && errors[`attr.${item.code}`] }">
              <label :for="`a-${item.code}`">
                {{ item.name }}<span v-if="item.unit" class="muted"> ({{ item.unit }})</span>
                <span v-if="item.required" style="color: var(--bad)"> *</span>
              </label>
              <select v-if="def?.dataType === 'Enum'" :id="`a-${item.code}`" v-model="specValues[item.code]">
                <option value="">—</option>
                <option v-for="v in def.allowedValues" :key="v" :value="v">{{ v }}</option>
              </select>
              <select v-else-if="def?.dataType === 'Boolean'" :id="`a-${item.code}`" v-model="specValues[item.code]">
                <option value="">—</option><option value="true">Yes</option><option value="false">No</option>
              </select>
              <input v-else :id="`a-${item.code}`" v-model="specValues[item.code]" :inputmode="def?.dataType === 'Number' ? 'decimal' : 'text'"
                :placeholder="def?.dataType === 'Number' && def.minValue != null ? `${def.minValue}–${def.maxValue}` : ''" />
              <span v-if="touched && errors[`attr.${item.code}`]" class="field-error">{{ errors[`attr.${item.code}`] }}</span>
            </div>
          </div>
        </div>
      </div>

      <div v-if="isEdit" class="card">
        <div class="card-header">
          <h3 class="card-title"><Icon name="eye" :size="16" /> Images & documents</h3>
          <span class="card-sub">Stored in Blob storage (Azurite → ./.azurite)</span>
        </div>
        <div class="card-body"><MediaManager :product-id="Number(id)" /></div>
      </div>

      <div class="card">
        <div class="card-header">
          <h3 class="card-title"><Icon name="tag" :size="16" /> List price & stock</h3>
          <span class="card-sub">Trade/customer prices live in Pricing</span>
        </div>
        <div class="card-body">
          <div class="form-grid">
            <div class="field" :class="{ invalid: touched && errors.price }">
              <label for="price">List price</label>
              <input id="price" v-model.number="form.price" type="number" step="0.01" min="0" placeholder="0.00" />
              <span v-if="touched && errors.price" class="field-error">{{ errors.price }}</span>
              <span v-else-if="isEdit" class="hint">Changing it closes the current price and starts a new one.</span>
            </div>
            <div class="field">
              <label for="cur">Currency</label>
              <select id="cur" v-model="form.currency"><option>GBP</option><option>USD</option><option>EUR</option></select>
            </div>
            <div class="field" :class="{ invalid: touched && errors.quantity }">
              <label for="qty">Opening stock</label>
              <input id="qty" v-model.number="form.quantity" type="number" min="0" placeholder="0" :disabled="isEdit" />
              <span v-if="touched && errors.quantity" class="field-error">{{ errors.quantity }}</span>
              <span v-else-if="isEdit" class="hint">Stock is maintained by supplier feeds.</span>
            </div>
          </div>
        </div>
      </div>
    </div>

    <div class="stack">
      <div class="card">
        <div class="card-header"><h3 class="card-title"><Icon name="grid" :size="16" /> Categories</h3><span class="card-sub">{{ form.categoryIds.length }} selected</span></div>
        <div class="card-body stack" style="gap: .8rem">
          <div v-for="r in roots" :key="r.id">
            <div class="stat-label" style="margin-bottom: .35rem">{{ r.name }}</div>
            <div class="chips">
              <span v-for="c in childrenOf(r.id)" :key="c.id" class="chip" :class="{ active: form.categoryIds.includes(c.id) }"
                role="checkbox" :aria-checked="form.categoryIds.includes(c.id)" tabindex="0"
                @click="toggleCategory(c.id)" @keydown.enter.prevent="toggleCategory(c.id)">
                <Icon v-if="form.categoryIds.includes(c.id)" name="check" :size="12" />{{ c.name }}
              </span>
            </div>
          </div>
        </div>
      </div>

      <div class="card card-pad stack" style="gap: .6rem; position: sticky; top: 76px">
        <button type="submit" class="btn-primary btn-lg" :disabled="saving">
          <Icon :name="saving ? 'refresh' : 'check'" :size="16" :class="{ spin: saving }" /> {{ isEdit ? 'Save changes' : 'Create product' }}
        </button>
        <router-link to="/catalogue" class="btn btn-secondary">Cancel</router-link>
        <div v-if="touched && !valid" class="alert bad"><Icon name="alertCircle" :size="16" />{{ Object.keys(errors).length }} field(s) need attention.</div>
      </div>
    </div>
  </form>
</template>
