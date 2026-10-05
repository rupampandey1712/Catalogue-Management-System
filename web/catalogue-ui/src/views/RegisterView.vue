<script setup lang="ts">
import { computed, ref } from 'vue'
import Icon from '../components/Icon.vue'
import { api, errorText } from '../api'

const form = ref({
  companyName: '', vatNumber: '', country: 'GB', wantsToBuy: true, wantsToSell: false,
  displayName: '', email: '', password: '', confirm: ''
})
const show = ref(false)
const busy = ref(false)
const done = ref<string | null>(null)
const serverErrors = ref<Record<string, string[]>>({})
const touched = ref(false)

// Mirrors the server's PasswordPolicy so feedback is instant; the server re-checks.
const checks = computed(() => {
  const p = form.value.password
  const local = form.value.email.split('@')[0]
  return [
    { ok: p.length >= 10, text: 'At least 10 characters' },
    { ok: /[A-Z]/.test(p), text: 'An upper-case letter' },
    { ok: /[a-z]/.test(p), text: 'A lower-case letter' },
    { ok: /\d/.test(p), text: 'A digit' },
    { ok: !(local.length >= 3 && p.toLowerCase().includes(local.toLowerCase())), text: 'Not your email name' }
  ]
})
const strength = computed(() => checks.value.filter(c => c.ok).length)
const errors = computed(() => {
  const e: Record<string, string> = {}
  if (form.value.companyName.trim().length < 2) e.companyName = 'Company name is required.'
  if (!form.value.wantsToBuy && !form.value.wantsToSell) e.capabilities = 'Choose buying, selling or both.'
  if (!form.value.displayName.trim()) e.displayName = 'Your name is required.'
  if (!/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(form.value.email)) e.email = 'Enter a valid email address.'
  if (strength.value < checks.value.length) e.password = 'The password does not meet the policy yet.'
  if (form.value.confirm !== form.value.password) e.confirm = 'Passwords do not match.'
  for (const [k, v] of Object.entries(serverErrors.value)) e[k] = v.join(' ')
  return e
})

async function submit() {
  touched.value = true
  serverErrors.value = {}
  if (Object.keys(errors.value).length) return
  busy.value = true
  try {
    const { confirm: _, ...body } = form.value
    const { data } = await api.post('/auth/register', body)
    done.value = data.message
  } catch (e: any) {
    if (e.response?.data?.errors) serverErrors.value = e.response.data.errors
    else serverErrors.value = { form: [errorText(e, 'Registration failed.')] }
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="login-screen">
    <section class="login-art">
      <div class="row" style="font-weight: 700; font-size: 1.05rem"><span class="brand-mark"><Icon name="box" :size="18" /></span> Catalogue</div>
      <div>
        <h1>Open a business account.</h1>
        <p>One account lets your company buy, sell, or both. We verify every company before it can trade — usually within one working day.</p>
        <div class="login-features">
          <div class="login-feature"><i><Icon name="shield" :size="16" /></i> Company verification (VAT number) before trading</div>
          <div class="login-feature"><i><Icon name="user" :size="16" /></i> You become the company admin and can invite colleagues</div>
          <div class="login-feature"><i><Icon name="tag" :size="16" /></i> Buyers get trade pricing; sellers set their own prices & discounts</div>
        </div>
      </div>
      <div style="color: #8f95c9; font-size: .8rem">Passwords are hashed with PBKDF2 · sessions use rotating httpOnly refresh tokens</div>
    </section>

    <section class="login-panel">
      <div class="login-card" style="max-width: 460px">
        <template v-if="done">
          <span class="stat-icon good" style="width: 48px; height: 48px"><Icon name="checkCircle" :size="24" /></span>
          <h2 class="mt-2" style="font-size: 1.5rem">Registration received</h2>
          <p class="page-subtitle mt-1">{{ done }}</p>
          <router-link to="/login" class="btn btn-primary mt-3" style="width: 100%">Back to sign in</router-link>
        </template>
        <template v-else>
          <h2 style="font-size: 1.5rem">Register your company</h2>
          <p class="page-subtitle" style="margin: .3rem 0 1.25rem">Already registered? <router-link to="/login">Sign in</router-link></p>
          <form class="stack" style="gap: .9rem" @submit.prevent="submit">
            <div class="field" :class="{ invalid: touched && errors.companyName }">
              <label for="cn">Company name</label>
              <input id="cn" v-model="form.companyName" autocomplete="organization" placeholder="e.g. Solent Heating Co" />
              <span v-if="touched && errors.companyName" class="field-error">{{ errors.companyName }}</span>
            </div>
            <div class="form-grid" style="grid-template-columns: 2fr 1fr">
              <div class="field"><label for="vat">VAT number</label><input id="vat" v-model="form.vatNumber" placeholder="GB123456789" /></div>
              <div class="field"><label for="ct">Country</label>
                <select id="ct" v-model="form.country"><option>GB</option><option>IE</option><option>FR</option><option>DE</option><option>NL</option></select></div>
            </div>
            <div>
              <label>What will your company do?</label>
              <div class="grid grid-2" style="gap: .5rem">
                <button type="button" class="role-option" :class="{ active: form.wantsToBuy }" @click="form.wantsToBuy = !form.wantsToBuy">
                  <span class="row" style="gap: .4rem"><Icon :name="form.wantsToBuy ? 'checkCircle' : 'tag'" :size="14" />Buy</span><small>Source products at trade prices</small>
                </button>
                <button type="button" class="role-option" :class="{ active: form.wantsToSell }" @click="form.wantsToSell = !form.wantsToSell">
                  <span class="row" style="gap: .4rem"><Icon :name="form.wantsToSell ? 'checkCircle' : 'truck'" :size="14" />Sell</span><small>List your range for other companies</small>
                </button>
              </div>
              <span v-if="touched && errors.capabilities" class="field-error">{{ errors.capabilities }}</span>
            </div>
            <div class="field" :class="{ invalid: touched && errors.displayName }">
              <label for="dn">Your name</label><input id="dn" v-model="form.displayName" autocomplete="name" />
              <span v-if="touched && errors.displayName" class="field-error">{{ errors.displayName }}</span>
            </div>
            <div class="field" :class="{ invalid: touched && errors.email }">
              <label for="em">Work email</label><input id="em" v-model="form.email" type="email" autocomplete="email" />
              <span v-if="touched && errors.email" class="field-error">{{ errors.email }}</span>
            </div>
            <div class="field" :class="{ invalid: touched && errors.password }">
              <label for="pw">Password</label>
              <div style="position: relative">
                <input id="pw" v-model="form.password" :type="show ? 'text' : 'password'" autocomplete="new-password" style="width: 100%; padding-right: 2.5rem" />
                <button type="button" class="btn-ghost btn-sm" style="position: absolute; right: .25rem; top: 50%; transform: translateY(-50%)" aria-label="Show password" @click="show = !show"><Icon name="eye" :size="15" /></button>
              </div>
              <div class="progress sm mt-1" :class="strength >= 5 ? 'good' : strength >= 3 ? 'warn' : 'bad'"><span :style="{ width: strength * 20 + '%' }" /></div>
              <div class="chips mt-1">
                <span v-for="c in checks" :key="c.text" class="tag" :style="c.ok ? 'color: var(--good)' : ''"><Icon :name="c.ok ? 'check' : 'x'" :size="11" />&nbsp;{{ c.text }}</span>
              </div>
              <span v-if="touched && serverErrors.password" class="field-error">{{ serverErrors.password.join(' ') }}</span>
            </div>
            <div class="field" :class="{ invalid: touched && errors.confirm }">
              <label for="pc">Confirm password</label><input id="pc" v-model="form.confirm" type="password" autocomplete="new-password" />
              <span v-if="touched && errors.confirm" class="field-error">{{ errors.confirm }}</span>
            </div>
            <div v-if="serverErrors.form" class="alert bad"><Icon name="alertCircle" :size="16" />{{ serverErrors.form.join(' ') }}</div>
            <button type="submit" class="btn-primary btn-lg" :disabled="busy"><Icon :name="busy ? 'refresh' : 'check'" :size="16" :class="{ spin: busy }" /> Create business account</button>
          </form>
        </template>
      </div>
    </section>
  </div>
</template>
