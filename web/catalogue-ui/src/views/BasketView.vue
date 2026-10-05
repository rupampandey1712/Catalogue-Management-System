<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import Icon from '../components/Icon.vue'
import EmptyState from '../components/EmptyState.vue'
import AvailabilityBadge from '../components/AvailabilityBadge.vue'
import { api, errorText, type BasketQuote } from '../api'
import { basket } from '../basket'
import { session } from '../session'
import { fmt, toast } from '../ui'

const router = useRouter()
const quote = ref<BasketQuote | null>(null)
const loading = ref(false)
const placing = ref(false)
const reference = ref('')
const note = ref('')
let debounce: ReturnType<typeof setTimeout>

// The server prices every line for this buyer (never trusts client prices) and picks the cheapest offer.
async function requote() {
  if (!basket.items.length) { quote.value = null; return }
  loading.value = true
  try {
    quote.value = (await api.post<BasketQuote>('/marketplace/quote', {
      lines: basket.items.map(i => ({ productId: i.productId, quantity: i.quantity, offerId: i.offerId ?? null }))
    })).data
  } catch (e) { toast('bad', 'Could not price basket', errorText(e)) }
  finally { loading.value = false }
}
watch(() => basket.items.map(i => `${i.productId}:${i.quantity}:${i.offerId}`).join(','), () => {
  clearTimeout(debounce)
  debounce = setTimeout(requote, 250)
})
onMounted(requote)

const item = (productId: number) => basket.items.find(i => i.productId === productId)
const canOrder = computed(() => !!quote.value && quote.value.lines.length > 0 && quote.value.lines.every(l => l.chosen) && !!session.customer)

function optimise() {
  basket.items.forEach(i => basket.setOffer(i.productId, undefined))
  toast('info', 'Basket optimised', 'Every line now uses the cheapest seller after your discounts.')
}

async function place() {
  placing.value = true
  try {
    const { data } = await api.post('/marketplace/orders', {
      lines: basket.items.map(i => ({ productId: i.productId, quantity: i.quantity, offerId: quote.value?.lines.find(l => l.productId === i.productId)?.chosen?.offerId ?? null })),
      buyerReference: reference.value || null, note: note.value || null
    })
    toast('good', `${data.length} purchase order(s) sent`, data.map((o: any) => o.number).join(', '))
    basket.clear()
    router.push('/orders')
  } catch (e: any) {
    toast('bad', 'Order not placed', e.response?.data?.detail ?? errorText(e))
    requote()
  } finally {
    placing.value = false
  }
}
</script>

<template>
  <div class="page-header">
    <div class="page-title-group">
      <h1 class="page-title">Basket <span v-if="basket.lines.value" class="badge primary">{{ basket.lines.value }} lines</span></h1>
      <span class="page-subtitle">Priced live for {{ session.customer?.name ?? 'your company' }}: each line goes to the cheapest seller after your discounts, unless you pick one.</span>
    </div>
    <div class="page-actions">
      <button v-if="basket.items.length" class="btn-ghost" @click="basket.clear()"><Icon name="trash" :size="15" /> Empty</button>
      <button v-if="basket.items.some(i => i.offerId)" class="btn-secondary" @click="optimise"><Icon name="sparkles" :size="15" /> Optimise for price</button>
      <router-link to="/catalogue" class="btn btn-secondary"><Icon name="box" :size="15" /> Continue shopping</router-link>
    </div>
  </div>

  <div v-if="!basket.items.length" class="card"><EmptyState icon="tag" title="Your basket is empty" text="Open a product in the catalogue and use “Compare & buy”."><router-link to="/catalogue" class="btn btn-primary btn-sm">Browse catalogue</router-link></EmptyState></div>

  <div v-else class="grid grid-main-side">
    <div class="card">
      <div class="table-wrap" :style="{ opacity: loading ? .6 : 1 }">
        <table>
          <thead><tr><th>Product</th><th style="width: 110px">Qty</th><th>Seller</th><th class="num">Unit</th><th class="num">Line</th><th></th></tr></thead>
          <tbody>
            <tr v-for="l in quote?.lines ?? []" :key="l.productId">
              <td style="max-width: 280px">
                <div class="row" style="gap: .6rem">
                  <img v-if="item(l.productId)?.imageUrl" :src="item(l.productId)!.imageUrl" alt="" width="40" height="40" style="border-radius: 8px; object-fit: cover" />
                  <span v-else class="list-icon"><Icon name="box" :size="15" /></span>
                  <div class="cell-title"><span class="cell-strong" style="white-space: nowrap; overflow: hidden; text-overflow: ellipsis">{{ l.productName }}</span><small><code>{{ l.sku }}</code></small></div>
                </div>
                <div v-if="l.problem" class="field-error">{{ l.problem }}</div>
              </td>
              <td>
                <input type="number" min="1" :value="l.quantity" style="width: 80px" :aria-label="`Quantity of ${l.sku}`"
                  @change="basket.setQuantity(l.productId, Number(($event.target as HTMLInputElement).value))" />
                <div v-if="l.chosen && l.chosen.orderQuantity !== l.quantity" class="hint">Ordered as {{ l.chosen.orderQuantity }} (pack {{ l.chosen.packSize }}, MOQ {{ l.chosen.minOrderQuantity }})</div>
              </td>
              <td style="min-width: 220px">
                <select :value="item(l.productId)?.offerId ?? ''" style="width: 100%" aria-label="Seller"
                  @change="basket.setOffer(l.productId, Number(($event.target as HTMLSelectElement).value) || undefined)">
                  <option value="">Cheapest ({{ l.alternatives.find(a => a.isCheapest)?.supplierCode ?? '—' }})</option>
                  <option v-for="a in l.alternatives" :key="a.offerId" :value="a.offerId" :disabled="!a.purchasable">
                    {{ a.supplierCode }} · {{ fmt.money(a.netUnitPrice, a.currency) }}{{ a.purchasable ? '' : ` (${a.notPurchasableReason})` }}
                  </option>
                </select>
                <div v-if="l.chosen" class="row mt-1" style="gap: .35rem"><AvailabilityBadge :value="l.chosen.availability" :lead-time-days="l.chosen.leadTimeDays" />
                  <span v-if="l.chosen.isCheapest" class="badge good" style="font-size: .68rem">Cheapest</span></div>
              </td>
              <td class="num">
                <div class="cell-title" style="align-items: flex-end">
                  <b>{{ fmt.money(l.chosen?.netUnitPrice, l.chosen?.currency) }}</b>
                  <small v-if="l.chosen?.totalDiscountPercent" style="color: var(--good)">−{{ l.chosen.totalDiscountPercent }}%</small>
                </div>
              </td>
              <td class="num cell-strong">{{ fmt.money(l.chosen?.lineTotal, l.chosen?.currency) }}</td>
              <td class="num"><button class="btn-ghost btn-sm" aria-label="Remove" @click="basket.remove(l.productId)"><Icon name="x" :size="14" /></button></td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>

    <div class="stack">
      <div class="card">
        <div class="card-header"><h3 class="card-title"><Icon name="truck" :size="16" /> Purchase orders</h3><span class="card-sub">one per seller</span></div>
        <div class="list">
          <div v-for="g in quote?.suppliers ?? []" :key="g.supplierId" class="list-item">
            <span class="list-icon"><Icon name="truck" :size="15" /></span>
            <div class="list-main"><div class="list-title">{{ g.supplierCode }} · {{ g.supplierName }}</div><div class="list-sub">{{ g.lines }} line(s)</div></div>
            <b class="tabular">{{ fmt.money(g.subtotal, g.currency) }}</b>
          </div>
        </div>
        <div class="card-body stack" style="gap: .4rem; border-top: 1px solid var(--border)">
          <div class="row"><span class="muted">Total (ex. VAT)</span><span class="spacer" /><b style="font-size: 1.2rem">{{ fmt.money(quote?.total, quote?.currency) }}</b></div>
          <div v-if="quote?.savings && quote.savings > 0" class="alert good" style="padding: .5rem .7rem">
            <Icon name="trendUp" :size="15" /><span>Saving <b>{{ fmt.money(quote.savings, quote.currency) }}</b> vs buying everything from each product's original supplier.</span>
          </div>
        </div>
      </div>
      <div class="card card-pad stack" style="gap: .7rem">
        <div class="field"><label for="ref">Your PO / job reference</label><input id="ref" v-model="reference" placeholder="e.g. JOB-4471" maxlength="64" /></div>
        <div class="field"><label for="nt">Delivery note</label><textarea id="nt" v-model="note" rows="2" maxlength="500" /></div>
        <div v-if="!session.customer" class="alert warn"><Icon name="alert" :size="16" />Your company is not set up to buy.</div>
        <button class="btn-primary btn-lg" :disabled="!canOrder || placing || loading" @click="place">
          <Icon :name="placing ? 'refresh' : 'check'" :size="16" :class="{ spin: placing }" /> Place {{ quote?.suppliers.length ?? 0 }} order(s)
        </button>
        <span class="hint">Prices are re-checked when you place the order. Settlement is on your trade account — no card payment.</span>
      </div>
    </div>
  </div>
</template>
