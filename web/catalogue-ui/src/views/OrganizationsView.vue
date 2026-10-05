<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import Icon from '../components/Icon.vue'
import EmptyState from '../components/EmptyState.vue'
import Overlay from '../components/Overlay.vue'
import { api, errorText, type Organization, type OrgUser, type PriceListInfo } from '../api'
import { fmt, toast } from '../ui'

const orgs = ref<Organization[]>([])
const lists = ref<PriceListInfo[]>([])
const tab = ref<'PendingVerification' | 'Active' | 'Suspended' | 'Rejected' | ''>('PendingVerification')
const approving = ref<Organization | null>(null)
const approvePriceList = ref<number | ''>('')
const approveCode = ref('')
const reviewing = ref<{ org: Organization; action: 'reject' | 'suspend' } | null>(null)
const note = ref('')
const usersOf = ref<{ org: Organization; users: OrgUser[] } | null>(null)

async function load() {
  orgs.value = (await api.get<Organization[]>('/organizations', { params: { status: tab.value || undefined } })).data
}
watch(tab, load)
onMounted(async () => {
  load()
  lists.value = (await api.get<PriceListInfo[]>('/pricelists')).data
  approvePriceList.value = lists.value.find(l => l.code === 'TRADE')?.id ?? ''
})

const counts = ref<Record<string, number>>({})
watch(orgs, async () => {
  const all = (await api.get<Organization[]>('/organizations')).data
  counts.value = all.reduce((a, o) => ({ ...a, [o.status]: (a[o.status] ?? 0) + 1 }), {} as Record<string, number>)
}, { immediate: false })

async function approve() {
  const o = approving.value!
  try {
    const { data } = await api.post(`/organizations/${o.id}/approve`, { priceListId: approvePriceList.value || null, supplierCode: approveCode.value || null })
    toast('good', `${o.name} approved`, [data.supplierId ? 'seller account created' : '', data.customerAccountId ? 'buyer account created' : ''].filter(Boolean).join(' · '))
    approving.value = null
    load()
  } catch (e) { toast('bad', 'Approval failed', errorText(e)) }
}
async function review() {
  const r = reviewing.value!
  try {
    await api.post(`/organizations/${r.org.id}/${r.action}`, { note: note.value || null })
    toast('info', `${r.org.name} ${r.action === 'reject' ? 'rejected' : 'suspended'}`, 'Their sessions were signed out.')
    reviewing.value = null
    note.value = ''
    load()
  } catch (e) { toast('bad', 'Action failed', errorText(e)) }
}
async function reactivate(o: Organization) {
  try { await api.post(`/organizations/${o.id}/reactivate`); toast('good', `${o.name} reactivated`); load() }
  catch (e) { toast('bad', 'Action failed', errorText(e)) }
}
async function showUsers(o: Organization) {
  usersOf.value = { org: o, users: (await api.get<OrgUser[]>(`/organizations/${o.id}/users`)).data }
}
const tabs = computed(() => [
  { v: 'PendingVerification', l: 'Awaiting verification' }, { v: 'Active', l: 'Active' }, { v: 'Suspended', l: 'Suspended' }, { v: 'Rejected', l: 'Rejected' }, { v: '', l: 'All' }
] as const)
</script>

<template>
  <div class="page-header">
    <div class="page-title-group">
      <h1 class="page-title">Companies</h1>
      <span class="page-subtitle">Verify registrations (KYB) before they trade. Approval creates their seller and/or buyer accounts.</span>
    </div>
  </div>

  <div class="card">
    <div class="toolbar">
      <div class="segmented">
        <button v-for="t in tabs" :key="t.v" :class="{ active: tab === t.v }" @click="tab = t.v">
          {{ t.l }} <span v-if="t.v && counts[t.v]" class="count">{{ counts[t.v] }}</span>
        </button>
      </div>
    </div>
    <div class="table-wrap">
      <table>
        <thead><tr><th>Company</th><th>Trades as</th><th>Accounts</th><th>Users</th><th>Registered</th><th>Status</th><th></th></tr></thead>
        <tbody>
          <tr v-for="o in orgs" :key="o.id">
            <td><div class="cell-title"><span class="cell-strong">{{ o.name }} <span v-if="o.isPlatformOwner" class="badge primary">Owner</span></span>
              <small>{{ o.country }}<template v-if="o.vatNumber"> · VAT {{ o.vatNumber }}</template></small></div></td>
            <td><div class="chips"><span v-if="o.canBuy" class="badge info"><span class="dot" />Buyer</span><span v-if="o.canSell" class="badge good"><span class="dot" />Seller</span></div></td>
            <td class="cell-muted" style="font-size: .8rem">
              <div v-if="o.supplierCode">Supplier <code>{{ o.supplierCode }}</code></div>
              <div v-if="o.customerAccountNumber">Customer <code>{{ o.customerAccountNumber }}</code></div>
              <span v-if="!o.supplierCode && !o.customerAccountNumber">—</span>
            </td>
            <td><button class="btn-ghost btn-sm" @click="showUsers(o)"><Icon name="user" :size="13" /> {{ o.userCount }}</button></td>
            <td class="cell-muted">{{ fmt.ago(o.createdAtUtc) }}</td>
            <td>
              <div class="cell-title">
                <span class="badge" :class="o.status === 'Active' ? 'good' : o.status === 'PendingVerification' ? 'warn' : 'bad'"><span class="dot" />{{ o.status === 'PendingVerification' ? 'Pending' : o.status }}</span>
                <small v-if="o.reviewedBy">{{ o.reviewedBy }} · {{ fmt.ago(o.reviewedAtUtc) }}<template v-if="o.reviewNote"> · “{{ o.reviewNote }}”</template></small>
              </div>
            </td>
            <td class="num">
              <div v-if="!o.isPlatformOwner" class="row" style="justify-content: flex-end; gap: .3rem">
                <button v-if="o.status === 'PendingVerification' || o.status === 'Rejected'" class="btn-primary btn-sm" @click="approving = o; approveCode = ''"><Icon name="check" :size="13" /> Approve</button>
                <button v-if="o.status === 'PendingVerification'" class="btn-danger btn-sm" @click="reviewing = { org: o, action: 'reject' }">Reject</button>
                <button v-if="o.status === 'Active'" class="btn-ghost btn-sm" @click="reviewing = { org: o, action: 'suspend' }">Suspend</button>
                <button v-if="o.status === 'Suspended'" class="btn-secondary btn-sm" @click="reactivate(o)">Reactivate</button>
              </div>
            </td>
          </tr>
          <tr v-if="!orgs.length"><td colspan="7"><EmptyState icon="checkCircle" title="Nothing here" :text="tab === 'PendingVerification' ? 'No registrations are waiting for verification.' : undefined" /></td></tr>
        </tbody>
      </table>
    </div>
  </div>

  <Overlay v-if="approving" :title="`Approve ${approving.name}`" subtitle="Check the VAT number and company details before approving." @close="approving = null">
    <div class="stack">
      <div v-if="approving.canSell" class="field">
        <label for="sc">Supplier code (seller)</label>
        <input id="sc" v-model="approveCode" :placeholder="approving.name.replace(/[^A-Za-z0-9]/g, '').toUpperCase().slice(0, 10)" style="text-transform: uppercase" />
        <span class="hint">Their offers and purchase orders appear under this code.</span>
      </div>
      <div v-if="approving.canBuy" class="field">
        <label for="pl">Buyer price list</label>
        <select id="pl" v-model="approvePriceList"><option value="">List price</option><option v-for="l in lists" :key="l.id" :value="l.id">{{ l.code }} · {{ l.name }} ({{ l.discountPercent }}%)</option></select>
      </div>
    </div>
    <template #footer>
      <button class="btn-secondary" @click="approving = null">Cancel</button>
      <button class="btn-primary" @click="approve"><Icon name="check" :size="15" /> Approve & create accounts</button>
    </template>
  </Overlay>

  <Overlay v-if="reviewing" :title="`${reviewing.action === 'reject' ? 'Reject' : 'Suspend'} ${reviewing.org.name}`" @close="reviewing = null">
    <div class="field"><label for="rn">Reason (shown to the company at sign-in)</label><textarea id="rn" v-model="note" rows="3" /></div>
    <template #footer>
      <button class="btn-secondary" @click="reviewing = null">Cancel</button>
      <button class="btn-danger" @click="review">{{ reviewing.action === 'reject' ? 'Reject' : 'Suspend' }}</button>
    </template>
  </Overlay>

  <Overlay v-if="usersOf" kind="drawer" :title="usersOf.org.name" subtitle="Company users" @close="usersOf = null">
    <div class="list" style="border: 1px solid var(--border); border-radius: var(--radius)">
      <div v-for="u in usersOf.users" :key="u.id" class="list-item">
        <span class="avatar" style="width: 30px; height: 30px; font-size: .7rem">{{ u.displayName.slice(0, 2).toUpperCase() }}</span>
        <div class="list-main"><div class="list-title">{{ u.displayName }}</div><div class="list-sub">{{ u.email }} · {{ u.roles.join(', ') }}</div></div>
        <span class="muted" style="font-size: .74rem">{{ fmt.ago(u.lastLoginUtc) }}</span>
      </div>
    </div>
  </Overlay>
</template>
