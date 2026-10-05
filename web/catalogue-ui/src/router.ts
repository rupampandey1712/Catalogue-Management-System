import { createRouter, createWebHistory } from 'vue-router'
import { session, type Policy } from './session'

declare module 'vue-router' {
  interface RouteMeta { auth?: boolean; title?: string; section?: string; policy?: Policy }
}

export const router = createRouter({
  history: createWebHistory(),
  scrollBehavior: () => ({ top: 0 }),
  routes: [
    { path: '/login', component: () => import('./views/LoginView.vue'), meta: { title: 'Sign in' } },
    { path: '/register', component: () => import('./views/RegisterView.vue'), meta: { title: 'Register your company' } },
    { path: '/account', component: () => import('./views/AccountView.vue'), meta: { auth: true, title: 'Account & company', section: 'Account' } },
    { path: '/basket', component: () => import('./views/BasketView.vue'), meta: { auth: true, title: 'Basket', section: 'Marketplace', policy: 'buyer' } },
    { path: '/orders', component: () => import('./views/OrdersView.vue'), meta: { auth: true, title: 'Orders', section: 'Marketplace', policy: 'marketplace' } },
    { path: '/payments', component: () => import('./views/PaymentsView.vue'), meta: { auth: true, title: 'Payments', section: 'Marketplace', policy: 'marketplace' } },
    { path: '/seller', component: () => import('./views/SellerView.vue'), meta: { auth: true, title: 'Seller portal', section: 'Marketplace', policy: 'seller' } },
    { path: '/media', component: () => import('./views/MediaView.vue'), meta: { auth: true, title: 'Media library', section: 'Integration', policy: 'supplierAdmin' } },
    { path: '/admin/organizations', component: () => import('./views/OrganizationsView.vue'), meta: { auth: true, title: 'Companies', section: 'Governance', policy: 'platformAdmin' } },
    { path: '/', redirect: '/dashboard' },
    { path: '/dashboard', component: () => import('./views/DashboardView.vue'), meta: { auth: true, title: 'Dashboard', section: 'Overview', policy: 'readCatalogue' } },
    { path: '/analytics', component: () => import('./views/AnalyticsView.vue'), meta: { auth: true, title: 'Analytics', section: 'Intelligence', policy: 'readCatalogue' } },
    { path: '/assistant', component: () => import('./views/AssistantView.vue'), meta: { auth: true, title: 'Assistant', section: 'Intelligence', policy: 'readCatalogue' } },
    { path: '/operations', component: () => import('./views/OperationsView.vue'), meta: { auth: true, title: 'Operations review', section: 'Intelligence', policy: 'importOps' } },
    { path: '/catalogue', component: () => import('./views/CatalogueView.vue'), meta: { auth: true, title: 'Catalogue', section: 'Manage', policy: 'readCatalogue' } },
    { path: '/products/new', component: () => import('./views/ProductEditView.vue'), meta: { auth: true, title: 'New product', section: 'Catalogue', policy: 'catalogueAdmin' } },
    { path: '/products/:id', component: () => import('./views/ProductEditView.vue'), meta: { auth: true, title: 'Edit product', section: 'Catalogue', policy: 'catalogueAdmin' } },
    { path: '/suppliers', component: () => import('./views/SuppliersView.vue'), meta: { auth: true, title: 'Suppliers', section: 'Manage', policy: 'supplierAdmin' } },
    { path: '/imports', component: () => import('./views/ImportsView.vue'), meta: { auth: true, title: 'Imports', section: 'Integration', policy: 'importOps' } },
    { path: '/imports/:id', component: () => import('./views/ImportDetailView.vue'), meta: { auth: true, title: 'Import details', section: 'Imports', policy: 'importOps' } },
    { path: '/event-hub', component: () => import('./views/EventHubView.vue'), meta: { auth: true, title: 'Event Hub', section: 'Integration', policy: 'importOps' } },
    { path: '/approvals', component: () => import('./views/ApprovalsView.vue'), meta: { auth: true, title: 'Approvals', section: 'Governance', policy: 'importOps' } },
    { path: '/pricing', component: () => import('./views/PricingView.vue'), meta: { auth: true, title: 'Pricing', section: 'Manage', policy: 'readCatalogue' } },
    { path: '/admin/categories', component: () => import('./views/CategoriesView.vue'), meta: { auth: true, title: 'Categories & specs', section: 'Manage', policy: 'readCatalogue' } },
    { path: '/audit', component: () => import('./views/AuditView.vue'), meta: { auth: true, title: 'Audit log', section: 'Governance', policy: 'importOps' } },
    { path: '/:pathMatch(.*)*', component: () => import('./views/NotFoundView.vue'), meta: { auth: true, title: 'Not found' } }
  ]
})

router.beforeEach(to => {
  if (to.meta.auth && !session.isAuthenticated) return { path: '/login', query: to.fullPath !== '/dashboard' ? { next: to.fullPath } : {} }
  if ((to.path === '/login' || to.path === '/register') && session.isAuthenticated) return '/dashboard'
  if (to.meta.policy && !session.can(to.meta.policy)) return to.path === '/dashboard' ? '/catalogue' : (session.can('readCatalogue') ? '/catalogue' : '/account')
})

router.afterEach(to => {
  document.title = to.meta.title ? `${to.meta.title} · Catalogue` : 'Catalogue'
})
