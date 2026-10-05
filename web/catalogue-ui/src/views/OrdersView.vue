<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import Icon from '../components/Icon.vue'
import Pager from '../components/Pager.vue'
import EmptyState from '../components/EmptyState.vue'
import Overlay from '../components/Overlay.vue'
import { api, errorText, type OrderSummary, type Paged, type Payment, type PaymentMethod, type StartPaymentResponse } from '../api'
import { session } from '../session'
import { fmt, toast } from '../ui'

const route = useRoute()
const router = useRouter()
const canBuy = session.can('buyer') && !!session.organization?.canBuy
const canSell = session.can('seller')
const tab = ref<'purchases' | 'sales'>(route.query.tab === 'sales' || !canBuy ? 'sales' : 'purchases')
const status = ref('')
const items = ref<OrderSummary[]>([])
const total = ref(0)
const page = ref(1)
const pageSize = ref(25)
const loading = ref(true)
const open = ref<any>(null)
const note = ref('')

async function load() {
  loading.value = true
  try {
    const { data } = await api.get<Paged<OrderSummary>>('/marketplace/orders', {
      params: { role: tab.value === 'sales' ? 'seller' : 'buyer', status: status.value || undefined, page: page.value, pageSize: pageSize.value }
    })
    items.value = data.items
    total.value = data.total
  } finally { loading.value = false }
}
watch([tab, status], () => { page.value = 1; load() })
watch([page, pageSize], load)
onMounted(async () => {
  load()
  if (canBuy) methods.value = (await api.get<PaymentMethod[]>('/payments/methods')).data
  if (route.query.payment) await returnedFromCheckout(Number(route.query.payment), String(route.query.result ?? ''))
})

// ----- payments -----
const methods = ref<PaymentMethod[]>([])
const method = ref<string>('')
const paying = ref(false)
const simulated = ref<{ payment: StartPaymentResponse; order: any } | null>(null)
const payTone = (s: string) => ({ Paid: 'good', Pending: 'warn', Refunded: 'info', Unpaid: '' } as Record<string, string>)[s] ?? ''
const attemptTone = (s: string) => ({ Succeeded: 'good', Pending: 'warn', Failed: 'bad', Refunded: 'info', Cancelled: '' } as Record<string, string>)[s] ?? ''

async function pay() {
  const provider = method.value || methods.value[0]?.provider
  if (!provider || !open.value) return
  paying.value = true
  try {
    const { data } = await api.post<StartPaymentResponse>(`/marketplace/orders/${open.value.id}/pay`, { provider })
    if (data.redirectUrl) { window.location.assign(data.redirectUrl); return } // hosted checkout (Stripe)
    simulated.value = { payment: data, order: open.value }
  } catch (e: any) { toast('bad', 'Payment not started', e.response?.data?.detail ?? errorText(e)) }
  finally { paying.value = false }
}

async function simulate(outcome: 'approve' | 'decline' | 'cancel') {
  if (!simulated.value) return
  const { payment, order } = simulated.value
  try {
    const { data } = await api.post<Payment>(`/payments/${payment.paymentId}/simulate`, { outcome })
    simulated.value = null
    if (data.status === 'Succeeded') toast('good', `Order ${order.number} paid`, `${fmt.money(data.amount, data.currency)} · test payment, no money moved`)
    else toast(outcome === 'decline' ? 'bad' : 'info', outcome === 'decline' ? 'Payment declined' : 'Payment cancelled', data.failureReason)
    await refreshOpen(order.id)
  } catch (e: any) {
    simulated.value = null
    toast('bad', 'Payment failed', e.response?.data?.detail ?? e.response?.data?.error ?? errorText(e))
  }
}

// Back from a hosted checkout: ask the server to check with the provider (works without webhooks), polling briefly
// because card payments can take a few seconds to settle.
async function returnedFromCheckout(id: number, result: string) {
  router.replace({ path: '/orders', query: {} })
  try {
    let p: Payment | null = null
    for (let i = 0; i < 6; i++) {
      p = (await api.get<Payment>(`/payments/${id}`, { params: { sync: true } })).data
      if (p.status !== 'Pending' || result === 'cancelled') break
      await new Promise(r => setTimeout(r, 2000))
    }
    if (!p) return
    if (p.status === 'Succeeded') toast('good', `Order ${p.orderNumber} paid`, `${fmt.money(p.amount, p.currency)}${p.isTest ? ' · test mode' : ''}`)
    else if (result === 'cancelled') toast('info', 'Checkout cancelled', `Order ${p.orderNumber} is still unpaid. You can pay later or settle on account.`)
    else if (p.status === 'Pending') toast('info', 'Payment processing', 'The provider has not confirmed yet. Reopen the order in a moment.')
    else toast('bad', `Payment ${p.status.toLowerCase()}`, p.failureReason)
    await refreshOpen(p.orderId)
  } catch (e) { toast('bad', 'Could not check the payment', errorText(e)) }
}

async function refreshOpen(id: number) {
  open.value = (await api.get(`/marketplace/orders/${id}`)).data
  load()
}

async function show(o: OrderSummary) {
  open.value = (await api.get(`/marketplace/orders/${o.id}`)).data
  note.value = ''
}
async function act(next: string) {
  try {
    await api.post(`/marketplace/orders/${open.value.id}/status`, { status: next, note: note.value || null })
    toast('good', `Order ${open.value.number} ${next.toLowerCase()}`)
    open.value = (await api.get(`/marketplace/orders/${open.value.id}`)).data
    load()
  } catch (e: any) { toast('bad', 'Could not update order', e.response?.data?.error ?? errorText(e)) }
}

const tone = (s: string) => ({ Submitted: 'info', Accepted: 'primary', Shipped: 'good', Rejected: 'bad', Cancelled: '' } as Record<string, string>)[s] ?? ''
const steps = ['Submitted', 'Accepted', 'Shipped']
const stepIndex = computed(() => steps.indexOf(open.value?.status))
</script>

<template>
  <div class="page-header">
    <div class="page-title-group">
      <h1 class="page-title">Orders</h1>
      <span class="page-subtitle">Purchase orders between companies. Sellers accept, reject and ship; buyers can cancel until shipment.</span>
    </div>
    <router-link v-if="canBuy" to="/basket" class="btn btn-primary"><Icon name="tag" :size="15" /> Basket</router-link>
  </div>

  <div class="card">
    <div class="toolbar">
      <div class="segmented">
        <button v-if="canBuy" :class="{ active: tab === 'purchases' }" @click="tab = 'purchases'"><Icon name="download" :size="13" /> Purchases</button>
        <button v-if="canSell" :class="{ active: tab === 'sales' }" @click="tab = 'sales'"><Icon name="upload" :size="13" /> Sales</button>
      </div>
      <select v-model="status" aria-label="Status">
        <option value="">Any status</option><option>Submitted</option><option>Accepted</option><option>Shipped</option><option>Rejected</option><option>Cancelled</option>
      </select>
      <span class="spacer" />
      <button class="btn-ghost btn-sm" aria-label="Refresh" @click="load"><Icon name="refresh" :size="14" :class="{ spin: loading }" /></button>
    </div>
    <div class="table-wrap">
      <table>
        <thead><tr><th>Order</th><th>{{ tab === 'sales' ? 'Buyer' : 'Seller' }}</th><th class="num">Lines</th><th class="num">Value</th><th class="num">Saved vs list</th><th>Status</th><th>Payment</th><th>Placed</th></tr></thead>
        <tbody>
          <tr v-for="o in items" :key="o.id" class="clickable" @click="show(o)">
            <td><div class="cell-title"><code>{{ o.number }}</code><small v-if="o.buyerReference">Ref {{ o.buyerReference }}</small></div></td>
            <td>{{ tab === 'sales' ? o.buyerName : `${o.supplierCode} · ${o.supplierName}` }}</td>
            <td class="num">{{ o.lines }}</td>
            <td class="num cell-strong">{{ fmt.money(o.subtotal, o.currency) }}</td>
            <td class="num" style="color: var(--good)">{{ o.listValue > o.subtotal ? fmt.money(o.listValue - o.subtotal, o.currency) : '—' }}</td>
            <td><span class="badge" :class="tone(o.status)"><span class="dot" />{{ o.status }}</span></td>
            <td><span class="badge" :class="payTone(o.paymentStatus)">{{ o.paymentStatus === 'Unpaid' ? 'On account' : o.paymentStatus }}</span></td>
            <td class="cell-muted">{{ fmt.ago(o.createdAtUtc) }}</td>
          </tr>
          <tr v-if="!loading && !items.length"><td colspan="8"><EmptyState icon="inbox" :title="tab === 'sales' ? 'No sales orders yet' : 'No purchases yet'" /></td></tr>
        </tbody>
      </table>
    </div>
    <Pager v-model:page="page" v-model:page-size="pageSize" :total="total" noun="orders" />
  </div>

  <Overlay v-if="open" kind="drawer" :title="open.number" @close="open = null">
    <template #subtitle><span class="badge" :class="tone(open.status)"><span class="dot" />{{ open.status }}</span> · {{ open.buyer.name }} → {{ open.supplier.code }}</template>
    <div v-if="stepIndex >= 0" class="row" style="gap: 0">
      <template v-for="(s, i) in steps" :key="s">
        <span class="badge" :class="i <= stepIndex ? 'good' : ''"><Icon :name="i <= stepIndex ? 'check' : 'clock'" :size="11" /> {{ s }}</span>
        <div v-if="i < steps.length - 1" style="flex: 1; height: 2px; background: var(--border); margin: 0 .4rem" />
      </template>
    </div>
    <dl class="kv">
      <dt>Buyer</dt><dd>{{ open.buyer.name }}</dd>
      <dt>Seller</dt><dd>{{ open.supplier.code }} · {{ open.supplier.name }}</dd>
      <dt>Reference</dt><dd>{{ open.buyerReference ?? '—' }}</dd>
      <dt>Note</dt><dd>{{ open.note ?? '—' }}</dd>
      <dt>Placed</dt><dd>{{ fmt.date(open.createdAtUtc) }} by {{ open.createdBy }}</dd>
      <dt v-if="open.statusNote">Last update</dt><dd v-if="open.statusNote">{{ open.statusNote }}</dd>
    </dl>
    <table>
      <thead><tr><th>Item</th><th class="num">Qty</th><th class="num">Unit</th><th class="num">Total</th></tr></thead>
      <tbody>
        <tr v-for="l in open.lines" :key="l.id">
          <td><div class="cell-title"><span>{{ l.description }}</span><small><code>{{ l.supplierSku }}</code> · {{ l.priceExplanation }}</small></div></td>
          <td class="num">{{ l.quantity }}</td>
          <td class="num">{{ fmt.money(l.unitPrice, open.currency) }}</td>
          <td class="num cell-strong">{{ fmt.money(l.lineTotal, open.currency) }}</td>
        </tr>
      </tbody>
    </table>
    <div class="row"><span class="muted">Order total</span><span class="spacer" /><b style="font-size: 1.15rem">{{ fmt.money(open.subtotal, open.currency) }}</b></div>

    <div class="card" style="box-shadow: none">
      <div class="card-header">
        <h3 class="card-title"><Icon name="card" :size="16" /> Payment</h3>
        <span class="badge" :class="payTone(open.paymentStatus)">{{ open.paymentStatus === 'Unpaid' ? 'Unpaid · on account' : open.paymentStatus }}</span>
      </div>
      <div class="card-body stack" style="gap: .7rem">
        <p v-if="open.paymentStatus === 'Paid'" class="muted" style="margin: 0; font-size: .85rem">Paid {{ fmt.date(open.paidAtUtc) }}. If the order is rejected or cancelled, the payment is refunded automatically.</p>
        <p v-else-if="open.paymentStatus === 'Refunded'" class="muted" style="margin: 0; font-size: .85rem">The payment was refunded because the order was closed.</p>
        <template v-else-if="open.canPay">
          <p class="muted" style="margin: 0; font-size: .85rem">By default this order is settled on your trade account. You can also pay it now.</p>
          <div v-if="methods.length" class="stack" style="gap: .4rem">
            <label v-for="m in methods" :key="m.provider" class="row" style="gap: .5rem; margin: 0; cursor: pointer">
              <input type="radio" name="pm" :value="m.provider" :checked="(method || methods[0].provider) === m.provider" @change="method = m.provider" />
              <span>{{ m.name }}</span><span v-if="m.testMode" class="badge warn" style="font-size: .66rem">TEST · no real money</span>
            </label>
            <button class="btn-primary" :disabled="paying" @click="pay"><Icon :name="paying ? 'refresh' : 'card'" :size="15" :class="{ spin: paying }" /> Pay {{ fmt.money(open.subtotal, open.currency) }}</button>
          </div>
          <div v-else class="alert info"><Icon name="info" :size="16" /><span>Online payment is not enabled on this server. Orders settle on account.</span></div>
        </template>
        <p v-else-if="open.paymentStatus === 'Unpaid'" class="muted" style="margin: 0; font-size: .85rem">Settled on the buyer's trade account.</p>
        <div v-if="open.payments?.length" class="list" style="border: 1px solid var(--border); border-radius: var(--radius)">
          <div v-for="p in open.payments" :key="p.id" class="list-item">
            <span class="list-icon"><Icon name="card" :size="14" /></span>
            <div class="list-main">
              <div class="list-title">{{ fmt.money(p.amount, p.currency) }} · {{ p.provider }}<span v-if="p.isTest" class="muted"> (test)</span></div>
              <div class="list-sub">{{ fmt.ago(p.createdAtUtc) }} by {{ p.createdBy }}<span v-if="p.failureReason"> · {{ p.failureReason }}</span><span v-if="p.refundReference"> · refund <code>{{ p.refundReference }}</code></span></div>
            </div>
            <span class="badge" :class="attemptTone(p.status)">{{ p.status }}</span>
          </div>
        </div>
      </div>
    </div>
    <template v-if="['Submitted', 'Accepted'].includes(open.status)">
      <div class="field"><label for="on">Note to the other party (optional)</label><input id="on" v-model="note" maxlength="500" /></div>
      <div class="row wrap">
        <template v-if="open.canAct.seller">
          <button v-if="open.status === 'Submitted'" class="btn-primary" @click="act('Accepted')"><Icon name="check" :size="15" /> Accept</button>
          <button v-if="open.status === 'Submitted'" class="btn-danger" @click="act('Rejected')">Reject</button>
          <button v-if="open.status === 'Accepted'" class="btn-primary" @click="act('Shipped')"><Icon name="truck" :size="15" /> Mark shipped</button>
        </template>
        <button v-if="open.canAct.buyer" class="btn-ghost" @click="act('Cancelled')">Cancel order</button>
      </div>
    </template>
  </Overlay>

  <Overlay v-if="simulated" title="Test checkout" @close="simulate('cancel')">
    <template #subtitle>Simulated payment provider</template>
    <div class="stack">
      <div class="alert warn"><Icon name="alert" :size="16" /><span><b>No money moves.</b> This checkout only records the outcome in the database so you can test the order and refund flow. It never asks for card details.</span></div>
      <dl class="kv">
        <dt>Order</dt><dd><code>{{ simulated.order.number }}</code></dd>
        <dt>Pay to</dt><dd>{{ simulated.order.supplier.name }}</dd>
        <dt>Amount</dt><dd><b style="font-size: 1.2rem">{{ fmt.money(simulated.payment.amount, simulated.payment.currency) }}</b></dd>
        <dt>Payment</dt><dd>#{{ simulated.payment.paymentId }}</dd>
      </dl>
    </div>
    <template #footer>
      <button class="btn-ghost" @click="simulate('cancel')">Cancel</button>
      <button class="btn-danger" @click="simulate('decline')"><Icon name="x" :size="15" /> Decline</button>
      <button class="btn-primary" @click="simulate('approve')"><Icon name="check" :size="15" /> Approve payment</button>
    </template>
  </Overlay>
</template>
