<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import Icon from '../components/Icon.vue'
import EmptyState from '../components/EmptyState.vue'
import Overlay from '../components/Overlay.vue'
import { api, errorText, type AttributeDef, type CategoryNode, type TemplateItem } from '../api'
import { session } from '../session'
import { fmt, toast } from '../ui'

const router = useRouter()
const canEdit = session.can('catalogueAdmin')
const nodes = ref<CategoryNode[]>([])
const defs = ref<AttributeDef[]>([])
const selectedId = ref<number | null>(null)
const template = ref<TemplateItem[]>([])
const draft = ref({ name: '', parentCategoryId: null as number | null, sortOrder: 0, isActive: true })
const dirtyTemplate = ref(false)
const saving = ref(false)
const expanded = ref<Set<number>>(new Set())
const addAttrId = ref<number | ''>('')
const showNewCategory = ref(false)
const newCategory = ref({ code: '', name: '', parentCategoryId: null as number | null })
const showNewAttr = ref(false)
const newAttr = ref({ code: '', name: '', dataType: 'Number', unit: '', allowed: '', minValue: '' as number | '', maxValue: '' as number | '', isFilterable: true })
const tab = ref<'tree' | 'dictionary'>('tree')

const roots = computed(() => nodes.value.filter(n => !n.parentCategoryId))
const childrenOf = (id: number) => nodes.value.filter(n => n.parentCategoryId === id)
const selected = computed(() => nodes.value.find(n => n.id === selectedId.value) ?? null)
const own = computed(() => template.value.filter(t => !t.inherited).sort((a, b) => a.sortOrder - b.sortOrder))
const inherited = computed(() => template.value.filter(t => t.inherited))
const available = computed(() => defs.value.filter(d => !template.value.some(t => t.attributeId === d.id)))
const rootTotal = (r: CategoryNode) => r.productCount + childrenOf(r.id).reduce((a, c) => a + c.productCount, 0)

async function load() {
  const [tree, attrs] = await Promise.all([api.get<CategoryNode[]>('/categories/tree'), api.get<AttributeDef[]>('/attributes')])
  nodes.value = tree.data
  defs.value = attrs.data
  if (!expanded.value.size) roots.value.forEach(r => expanded.value.add(r.id))
  if (!selectedId.value) selectedId.value = childrenOf(roots.value[0]?.id ?? -1)[0]?.id ?? roots.value[0]?.id ?? null
}

watch(selectedId, async id => {
  if (id == null) return
  const n = nodes.value.find(x => x.id === id)
  if (n) draft.value = { name: n.name, parentCategoryId: n.parentCategoryId ?? null, sortOrder: n.sortOrder, isActive: n.isActive }
  template.value = (await api.get<TemplateItem[]>(`/categories/${id}/attributes`)).data
  dirtyTemplate.value = false
})

function toggle(id: number) {
  expanded.value.has(id) ? expanded.value.delete(id) : expanded.value.add(id)
}

async function saveCategory() {
  if (!selected.value) return
  saving.value = true
  try {
    await api.put(`/categories/${selected.value.id}`, { code: selected.value.code, ...draft.value })
    toast('good', 'Category saved', draft.value.name)
    await load()
  } catch (e) { toast('bad', 'Could not save category', errorText(e)) }
  finally { saving.value = false }
}

function addAttribute() {
  const def = defs.value.find(d => d.id === addAttrId.value)
  if (!def) return
  template.value.push({ attributeId: def.id, code: def.code, name: def.name, dataType: def.dataType, unit: def.unit,
    required: false, sortOrder: (own.value[own.value.length - 1]?.sortOrder ?? 0) + 10, inherited: false })
  addAttrId.value = ''
  dirtyTemplate.value = true
}
function removeAttribute(t: TemplateItem) {
  template.value = template.value.filter(x => x !== t)
  dirtyTemplate.value = true
}
function move(t: TemplateItem, dir: -1 | 1) {
  const list = own.value
  const i = list.indexOf(t), j = i + dir
  if (j < 0 || j >= list.length) return
  ;[list[i].sortOrder, list[j].sortOrder] = [list[j].sortOrder, list[i].sortOrder]
  if (list[i].sortOrder === list[j].sortOrder) list[j].sortOrder += dir
  dirtyTemplate.value = true
}

async function saveTemplate() {
  if (!selected.value) return
  saving.value = true
  try {
    await api.put(`/categories/${selected.value.id}/attributes`,
      own.value.map((t, i) => ({ attributeId: t.attributeId, required: t.required, sortOrder: (i + 1) * 10 })))
    toast('good', 'Specification template saved', `${own.value.length} attributes on ${selected.value.name}`)
    dirtyTemplate.value = false
    await load()
  } catch (e) { toast('bad', 'Could not save template', errorText(e)) }
  finally { saving.value = false }
}

async function createCategory() {
  try {
    const { data } = await api.post('/categories', newCategory.value)
    toast('good', 'Category created', newCategory.value.name)
    showNewCategory.value = false
    newCategory.value = { code: '', name: '', parentCategoryId: null }
    await load()
    selectedId.value = data.id
  } catch (e) { toast('bad', 'Could not create category', errorText(e)) }
}

async function createAttribute() {
  const a = newAttr.value
  try {
    await api.post('/attributes', {
      code: a.code, name: a.name, dataType: a.dataType, unit: a.unit || null,
      allowedValues: a.dataType === 'Enum' ? a.allowed.split(',').map(s => s.trim()).filter(Boolean) : null,
      minValue: a.minValue === '' ? null : a.minValue, maxValue: a.maxValue === '' ? null : a.maxValue, isFilterable: a.isFilterable
    })
    toast('good', 'Attribute created', a.name)
    showNewAttr.value = false
    newAttr.value = { code: '', name: '', dataType: 'Number', unit: '', allowed: '', minValue: '', maxValue: '', isFilterable: true }
    defs.value = (await api.get<AttributeDef[]>('/attributes')).data
  } catch (e) { toast('bad', 'Could not create attribute', errorText(e)) }
}

const typeIcon: Record<string, string> = { Number: 'hash', Enum: 'menu', Text: 'file', Boolean: 'check' }
onMounted(load)
</script>

<template>
  <div class="page-header">
    <div class="page-title-group">
      <h1 class="page-title">Categories & specifications</h1>
      <span class="page-subtitle">The product taxonomy and, per category, which specifications products must carry.</span>
    </div>
    <div class="page-actions">
      <div class="segmented">
        <button :class="{ active: tab === 'tree' }" @click="tab = 'tree'">Category tree</button>
        <button :class="{ active: tab === 'dictionary' }" @click="tab = 'dictionary'">Attribute dictionary <span class="count">{{ defs.length }}</span></button>
      </div>
      <button v-if="canEdit && tab === 'tree'" class="btn-primary" @click="showNewCategory = true"><Icon name="plus" :size="15" /> New category</button>
      <button v-if="canEdit && tab === 'dictionary'" class="btn-primary" @click="showNewAttr = true"><Icon name="plus" :size="15" /> New attribute</button>
    </div>
  </div>

  <div v-if="tab === 'tree'" class="grid" style="grid-template-columns: minmax(260px, 340px) minmax(0, 1fr); align-items: start">
    <div class="card">
      <div class="list">
        <template v-for="r in roots" :key="r.id">
          <div class="list-item clickable" :style="selectedId === r.id ? 'background: var(--primary-soft)' : ''" style="cursor: pointer" @click="selectedId = r.id">
            <button class="btn-ghost btn-sm" style="padding: .1rem" :aria-label="expanded.has(r.id) ? 'Collapse' : 'Expand'" @click.stop="toggle(r.id)">
              <Icon :name="expanded.has(r.id) ? 'chevronDown' : 'chevronRight'" :size="14" />
            </button>
            <div class="list-main">
              <div class="list-title">{{ r.name }} <span v-if="!r.isActive" class="badge">Inactive</span></div>
              <div class="list-sub">{{ childrenOf(r.id).length }} subcategories · {{ fmt.compact(rootTotal(r)) }} products</div>
            </div>
          </div>
          <template v-if="expanded.has(r.id)">
            <div v-for="c in childrenOf(r.id)" :key="c.id" class="list-item" style="cursor: pointer; padding-left: 3rem"
              :style="selectedId === c.id ? 'background: var(--primary-soft)' : ''" @click="selectedId = c.id">
              <div class="list-main">
                <div class="list-title">{{ c.name }} <span v-if="!c.isActive" class="badge">Inactive</span></div>
                <div class="list-sub"><code style="font-size: .7rem">{{ c.code }}</code> · {{ fmt.compact(c.productCount) }} products</div>
              </div>
              <span class="tag" :title="`${c.requiredAttributeCount} required of ${c.attributeCount} specs`">{{ c.requiredAttributeCount }}/{{ c.attributeCount }}</span>
            </div>
          </template>
        </template>
        <EmptyState v-if="!nodes.length" icon="layers" title="No categories" />
      </div>
    </div>

    <div v-if="selected" class="stack">
      <div class="card">
        <div class="card-header">
          <div>
            <h3 class="card-title">{{ selected.name }} <code style="font-size: .72rem">{{ selected.code }}</code></h3>
            <div class="card-sub">{{ fmt.n(selected.productCount) }} products directly in this category</div>
          </div>
          <button class="btn-secondary btn-sm" @click="router.push({ path: '/catalogue', query: { category: selected.id } })">
            Browse products <Icon name="arrowRight" :size="14" />
          </button>
        </div>
        <div class="card-body">
          <div class="form-grid">
            <div class="field"><label for="cn">Name</label><input id="cn" v-model="draft.name" :disabled="!canEdit" /></div>
            <div class="field">
              <label for="cp">Parent</label>
              <select id="cp" v-model="draft.parentCategoryId" :disabled="!canEdit">
                <option :value="null">— Top level —</option>
                <option v-for="r in roots.filter(r => r.id !== selected!.id)" :key="r.id" :value="r.id">{{ r.name }}</option>
              </select>
            </div>
            <div class="field"><label for="cs">Sort order</label><input id="cs" v-model.number="draft.sortOrder" type="number" :disabled="!canEdit" /></div>
            <div class="field">
              <label>Status</label>
              <label class="row" style="margin: .45rem 0 0; font-weight: 500"><input v-model="draft.isActive" type="checkbox" :disabled="!canEdit" /> Active (imports accept this category)</label>
            </div>
          </div>
          <div v-if="canEdit" class="row mt-2"><button class="btn-primary" :disabled="saving" @click="saveCategory"><Icon name="check" :size="15" /> Save category</button></div>
        </div>
      </div>

      <div class="card">
        <div class="card-header">
          <div>
            <h3 class="card-title"><Icon name="layers" :size="16" /> Specification template</h3>
            <div class="card-sub">Validated on every import row and in the product editor; required specs drive data-quality checks.</div>
          </div>
          <button v-if="canEdit" class="btn-primary btn-sm" :disabled="!dirtyTemplate || saving" @click="saveTemplate"><Icon name="check" :size="14" /> Save template</button>
        </div>
        <div class="table-wrap">
          <table>
            <thead><tr><th style="width: 34px"></th><th>Attribute</th><th>Type</th><th>Unit</th><th>Required</th><th v-if="canEdit"></th></tr></thead>
            <tbody>
              <tr v-for="t in inherited" :key="'i' + t.attributeId" style="opacity: .7">
                <td><Icon :name="typeIcon[t.dataType] ?? 'file'" :size="14" /></td>
                <td>{{ t.name }} <span class="tag">inherited</span></td>
                <td class="cell-muted">{{ t.dataType }}</td><td class="cell-muted">{{ t.unit ?? '—' }}</td>
                <td><span v-if="t.required" class="badge warn">Required</span><span v-else class="muted">Optional</span></td>
                <td v-if="canEdit" />
              </tr>
              <tr v-for="(t, i) in own" :key="t.attributeId">
                <td><Icon :name="typeIcon[t.dataType] ?? 'file'" :size="14" /></td>
                <td><span class="cell-strong">{{ t.name }}</span> <code style="font-size: .7rem">{{ t.code }}</code></td>
                <td class="cell-muted">{{ t.dataType }}</td>
                <td class="cell-muted">{{ t.unit ?? '—' }}</td>
                <td>
                  <label class="row" style="margin: 0; font-weight: 500"><input v-model="t.required" type="checkbox" :disabled="!canEdit" @change="dirtyTemplate = true" /> {{ t.required ? 'Required' : 'Optional' }}</label>
                </td>
                <td v-if="canEdit" class="num">
                  <div class="row" style="justify-content: flex-end; gap: .15rem">
                    <button class="btn-ghost btn-sm" :disabled="i === 0" aria-label="Move up" @click="move(t, -1)"><Icon name="chevronUp" :size="14" /></button>
                    <button class="btn-ghost btn-sm" :disabled="i === own.length - 1" aria-label="Move down" @click="move(t, 1)"><Icon name="chevronDown" :size="14" /></button>
                    <button class="btn-ghost btn-sm" aria-label="Remove" @click="removeAttribute(t)"><Icon name="trash" :size="14" style="color: var(--bad)" /></button>
                  </div>
                </td>
              </tr>
              <tr v-if="!template.length"><td colspan="6"><EmptyState icon="layers" title="No specifications yet" text="Add attributes that products in this category should carry." /></td></tr>
            </tbody>
          </table>
        </div>
        <div v-if="canEdit" class="card-footer row">
          <select v-model="addAttrId" aria-label="Attribute to add" style="flex: 1; max-width: 360px">
            <option value="">Add an attribute…</option>
            <option v-for="d in available" :key="d.id" :value="d.id">{{ d.name }}{{ d.unit ? ` (${d.unit})` : '' }} — {{ d.dataType }}</option>
          </select>
          <button class="btn-secondary btn-sm" :disabled="!addAttrId" @click="addAttribute"><Icon name="plus" :size="14" /> Add</button>
          <span v-if="dirtyTemplate" class="muted" style="font-size: .78rem">Unsaved changes</span>
        </div>
      </div>
    </div>
  </div>

  <div v-else class="card">
    <div class="table-wrap">
      <table>
        <thead><tr><th>Attribute</th><th>Code</th><th>Type</th><th>Unit</th><th>Allowed values / range</th><th class="num">Used by</th><th>Filterable</th></tr></thead>
        <tbody>
          <tr v-for="d in defs" :key="d.id">
            <td><div class="cell-title"><span class="cell-strong">{{ d.name }}</span><small v-if="d.description">{{ d.description }}</small></div></td>
            <td><code>{{ d.code }}</code></td>
            <td><span class="row" style="gap: .35rem"><Icon :name="typeIcon[d.dataType] ?? 'file'" :size="13" />{{ d.dataType }}</span></td>
            <td class="cell-muted">{{ d.unit ?? '—' }}</td>
            <td style="max-width: 320px">
              <div v-if="d.allowedValues.length" class="chips"><span v-for="v in d.allowedValues" :key="v" class="tag">{{ v }}</span></div>
              <span v-else-if="d.minValue != null || d.maxValue != null" class="secondary-text">{{ d.minValue ?? '−∞' }} – {{ d.maxValue ?? '∞' }}</span>
              <span v-else class="muted">Free text</span>
            </td>
            <td class="num">{{ d.usedByCategories }} {{ d.usedByCategories === 1 ? 'category' : 'categories' }}</td>
            <td><Icon v-if="d.isFilterable" name="check" :size="15" style="color: var(--good)" /></td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>

  <Overlay v-if="showNewCategory" title="New category" @close="showNewCategory = false">
    <form id="nc" class="stack" @submit.prevent="createCategory">
      <div class="form-grid" style="grid-template-columns: 1fr 2fr">
        <div class="field"><label for="ncc">Code</label><input id="ncc" v-model="newCategory.code" required placeholder="FAN-COILS" style="text-transform: uppercase" /></div>
        <div class="field"><label for="ncn">Name</label><input id="ncn" v-model="newCategory.name" required placeholder="Fan Coil Units" /></div>
      </div>
      <div class="field">
        <label for="ncp">Parent</label>
        <select id="ncp" v-model="newCategory.parentCategoryId"><option :value="null">— Top level —</option><option v-for="r in roots" :key="r.id" :value="r.id">{{ r.name }}</option></select>
      </div>
    </form>
    <template #footer>
      <button class="btn-secondary" @click="showNewCategory = false">Cancel</button>
      <button class="btn-primary" type="submit" form="nc"><Icon name="plus" :size="15" /> Create</button>
    </template>
  </Overlay>

  <Overlay v-if="showNewAttr" title="New attribute" subtitle="A specification that categories can add to their template." @close="showNewAttr = false">
    <form id="na" class="stack" @submit.prevent="createAttribute">
      <div class="form-grid" style="grid-template-columns: 1fr 2fr">
        <div class="field"><label for="nac">Code</label><input id="nac" v-model="newAttr.code" required placeholder="NOISE_DB" style="text-transform: uppercase" /></div>
        <div class="field"><label for="nan">Name</label><input id="nan" v-model="newAttr.name" required placeholder="Noise level" /></div>
      </div>
      <div class="form-grid">
        <div class="field">
          <label for="nat">Data type</label>
          <select id="nat" v-model="newAttr.dataType"><option>Number</option><option>Enum</option><option>Text</option><option>Boolean</option></select>
        </div>
        <div class="field"><label for="nau">Unit</label><input id="nau" v-model="newAttr.unit" placeholder="kW, mm, dB(A)…" /></div>
      </div>
      <div v-if="newAttr.dataType === 'Enum'" class="field">
        <label for="nav">Allowed values</label><input id="nav" v-model="newAttr.allowed" required placeholder="Comma separated, e.g. R32, R290, R410A" />
      </div>
      <div v-if="newAttr.dataType === 'Number'" class="form-grid">
        <div class="field"><label for="namin">Minimum</label><input id="namin" v-model.number="newAttr.minValue" type="number" step="any" /></div>
        <div class="field"><label for="namax">Maximum</label><input id="namax" v-model.number="newAttr.maxValue" type="number" step="any" /></div>
      </div>
      <label class="row" style="font-weight: 500"><input v-model="newAttr.isFilterable" type="checkbox" /> Show as a catalogue filter</label>
    </form>
    <template #footer>
      <button class="btn-secondary" @click="showNewAttr = false">Cancel</button>
      <button class="btn-primary" type="submit" form="na"><Icon name="plus" :size="15" /> Create</button>
    </template>
  </Overlay>
</template>
