import { computed, reactive, watch } from 'vue'
import { session } from './session'

export interface BasketItem {
  productId: number; sku: string; name: string; imageUrl?: string
  quantity: number
  /** Supplier offer the buyer picked; undefined = let the server choose the cheapest. */
  offerId?: number
}

/** Client-side basket (per user, in localStorage). Prices are always re-quoted by the server. */
const state = reactive<{ items: BasketItem[] }>({ items: [] })
const key = () => `catalogue.basket.${session.user?.id ?? 'anon'}`

function load() {
  try { state.items = JSON.parse(localStorage.getItem(key()) ?? '[]') } catch { state.items = [] }
}
watch(() => session.user?.id, load, { immediate: true })
watch(() => state.items, items => { try { localStorage.setItem(key(), JSON.stringify(items)) } catch { /* ignore */ } }, { deep: true })

export const basket = {
  get items() { return state.items },
  count: computed(() => state.items.reduce((a, i) => a + i.quantity, 0)),
  lines: computed(() => state.items.length),
  add(item: Omit<BasketItem, 'quantity'> & { quantity?: number }) {
    const existing = state.items.find(i => i.productId === item.productId)
    if (existing) {
      existing.quantity += item.quantity ?? 1
      if (item.offerId) existing.offerId = item.offerId
    } else state.items.push({ ...item, quantity: item.quantity ?? 1 })
  },
  setQuantity(productId: number, quantity: number) {
    const i = state.items.find(x => x.productId === productId)
    if (i) i.quantity = Math.max(1, Math.floor(quantity || 1))
  },
  setOffer(productId: number, offerId?: number) {
    const i = state.items.find(x => x.productId === productId)
    if (i) i.offerId = offerId
  },
  remove(productId: number) { state.items = state.items.filter(i => i.productId !== productId) },
  clear() { state.items = [] }
}
