<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import Icon from '../components/Icon.vue'
import Pager from '../components/Pager.vue'
import EmptyState from '../components/EmptyState.vue'
import StatTile from '../components/StatTile.vue'
import { api, type Payment, type PaymentMethod } from '../api'
import { session } from '../session'
import { fmt } from '../ui'

interface Totals { currency: string; amount: number; count: number }

const canBuy = session.can('buyer') && !!session.organization?.canBuy
const canSell = session.can('seller')
const staff = session.can('platformAdmin')
const role = ref<'buyer' | 'seller' | 'all'>(canBuy ? 'buyer' : canSell ? 'seller' : 'all')
const status = ref('')
const items = ref<Payment[]>([])
const total = ref(0)
const collected = ref<Totals[]>([])
const refunded = ref<Totals[]>([])
const methods = ref<PaymentMethod[]>([])
const page = ref(1)
const pageSize = ref(25)
const loading = ref(true)

async function load() {
  loading.value = true
  try {
    const { data } = await api.get('/payments/', { params: { role: role.value, status: status.value || undefined, page: page.value, pageSize: pageSize.value } })
    items.value = data.items
    total.value = data.total
    collected.value = data.collected
    refunded.value = data.refunded
  } finally { loading.value = false }
}
watch([role, status], () => { page.value = 1; load() })
watch([page, pageSize], load)
onMounted(async () => {
  load()
  methods.value = (await api.get<PaymentMethod[]>('/payments/methods')).data
})

const sum = (t: Totals[]) => t.length ? t.map(x => fmt.money(x.amount, x.currency)).join(' · ') : fmt.money(0, 'GBP')
const count = (t: Totals[]) => t.reduce((n, x) => n + x.count, 0)
const tone = (s: string) => ({ Succeeded: 'good', Pending: 'warn', Failed: 'bad', Refunded: 'info', Cancelled: '' } as Record<string, string>)[s] ?? ''
</script>

<template>
  <div class="page-header">
    <div class="page-title-group">
      <h1 class="page-title">Payments</h1>
      <span class="page-subtitle">Online payments for purchase orders. Orders that are not paid online settle on the buyer's trade account.</span>
    </div>
    <router-link to="/orders" class="btn btn-secondary"><Icon name="inbox" :size="15" /> Orders</router-link>
  </div>

  <div class="stat-grid">
    <StatTile :label="role === 'seller' ? 'Received' : role === 'buyer' ? 'Paid' : 'Collected'" icon="checkCircle" tone="good" :value="sum(collected)" :loading="loading" :foot="`${count(collected)} payment(s)`" />
    <StatTile label="Refunded" icon="refresh" tone="info" :value="sum(refunded)" :loading="loading" :foot="`${count(refunded)} refund(s)`" />
    <StatTile label="Payment methods" icon="card" :value="String(methods.length)" :loading="loading"
      :foot="methods.map(m => m.name).join(' · ') || 'None enabled: on account only'" />
  </div>

  <div v-if="methods.some(m => m.testMode)" class="alert warn" style="margin-bottom: 1rem">
    <Icon name="alert" :size="16" /><span>Test mode: payments marked <b>test</b> moved no real money (simulated provider or Stripe test keys).</span>
  </div>

  <div class="card">
    <div class="toolbar">
      <div class="segmented">
        <button v-if="canBuy" :class="{ active: role === 'buyer' }" @click="role = 'buyer'"><Icon name="download" :size="13" /> Paid by us</button>
        <button v-if="canSell || staff" :class="{ active: role === 'seller' }" @click="role = 'seller'"><Icon name="upload" :size="13" /> Received</button>
        <button v-if="staff" :class="{ active: role === 'all' }" @click="role = 'all'"><Icon name="grid" :size="13" /> All</button>
      </div>
      <select v-model="status" aria-label="Status">
        <option value="">Any status</option><option>Succeeded</option><option>Pending</option><option>Failed</option><option>Cancelled</option><option>Refunded</option>
      </select>
      <span class="spacer" />
      <button class="btn-ghost btn-sm" aria-label="Refresh" @click="load"><Icon name="refresh" :size="14" :class="{ spin: loading }" /></button>
    </div>
    <div class="table-wrap">
      <table>
        <thead><tr><th>Payment</th><th>Order</th><th>{{ role === 'seller' ? 'Buyer' : 'Seller' }}</th><th>Method</th><th class="num">Amount</th><th>Status</th><th>When</th></tr></thead>
        <tbody>
          <tr v-for="p in items" :key="p.id">
            <td><div class="cell-title"><span>#{{ p.id }}</span><small v-if="p.paymentReference"><code>{{ p.paymentReference }}</code></small></div></td>
            <td><router-link :to="{ path: '/orders', query: role === 'seller' ? { tab: 'sales' } : {} }"><code>{{ p.orderNumber }}</code></router-link></td>
            <td>{{ role === 'seller' ? p.buyerName : `${p.supplierCode} · ${p.supplierName}` }}</td>
            <td>{{ p.provider }} <span v-if="p.isTest" class="badge warn" style="font-size: .66rem">test</span></td>
            <td class="num cell-strong">{{ fmt.money(p.amount, p.currency) }}</td>
            <td><span class="badge" :class="tone(p.status)">{{ p.status }}</span>
              <div v-if="p.failureReason" class="muted" style="font-size: .74rem">{{ p.failureReason }}</div></td>
            <td class="cell-muted">{{ fmt.ago(p.refundedAtUtc ?? p.completedAtUtc ?? p.createdAtUtc) }}</td>
          </tr>
          <tr v-if="!loading && !items.length"><td colspan="7"><EmptyState icon="card" title="No payments yet" /></td></tr>
        </tbody>
      </table>
    </div>
    <Pager v-model:page="page" v-model:page-size="pageSize" :total="total" noun="payments" />
  </div>
</template>
