<script setup lang="ts">
import { onMounted, ref } from 'vue'
import Icon from '../components/Icon.vue'
import Overlay from '../components/Overlay.vue'
import { api, errorText, type OrgUser, type SessionResponse } from '../api'
import { session } from '../session'
import { fmt, toast } from '../ui'

const company = ref<any>(null)
const pw = ref({ currentPassword: '', newPassword: '', confirm: '' })
const pwErrors = ref<Record<string, string[]>>({})
const busy = ref(false)
const showAdd = ref(false)
const newUser = ref({ email: '', displayName: '', password: '', roles: ['Buyer'] as string[] })

async function load() {
  if (session.organization) company.value = (await api.get('/organizations/me')).data
}

async function changePassword() {
  pwErrors.value = {}
  if (pw.value.newPassword !== pw.value.confirm) { pwErrors.value = { confirm: ['Passwords do not match.'] }; return }
  busy.value = true
  try {
    const { data } = await api.post<SessionResponse>('/auth/change-password', { currentPassword: pw.value.currentPassword, newPassword: pw.value.newPassword })
    session.set(data.token, data.expiresAtUtc, data.user)
    pw.value = { currentPassword: '', newPassword: '', confirm: '' }
    toast('good', 'Password changed', 'Your other sessions have been signed out.')
  } catch (e: any) {
    pwErrors.value = e.response?.data?.errors ?? { form: [errorText(e)] }
  } finally {
    busy.value = false
  }
}

const assignable = () => ['OrgAdmin', ...(company.value?.canBuy ? ['Buyer'] : []), ...(company.value?.canSell ? ['Seller'] : [])]
function toggleRole(r: string) {
  const i = newUser.value.roles.indexOf(r)
  i >= 0 ? newUser.value.roles.splice(i, 1) : newUser.value.roles.push(r)
}
async function addUser() {
  try {
    await api.post('/organizations/me/users', newUser.value)
    toast('good', 'Colleague added', newUser.value.email)
    showAdd.value = false
    newUser.value = { email: '', displayName: '', password: '', roles: ['Buyer'] }
    load()
  } catch (e: any) {
    const errs = e.response?.data?.errors
    toast('bad', 'Could not add user', errs ? Object.values(errs).flat().join(' ') : errorText(e))
  }
}
onMounted(load)
</script>

<template>
  <div class="page-header">
    <div class="page-title-group">
      <h1 class="page-title">Account & company</h1>
      <span class="page-subtitle">Your profile, password and the company you trade as.</span>
    </div>
  </div>

  <div class="grid grid-main-side">
    <div class="stack">
      <div v-if="company" class="card">
        <div class="card-header">
          <div>
            <h3 class="card-title"><Icon name="truck" :size="16" /> {{ company.name }}</h3>
            <div class="card-sub">{{ company.country }}<template v-if="company.vatNumber"> · VAT {{ company.vatNumber }}</template></div>
          </div>
          <div class="chips">
            <span v-if="company.isPlatformOwner" class="badge primary">Platform owner</span>
            <span v-if="company.canBuy" class="badge info"><span class="dot" />Buys</span>
            <span v-if="company.canSell" class="badge good"><span class="dot" />Sells</span>
          </div>
        </div>
        <div class="card-body grid grid-2">
          <div class="stat-card">
            <span class="stat-label">As a buyer</span>
            <template v-if="company.customerAccount">
              <span class="cell-strong">{{ company.customerAccount.accountNumber }}</span>
              <span class="stat-foot">Price list {{ company.customerAccount.priceList ?? 'list price' }} · <router-link to="/basket">Basket</router-link> · <router-link to="/orders">Orders</router-link></span>
            </template>
            <span v-else class="muted">Not enabled</span>
          </div>
          <div class="stat-card">
            <span class="stat-label">As a seller</span>
            <template v-if="company.supplier">
              <span class="cell-strong">{{ company.supplier.code }} · {{ company.supplier.name }}</span>
              <span class="stat-foot"><router-link to="/seller">Seller portal</router-link> · <router-link to="/orders?tab=sales">Sales orders</router-link></span>
            </template>
            <span v-else class="muted">Not enabled</span>
          </div>
        </div>
      </div>

      <div v-if="company" class="card">
        <div class="card-header">
          <h3 class="card-title"><Icon name="user" :size="16" /> Team</h3>
          <button v-if="session.can('orgAdmin')" class="btn-primary btn-sm" @click="showAdd = true"><Icon name="plus" :size="14" /> Add colleague</button>
        </div>
        <div class="table-wrap">
          <table>
            <thead><tr><th>Name</th><th>Email</th><th>Roles</th><th>Last sign-in</th><th>Status</th></tr></thead>
            <tbody>
              <tr v-for="u in (company.users as OrgUser[])" :key="u.id">
                <td class="cell-strong">{{ u.displayName }}</td>
                <td class="cell-muted">{{ u.email }}</td>
                <td><div class="chips"><span v-for="r in u.roles" :key="r" class="tag">{{ r }}</span></div></td>
                <td class="cell-muted">{{ fmt.ago(u.lastLoginUtc) }}</td>
                <td><span class="badge" :class="u.lockoutEndUtc && new Date(u.lockoutEndUtc + 'Z') > new Date() ? 'warn' : u.status === 'Active' ? 'good' : ''"><span class="dot" />
                  {{ u.lockoutEndUtc && new Date(u.lockoutEndUtc + 'Z') > new Date() ? 'Locked' : u.status }}</span></td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>
      <div v-if="!session.organization" class="alert info"><Icon name="info" :size="16" />You are platform staff and not a member of a trading company.</div>
    </div>

    <div class="stack">
      <div class="card card-pad stack" style="gap: .5rem">
        <div class="row"><span class="avatar" style="width: 44px; height: 44px">{{ session.initials }}</span>
          <div class="cell-title"><b>{{ session.user?.displayName }}</b><small>{{ session.user?.email }}</small></div></div>
        <div class="chips"><span v-for="r in session.user?.roles" :key="r" class="tag">{{ r }}</span></div>
        <div class="muted" style="font-size: .78rem">Signed in as <code>{{ session.user?.username }}</code> · session renews automatically</div>
      </div>
      <form class="card" @submit.prevent="changePassword">
        <div class="card-header"><h3 class="card-title"><Icon name="shield" :size="16" /> Change password</h3></div>
        <div class="card-body stack" style="gap: .8rem">
          <div class="field" :class="{ invalid: pwErrors.currentPassword }"><label for="cp">Current password</label>
            <input id="cp" v-model="pw.currentPassword" type="password" autocomplete="current-password" required />
            <span v-if="pwErrors.currentPassword" class="field-error">{{ pwErrors.currentPassword[0] }}</span></div>
          <div class="field" :class="{ invalid: pwErrors.newPassword }"><label for="np">New password</label>
            <input id="np" v-model="pw.newPassword" type="password" autocomplete="new-password" required />
            <span v-if="pwErrors.newPassword" class="field-error">{{ pwErrors.newPassword.join(' ') }}</span>
            <span v-else class="hint">10+ characters with upper, lower case and a digit.</span></div>
          <div class="field" :class="{ invalid: pwErrors.confirm }"><label for="cf">Confirm</label>
            <input id="cf" v-model="pw.confirm" type="password" autocomplete="new-password" required />
            <span v-if="pwErrors.confirm" class="field-error">{{ pwErrors.confirm[0] }}</span></div>
          <button type="submit" class="btn-primary" :disabled="busy">Update password</button>
        </div>
      </form>
    </div>
  </div>

  <Overlay v-if="showAdd" title="Add a colleague" subtitle="They sign in with this email and the password you set (ask them to change it)." @close="showAdd = false">
    <form id="au" class="stack" @submit.prevent="addUser">
      <div class="form-grid">
        <div class="field"><label for="ad">Name</label><input id="ad" v-model="newUser.displayName" required /></div>
        <div class="field"><label for="ae">Email</label><input id="ae" v-model="newUser.email" type="email" required /></div>
      </div>
      <div class="field"><label for="ap">Temporary password</label><input id="ap" v-model="newUser.password" type="text" required /></div>
      <div><label>Roles</label><div class="chips">
        <span v-for="r in assignable()" :key="r" class="chip" :class="{ active: newUser.roles.includes(r) }" @click="toggleRole(r)">{{ r }}</span>
      </div></div>
    </form>
    <template #footer>
      <button class="btn-secondary" @click="showAdd = false">Cancel</button>
      <button class="btn-primary" type="submit" form="au"><Icon name="plus" :size="15" /> Add</button>
    </template>
  </Overlay>
</template>
