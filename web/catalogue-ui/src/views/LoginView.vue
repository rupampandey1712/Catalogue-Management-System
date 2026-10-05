<script setup lang="ts">
import { ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import Icon from '../components/Icon.vue'
import { api, type SessionResponse } from '../api'
import { session } from '../session'

const router = useRouter()
const route = useRoute()
const username = ref('')
const password = ref('')
const showPassword = ref(false)
const error = ref<{ title: string; detail?: string; tone: 'bad' | 'warn' } | null>(
  route.query.expired ? { title: 'Your session expired. Please sign in again.', tone: 'warn' } : null)
const busy = ref(false)

const demoUsers = [
  { username: 'admin', password: 'admin123', role: 'Platform admin', note: 'Catalogue Direct · buys & sells' },
  { username: 'buyer', password: 'buyer123', role: 'Buyer', note: 'BriteHeat Installers' },
  { username: 'seller', password: 'seller123', role: 'Seller + buyer', note: 'Northwest Distribution' },
  { username: 'hvx', password: 'hvx12345', role: 'Seller', note: 'HVAC Express' },
  { username: 'support', password: 'support123', role: 'Support', note: 'Imports & reprocess' },
  { username: 'pennine', password: 'pennine123', role: 'Pending company', note: 'Awaiting verification' }
]

function pick(u: typeof demoUsers[number]) {
  username.value = u.username
  password.value = u.password
}

async function submit() {
  error.value = null
  busy.value = true
  try {
    const { data } = await api.post<SessionResponse>('/auth/login', { username: username.value, password: password.value })
    session.set(data.token, data.expiresAtUtc, data.user)
    router.push((route.query.next as string) || (session.can('readCatalogue') ? '/dashboard' : '/account'))
  } catch (e: any) {
    const status = e.response?.status
    const d = e.response?.data
    error.value = !e.response ? { title: 'Cannot reach the server. Is the API running?', tone: 'bad' }
      : status === 429 ? { title: 'Too many sign-in attempts from this network.', detail: 'Wait a minute and try again.', tone: 'warn' }
      : status === 423 ? { title: d?.title ?? 'Account locked', detail: d?.detail, tone: 'warn' }
      : status === 403 ? { title: d?.title ?? 'Access denied', detail: d?.detail, tone: 'warn' }
      : { title: d?.title ?? 'Invalid email/username or password.', tone: 'bad' }
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="login-screen">
    <section class="login-art">
      <div class="row" style="font-weight: 700; font-size: 1.05rem">
        <span class="brand-mark"><Icon name="box" :size="18" /></span> Catalogue
      </div>
      <div>
        <h1>The B2B marketplace for HVAC & plumbing.</h1>
        <p>Sell your range to installers and merchants, and buy from whichever supplier is cheapest after your negotiated discounts — all from one catalogue.</p>
        <div class="login-features">
          <div class="login-feature"><i><Icon name="truck" :size="16" /></i> Sell: list products, set prices, manage buyer discounts</div>
          <div class="login-feature"><i><Icon name="tag" :size="16" /></i> Buy: compare every seller's net price and order in one basket</div>
          <div class="login-feature"><i><Icon name="layers" :size="16" /></i> Specs, images & datasheets for every product</div>
        </div>
      </div>
      <div style="color: #8f95c9; font-size: .8rem">Learning build · ASP.NET Core 10 · Vue 3 · Azure emulators</div>
    </section>

    <section class="login-panel">
      <div class="login-card">
        <h2 style="font-size: 1.6rem">Welcome back</h2>
        <p class="page-subtitle" style="margin: .35rem 0 1.5rem">Sign in with your email or username.</p>

        <form @submit.prevent="submit">
          <div class="field">
            <label for="u">Email or username</label>
            <div class="input-icon"><span class="icon"><Icon name="user" :size="15" /></span><input id="u" v-model="username" autocomplete="username" required /></div>
          </div>
          <div class="field mt-2">
            <label for="p">Password</label>
            <div class="input-icon" style="position: relative">
              <span class="icon"><Icon name="shield" :size="15" /></span>
              <input id="p" v-model="password" :type="showPassword ? 'text' : 'password'" autocomplete="current-password" required style="padding-right: 2.5rem" />
              <button type="button" class="btn-ghost btn-sm" style="position: absolute; right: .25rem; top: 50%; transform: translateY(-50%)"
                :aria-label="showPassword ? 'Hide password' : 'Show password'" @click="showPassword = !showPassword"><Icon name="eye" :size="15" /></button>
            </div>
          </div>
          <div v-if="error" class="alert mt-2" :class="error.tone"><Icon name="alertCircle" :size="16" /><span><b>{{ error.title }}</b><template v-if="error.detail"><br />{{ error.detail }}</template></span></div>
          <button type="submit" class="btn-primary btn-lg" style="width: 100%; margin-top: 1.25rem" :disabled="busy">
            <Icon v-if="busy" name="refresh" :size="16" class="spin" /> {{ busy ? 'Signing in…' : 'Sign in' }}
          </button>
        </form>
        <p class="mt-2" style="text-align: center; font-size: .88rem">
          New to the platform? <router-link to="/register"><b>Register your company</b></router-link>
        </p>

        <div class="mt-3">
          <div class="card-sub" style="margin-bottom: .5rem">Demo accounts (seeded, development only) — click to fill</div>
          <div class="role-grid">
            <button v-for="u in demoUsers" :key="u.username" type="button" class="role-option" :class="{ active: username === u.username }" @click="pick(u)">
              <span>{{ u.role }}</span><small>{{ u.username }} · {{ u.note }}</small>
            </button>
          </div>
        </div>
      </div>
    </section>
  </div>
</template>
