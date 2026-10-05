<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import Icon from './components/Icon.vue'
import ToastHost from './components/ToastHost.vue'
import { session, type Policy } from './session'
import { api } from './api'
import { basket } from './basket'
import { applyTheme, layout, theme, toggleSidebar, useHotkey, usePolling, type Theme } from './ui'

const route = useRoute()
const router = useRouter()
const menuOpen = ref(false)
const search = ref('')
const searchInput = ref<HTMLInputElement>()
const streaming = ref(false)
const pendingApprovals = ref(0)

interface NavItem { to: string; label: string; icon: string; policy: Policy; badge?: 'live' | 'count' | 'basket' }
const groups: { title: string; items: NavItem[] }[] = [
  { title: 'Overview', items: [{ to: '/dashboard', label: 'Dashboard', icon: 'dashboard', policy: 'readCatalogue' }] },
  {
    title: 'Intelligence', items: [
      { to: '/analytics', label: 'Analytics', icon: 'trendUp', policy: 'readCatalogue' },
      { to: '/assistant', label: 'Assistant', icon: 'sparkles', policy: 'readCatalogue' },
      { to: '/operations', label: 'Operations review', icon: 'activity', policy: 'importOps' }
    ]
  },
  {
    title: 'Manage', items: [
      { to: '/catalogue', label: 'Catalogue', icon: 'box', policy: 'readCatalogue' },
      { to: '/admin/categories', label: 'Categories & specs', icon: 'layers', policy: 'readCatalogue' },
      { to: '/pricing', label: 'Pricing', icon: 'tag', policy: 'readCatalogue' },
      { to: '/suppliers', label: 'Suppliers', icon: 'truck', policy: 'supplierAdmin' }
    ]
  },
  {
    title: 'Marketplace', items: [
      { to: '/basket', label: 'Basket', icon: 'tag', policy: 'buyer', badge: 'basket' },
      { to: '/orders', label: 'Orders', icon: 'inbox', policy: 'marketplace' },
      { to: '/payments', label: 'Payments', icon: 'card', policy: 'marketplace' },
      { to: '/seller', label: 'Seller portal', icon: 'truck', policy: 'seller' }
    ]
  },
  {
    title: 'Integration', items: [
      { to: '/imports', label: 'Imports', icon: 'upload', policy: 'importOps' },
      { to: '/event-hub', label: 'Event Hub', icon: 'zap', policy: 'importOps', badge: 'live' },
      { to: '/media', label: 'Media library', icon: 'eye', policy: 'supplierAdmin' }
    ]
  },
  {
    title: 'Governance', items: [
      { to: '/approvals', label: 'Approvals', icon: 'checkCircle', policy: 'importOps', badge: 'count' },
      { to: '/admin/organizations', label: 'Companies', icon: 'user', policy: 'platformAdmin' },
      { to: '/audit', label: 'Audit log', icon: 'shield', policy: 'importOps' }
    ]
  }
]
const visibleGroups = computed(() => groups
  .map(g => ({ ...g, items: g.items.filter(i => session.can(i.policy)) }))
  .filter(g => g.items.length))

// Light-weight "is something streaming" indicator for the sidebar.
usePolling(async () => {
  if (!session.isAuthenticated || !session.can('importOps')) return
  const { data } = await api.get('/eventhub/overview')
  streaming.value = data.runs?.some((r: any) => r.state === 'Publishing') || data.totalLag > 0 || data.rates?.rowsPerSec > 0
}, 8000)
usePolling(async () => {
  if (!session.isAuthenticated || !session.can('importOps')) return
  pendingApprovals.value = (await api.get('/approvals/summary')).data.totalPending
}, 20000)

const themes: { value: Theme; label: string; icon: string }[] = [
  { value: 'light', label: 'Light', icon: 'sun' },
  { value: 'dark', label: 'Dark', icon: 'moon' },
  { value: 'system', label: 'System', icon: 'monitor' }
]
const themeIcon = computed(() => themes.find(t => t.value === theme.value)?.icon ?? 'monitor')
function cycleTheme() {
  const i = themes.findIndex(t => t.value === theme.value)
  applyTheme(themes[(i + 1) % themes.length].value)
}

function runSearch() {
  const q = search.value.trim()
  router.push({ path: '/catalogue', query: q ? { q } : {} })
  searchInput.value?.blur()
}
useHotkey('mod+k', e => { e.preventDefault(); searchInput.value?.focus() })
useHotkey('/', e => {
  const tag = (e.target as HTMLElement)?.tagName
  if (tag !== 'INPUT' && tag !== 'TEXTAREA' && tag !== 'SELECT') { e.preventDefault(); searchInput.value?.focus() }
})

async function logout() {
  menuOpen.value = false
  try { await api.post('/auth/logout') } catch { /* cookie is cleared server-side when reachable */ }
  session.clear()
  router.push('/login')
}

watch(() => route.fullPath, () => { layout.mobileOpen = false; menuOpen.value = false })
</script>

<template>
  <div v-if="route.path !== '/login' && route.path !== '/register'" class="app-shell" :class="{ collapsed: layout.collapsed, 'mobile-open': layout.mobileOpen }">
    <aside class="sidebar" aria-label="Main navigation">
      <div class="sidebar-brand">
        <span class="brand-mark"><Icon name="box" :size="18" /></span>
        <span class="brand-text">Catalogue<span class="brand-sub">Product information hub</span></span>
      </div>
      <template v-for="g in visibleGroups" :key="g.title">
        <div class="sidebar-section">{{ g.title }}</div>
        <nav class="sidebar-nav">
          <router-link v-for="item in g.items" :key="item.to" :to="item.to" :title="layout.collapsed ? item.label : undefined">
            <Icon :name="item.icon" :size="18" />
            <span class="nav-label">{{ item.label }}</span>
            <span v-if="item.badge === 'live' && streaming" class="nav-pill live">Live</span>
            <span v-if="item.badge === 'basket' && basket.lines.value > 0" class="nav-pill">{{ basket.lines.value }}</span>
            <span v-if="item.badge === 'count' && pendingApprovals > 0" class="nav-pill">{{ pendingApprovals > 999 ? Math.round(pendingApprovals / 1000) + 'k' : pendingApprovals }}</span>
          </router-link>
        </nav>
      </template>
      <div class="sidebar-footer">
        <button class="collapse-btn" @click="toggleSidebar">
          <Icon :name="layout.collapsed ? 'chevronRight' : 'chevronLeft'" :size="18" />
          <span class="nav-label">Collapse</span>
        </button>
      </div>
    </aside>

    <div class="content">
      <header class="topbar">
        <button class="icon-btn mobile-only" aria-label="Open menu" @click="layout.mobileOpen = !layout.mobileOpen"><Icon name="menu" :size="17" /></button>
        <div class="crumbs">
          <span v-if="route.meta.section">{{ route.meta.section }}</span>
          <Icon v-if="route.meta.section" name="chevronRight" :size="14" />
          <strong>{{ route.meta.title }}</strong>
        </div>
        <form class="topbar-search" role="search" @submit.prevent="runSearch">
          <span class="icon"><Icon name="search" :size="15" /></span>
          <input ref="searchInput" v-model="search" placeholder="Search SKU or product name…" aria-label="Search catalogue" />
          <span class="kbd">Ctrl K</span>
        </form>
        <button class="icon-btn" :title="`Theme: ${theme}`" aria-label="Toggle theme" @click="cycleTheme"><Icon :name="themeIcon" :size="17" /></button>
        <div class="user-chip" tabindex="0" @click="menuOpen = !menuOpen" @keydown.enter="menuOpen = !menuOpen">
          <span class="avatar">{{ session.initials }}</span>
          <span class="user-meta"><b>{{ session.user?.displayName }}</b><span>{{ session.organization?.name ?? session.primaryRole }}</span></span>
          <Icon name="chevronDown" :size="14" style="color: var(--muted)" />
          <div v-if="menuOpen" class="menu" @click.stop>
            <div style="padding: .45rem .6rem .35rem">
              <div style="font-weight: 650">{{ session.user?.username }}</div>
              <div class="muted" style="font-size: .76rem">{{ session.organization?.name ?? 'Platform staff' }}</div>
              <div class="muted" style="font-size: .76rem">Roles: {{ session.user?.roles.join(', ') }}</div>
            </div>
            <div class="menu-sep" />
            <button v-for="t in themes" :key="t.value" @click="applyTheme(t.value)">
              <Icon :name="t.icon" :size="15" /> {{ t.label }} theme
              <Icon v-if="theme === t.value" name="check" :size="15" style="margin-left: auto; color: var(--primary)" />
            </button>
            <div class="menu-sep" />
            <router-link to="/account" @click="menuOpen = false"><Icon name="user" :size="15" /> Account & company</router-link>
            <div class="menu-sep" />
            <button @click="logout"><Icon name="logout" :size="15" /> Sign out</button>
          </div>
        </div>
      </header>
      <main class="content-body">
        <router-view v-slot="{ Component }">
          <component :is="Component" :key="route.path" />
        </router-view>
      </main>
    </div>
    <div v-if="menuOpen" style="position: fixed; inset: 0; z-index: 15" @click="menuOpen = false" />
  </div>
  <router-view v-else />
  <ToastHost />
</template>
