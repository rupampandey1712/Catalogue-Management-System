import { reactive } from 'vue'

/** Endpoint policies from the API, mirrored so the UI only offers what the user can do. */
const POLICIES = {
  readCatalogue: ['Admin', 'SupplierManager', 'Buyer', 'Seller', 'Support', 'Viewer'],
  catalogueAdmin: ['Admin'],
  platformAdmin: ['Admin'],
  supplierAdmin: ['Admin', 'SupplierManager'],
  importOps: ['Admin', 'SupplierManager', 'Support'],
  buyer: ['Buyer', 'Admin'],
  seller: ['Seller', 'Admin', 'SupplierManager'],
  marketplace: ['Buyer', 'Seller', 'Admin', 'SupplierManager', 'Support'],
  orgAdmin: ['OrgAdmin']
} as const
export type Policy = keyof typeof POLICIES

export interface OrgInfo { id: number; name: string; canBuy: boolean; canSell: boolean; status: string; isPlatformOwner: boolean }
export interface UserProfile {
  id: number; username: string; displayName: string; email: string; roles: string[]
  organization?: OrgInfo; customerId?: number; customerName?: string; supplierId?: number; supplierCode?: string
}

/**
 * The access token is kept in memory only (30 min lifetime); the long-lived refresh token is an
 * httpOnly cookie the browser sends to /api/auth/refresh. A page reload restores the session from
 * that cookie, so nothing an injected script could steal sits in localStorage.
 */
const state = reactive<{ token: string | null; expiresAt: number; user: UserProfile | null; restored: boolean }>({
  token: null, expiresAt: 0, user: null, restored: false
})

const HINT = 'catalogue.session' // non-secret marker: "there may be a refresh cookie, try restoring"

export const session = {
  get token() { return state.token },
  get user() { return state.user },
  get isAuthenticated() { return !!state.token && !!state.user },
  get initials() { return (state.user?.displayName ?? '?').split(' ').map(p => p[0]).join('').slice(0, 2).toUpperCase() },
  get primaryRole() {
    const r = state.user?.roles ?? []
    return r.includes('Admin') ? 'Platform admin' : r.includes('Support') ? 'Support' : r.includes('SupplierManager') ? 'Supplier manager'
      : r.includes('Seller') && r.includes('Buyer') ? 'Buyer & seller' : r.includes('Seller') ? 'Seller' : r.includes('Buyer') ? 'Buyer' : 'Viewer'
  },
  get customer() { return state.user?.customerId ? { id: state.user.customerId, name: state.user.customerName ?? '' } : null },
  get organization() { return state.user?.organization ?? null },
  get expiresAt() { return state.expiresAt },
  can(policy: Policy): boolean {
    const roles = state.user?.roles ?? []
    return roles.some(r => (POLICIES[policy] as readonly string[]).includes(r))
  },
  /** Called with every login/refresh response. */
  set(token: string, expiresAtUtc: string, user: UserProfile) {
    state.token = token
    state.expiresAt = new Date(expiresAtUtc).getTime()
    state.user = user
    try { localStorage.setItem(HINT, '1') } catch { /* private mode */ }
  },
  clear() {
    state.token = null
    state.user = null
    state.expiresAt = 0
    try { localStorage.removeItem(HINT); localStorage.removeItem('token'); localStorage.removeItem('user') } catch { /* ignore */ }
  },
  get hasSessionHint() { try { return localStorage.getItem(HINT) === '1' } catch { return false } },
  get restored() { return state.restored },
  markRestored() { state.restored = true }
}
