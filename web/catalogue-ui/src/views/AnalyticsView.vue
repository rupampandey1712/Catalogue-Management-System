<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import Icon from '../components/Icon.vue'
import PulseAreaChart from '../components/charts/PulseAreaChart.vue'
import GradientBars from '../components/charts/GradientBars.vue'
import DonutChart from '../components/charts/DonutChart.vue'
import RadialGauge from '../components/charts/RadialGauge.vue'
import Sparkline from '../components/charts/Sparkline.vue'
import { compact } from '../components/charts/palette'
import { api, errorText, type AnalyticsRange, type AnalyticsReport, type ChartSeries, type Kpi } from '../api'
import { fmt, toast } from '../ui'

const ranges: { key: AnalyticsRange; label: string }[] = [
  { key: 'today', label: 'Today' }, { key: '7d', label: '7 days' }, { key: '15d', label: '15 days' },
  { key: '1m', label: '1 month' }, { key: '3m', label: '3 months' }, { key: '6m', label: '6 months' }, { key: '1y', label: 'Year' }
]
const STORE = 'analytics.range'
const route = useRoute()
const router = useRouter()
const initial = (route.query.range as AnalyticsRange) || (() => { try { return localStorage.getItem(STORE) as AnalyticsRange } catch { return null } })() || '7d'
const range = ref<AnalyticsRange>(ranges.some(r => r.key === initial) ? initial : '7d')
const report = ref<AnalyticsReport | null>(null)
const loading = ref(false)

async function load() {
  loading.value = true
  try { report.value = (await api.get<AnalyticsReport>('/analytics', { params: { range: range.value }, timeout: 120_000 })).data }
  catch (e) { toast('bad', 'Analytics unavailable', errorText(e)) }
  finally { loading.value = false }
}
watch(range, r => {
  router.replace({ query: { ...route.query, range: r } })
  try { localStorage.setItem(STORE, r) } catch { /* private mode */ }
  load()
})
onMounted(load)

const platform = computed(() => report.value?.scope === 'platform')
const money = (v: number) => '£' + compact(v)
const label = (iso: string) => {
  const d = new Date(iso.endsWith('Z') ? iso : iso + 'Z')
  switch (report.value?.bucket) {
    case 'Hour': return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', timeZone: 'UTC' })
    case 'Week': return 'w/c ' + d.toLocaleDateString([], { day: 'numeric', month: 'short', timeZone: 'UTC' })
    case 'Month': return d.toLocaleDateString([], { month: 'short', year: '2-digit', timeZone: 'UTC' })
    default: return d.toLocaleDateString([], { day: 'numeric', month: 'short', timeZone: 'UTC' })
  }
}
const labels = computed(() => report.value?.labels ?? [])
const s = (group: string, key: string): number[] => report.value?.series[group]?.find((x: ChartSeries) => x.key === key)?.values ?? []
const bars = (group: string, key: string) => labels.value.map((l, i) => ({ label: label(l), value: s(group, key)[i] ?? 0 }))
const br = (k: string) => report.value?.breakdowns[k] ?? []
const rk = (k: string) => report.value?.rankings[k] ?? []

const kpis = computed(() => (report.value?.kpis ?? []).filter(k => k.key !== 'aov' || k.value > 0))
const kpiColors = ['var(--series-1)', 'var(--series-2)', 'var(--series-3)', '#8b5cf6', '#ec4899', '#f59e0b', '#14b8a6', '#64748b', '#ef4444', '#84cc16', '#0ea5e9']
const kpiIcons: Record<string, string> = {
  orders: 'inbox', gmv: 'trendUp', aov: 'tag', collected: 'card', refunded: 'refresh', productsAdded: 'box', media: 'eye',
  rows: 'database', importErrors: 'alertCircle', decided: 'checkCircle', companies: 'user'
}
const value = (k: Kpi) => (k.unit === 'GBP' ? fmt.money(k.value, 'GBP') : fmt.compact(k.value))
function delta(k: Kpi) {
  if (k.previous == null) return null
  if (k.previous === 0) return k.value > 0 ? { text: 'new', up: true } : null
  const pct = ((k.value - k.previous) / k.previous) * 100
  return { text: `${pct >= 0 ? '+' : ''}${Math.abs(pct) >= 100 ? pct.toFixed(0) : pct.toFixed(1)}%`, up: pct >= 0 }
}
// Rejected rows going up is bad news, everything else up is good.
const goodWhenUp = (k: Kpi) => !['importErrors', 'refunded'].includes(k.key)

const gmvTotal = computed(() => report.value?.kpis.find(k => k.key === 'gmv'))
const coverage = computed(() => report.value && report.value.snapshot.products > 0 ? (report.value.snapshot.productsWithImage / report.value.snapshot.products) * 100 : 0)
const approvalRate = computed(() => {
  const sl = br('changeStatus')
  const total = sl.reduce((a, x) => a + x.value, 0)
  return total ? ((sl.find(x => x.label === 'Approved')?.value ?? 0) / total) * 100 : 0
})
const rangeText = computed(() => report.value ? `${fmt.date(report.value.fromUtc)} – ${fmt.date(report.value.toUtc)} UTC` : '')
</script>

<template>
  <div class="page-header">
    <div class="page-title-group">
      <h1 class="page-title">Analytics</h1>
      <span class="page-subtitle">{{ platform ? 'Everything on the platform' : 'Your company’s orders and payments, plus the shared catalogue' }} · {{ rangeText }}</span>
    </div>
    <div class="row" style="gap: .5rem">
      <div class="segmented" role="group" aria-label="Period">
        <button v-for="r in ranges" :key="r.key" :class="{ active: range === r.key }" :aria-pressed="range === r.key" @click="range = r.key">{{ r.label }}</button>
      </div>
      <button class="btn-ghost btn-sm" aria-label="Refresh" :disabled="loading" @click="load"><Icon name="refresh" :size="15" :class="{ spin: loading }" /></button>
    </div>
  </div>

  <div v-if="!report" class="card card-body muted" style="text-align: center; padding: 3rem">
    <Icon name="refresh" :size="20" class="spin" /> Crunching millions of rows… the first load of a period takes a few seconds, then it is cached.
  </div>

  <template v-else>
    <div class="hero" :class="{ dim: loading }">
      <div class="hero-text">
        <span class="hero-kicker">Order value · {{ ranges.find(r => r.key === range)?.label }}</span>
        <b class="hero-value">{{ fmt.money(gmvTotal?.value ?? 0, 'GBP') }}</b>
        <span v-if="gmvTotal && delta(gmvTotal)" class="hero-delta" :class="{ down: !delta(gmvTotal)!.up }">
          <Icon :name="delta(gmvTotal)!.up ? 'trendUp' : 'chevronDown'" :size="14" /> {{ delta(gmvTotal)!.text }} vs previous period
        </span>
        <div class="hero-mini">
          <span><b>{{ fmt.n(report.snapshot.products) }}</b> products</span>
          <span><b>{{ fmt.n(report.snapshot.offers) }}</b> offers</span>
          <span><b>{{ report.snapshot.suppliers }}</b> suppliers</span>
          <span><b>{{ report.snapshot.activeCompanies }}</b> companies</span>
        </div>
      </div>
      <div class="hero-chart">
        <PulseAreaChart :labels="labels" :format-label="label" :height="190" dual-axis :format="compact"
          :series="[{ label: 'Order value (£)', values: s('orders', 'gmv'), color: '#a5b4fc' }, { label: 'Orders', values: s('orders', 'orders'), color: '#67e8f9', area: false }]" />
      </div>
    </div>

    <div class="kpi-grid" :class="{ dim: loading }">
      <div v-for="(k, i) in kpis" :key="k.key" class="kpi card">
        <div class="kpi-top">
          <span class="kpi-icon" :style="{ background: `color-mix(in srgb, ${kpiColors[i % kpiColors.length]} 14%, transparent)`, color: kpiColors[i % kpiColors.length] }">
            <Icon :name="kpiIcons[k.key] ?? 'activity'" :size="15" />
          </span>
          <span class="kpi-label">{{ k.label }}</span>
        </div>
        <div class="kpi-value">{{ value(k) }}</div>
        <div class="kpi-foot">
          <span v-if="delta(k)" class="delta" :class="delta(k)!.up === goodWhenUp(k) ? 'good' : 'bad'">{{ delta(k)!.text }}</span>
          <span v-else class="muted">—</span>
          <Sparkline v-if="k.spark.length > 1" :values="k.spark" :color="kpiColors[i % kpiColors.length]" :width="90" :height="28" />
        </div>
      </div>
    </div>

    <div class="grid grid-main-side" :class="{ dim: loading }">
      <div class="card">
        <div class="card-header"><h3 class="card-title"><Icon name="card" :size="16" /> Payments</h3><span class="card-sub">Collected vs refunded</span></div>
        <div class="card-body"><PulseAreaChart :labels="labels" :format-label="label" :format="money"
          :series="[{ label: 'Collected', values: s('payments', 'collected'), color: '#10b981' }, { label: 'Refunded', values: s('payments', 'refunded'), color: '#f43f5e' }]" /></div>
      </div>
      <div class="card">
        <div class="card-header"><h3 class="card-title"><Icon name="inbox" :size="16" /> Orders by status</h3></div>
        <div class="card-body"><DonutChart :slices="br('orderStatus')" center-label="orders" /></div>
      </div>
    </div>

    <div class="grid grid-3" :class="{ dim: loading }">
      <div class="card">
        <div class="card-header"><h3 class="card-title"><Icon name="truck" :size="16" /> Top sellers by order value</h3></div>
        <div class="card-body"><GradientBars horizontal :bars="rk('topSuppliers')" :format="money" /></div>
      </div>
      <div class="card">
        <div class="card-header"><h3 class="card-title"><Icon name="tag" :size="16" /> Most ordered products</h3><span class="card-sub">units</span></div>
        <div class="card-body"><GradientBars horizontal :bars="rk('topProducts')" /></div>
      </div>
      <div class="card">
        <div class="card-header"><h3 class="card-title"><Icon name="card" :size="16" /> Payment methods</h3></div>
        <div class="card-body stack">
          <DonutChart pie :slices="br('paymentProvider')" :format="money" :size="140" />
          <DonutChart :slices="br('paymentStatus')" center-label="attempts" :size="140" />
        </div>
      </div>
    </div>

    <template v-if="platform">
      <div class="grid grid-main-side" :class="{ dim: loading }">
        <div class="card">
          <div class="card-header"><h3 class="card-title"><Icon name="database" :size="16" /> Supplier feed ingestion</h3><span class="card-sub">EDI / CSV rows</span></div>
          <div class="card-body"><PulseAreaChart :labels="labels" :format-label="label"
            :series="[{ label: 'Rows processed', values: s('imports', 'rows'), color: '#6366f1' }, { label: 'Rows rejected', values: s('imports', 'errors'), color: '#f97316' }]" /></div>
        </div>
        <div class="card">
          <div class="card-header"><h3 class="card-title"><Icon name="upload" :size="16" /> Imports by status</h3></div>
          <div class="card-body"><DonutChart pie :slices="br('importStatus')" /></div>
        </div>
      </div>
    </template>

    <div class="grid grid-main-side" :class="{ dim: loading }">
      <div class="card">
        <div class="card-header"><h3 class="card-title"><Icon name="box" :size="16" /> Catalogue growth</h3><span class="card-sub">products added per {{ report.bucket.toLowerCase() }}</span></div>
        <div class="card-body"><GradientBars :bars="bars('catalogue', 'created')" single :gradient="['#10b981', '#22d3ee']" /></div>
      </div>
      <div class="card">
        <div class="card-header"><h3 class="card-title"><Icon name="layers" :size="16" /> Stock availability</h3><span class="card-sub">all offers</span></div>
        <div class="card-body"><DonutChart :slices="br('availability')" center-label="offers" /></div>
      </div>
    </div>

    <div class="grid grid-3" :class="{ dim: loading }">
      <div class="card">
        <div class="card-header"><h3 class="card-title"><Icon name="grid" :size="16" /> Products by category</h3></div>
        <div class="card-body"><GradientBars horizontal :bars="rk('categories')" /></div>
      </div>
      <div class="card">
        <div class="card-header"><h3 class="card-title"><Icon name="truck" :size="16" /> Offers by supplier</h3></div>
        <div class="card-body"><GradientBars horizontal :bars="rk('offersBySupplier')" /></div>
      </div>
      <div class="card">
        <div class="card-header"><h3 class="card-title"><Icon name="eye" :size="16" /> Media</h3></div>
        <div class="card-body stack" style="align-items: center">
          <RadialGauge :value="coverage" label="have an image" :sub="`${fmt.n(report.snapshot.productsWithImage)} of ${fmt.compact(report.snapshot.products)} products`" />
          <DonutChart pie :slices="br('mediaKind')" :size="120" />
        </div>
      </div>
    </div>

    <template v-if="platform">
      <div class="grid grid-main-side" :class="{ dim: loading }">
        <div class="card">
          <div class="card-header"><h3 class="card-title"><Icon name="checkCircle" :size="16" /> Approvals</h3><span class="card-sub">changes raised vs decided</span></div>
          <div class="card-body"><PulseAreaChart :labels="labels" :format-label="label"
            :series="[{ label: 'Raised', values: s('approvals', 'raised'), color: '#f59e0b' }, { label: 'Decided', values: s('approvals', 'decided'), color: '#10b981' }]" /></div>
        </div>
        <div class="card">
          <div class="card-header"><h3 class="card-title"><Icon name="checkCircle" :size="16" /> Decisions</h3></div>
          <div class="card-body stack" style="align-items: center">
            <RadialGauge :value="approvalRate" label="approved" from="#10b981" to="#a3e635" :size="130" />
            <DonutChart :slices="br('changeStatus')" center-label="changes" :size="130" />
          </div>
        </div>
      </div>

      <div class="grid grid-3" :class="{ dim: loading }">
        <div class="card">
          <div class="card-header"><h3 class="card-title"><Icon name="user" :size="16" /> Company registrations</h3></div>
          <div class="card-body"><GradientBars :bars="bars('companies', 'registered')" single :gradient="['#8b5cf6', '#ec4899']" :height="200" /></div>
        </div>
        <div class="card">
          <div class="card-header"><h3 class="card-title"><Icon name="user" :size="16" /> Companies by status</h3></div>
          <div class="card-body"><DonutChart pie :slices="br('companyStatus')" :size="150" /></div>
        </div>
        <div class="card">
          <div class="card-header"><h3 class="card-title"><Icon name="activity" :size="16" /> Most frequent actions</h3></div>
          <div class="card-body"><GradientBars horizontal :bars="br('topActions')" /></div>
        </div>
      </div>

      <div class="card" :class="{ dim: loading }">
        <div class="card-header"><h3 class="card-title"><Icon name="activity" :size="16" /> Platform activity</h3><span class="card-sub">audited actions and media uploads</span></div>
        <div class="card-body"><PulseAreaChart :labels="labels" :format-label="label" :height="200" dual-axis
          :series="[{ label: 'Audited actions', values: s('activity', 'events'), color: '#0ea5e9' }, { label: 'Media files added', values: s('media', 'uploads'), color: '#ec4899' }]" /></div>
      </div>
    </template>

    <p class="muted" style="font-size: .76rem; margin-top: 1rem">
      Times are UTC. Computed {{ fmt.ago(report.computedAtUtc) }}; period results are cached for 2 minutes and catalogue totals for 10.
      Changes compare with the equally long period just before.
    </p>
  </template>
</template>

<style scoped>
.dim { opacity: .6; transition: opacity .2s; }
.grid { margin-bottom: 1.1rem; }
.grid-3 { grid-template-columns: repeat(3, minmax(0, 1fr)); }
@media (max-width: 1100px) { .grid-3 { grid-template-columns: 1fr; } }

.hero {
  position: relative; overflow: hidden; display: grid; grid-template-columns: minmax(220px, 1fr) minmax(0, 2.2fr); gap: 1rem; align-items: center;
  border-radius: var(--radius-lg); padding: 1.3rem 1.4rem 1rem; margin-bottom: 1.1rem; color: #fff;
  background: linear-gradient(120deg, #312e81 0%, #4f46e5 45%, #0e7490 100%);
  box-shadow: 0 18px 40px -18px rgb(79 70 229 / 60%);
}
.hero::after {
  content: ''; position: absolute; inset: -40% -10% auto auto; width: 60%; height: 160%; pointer-events: none;
  background: radial-gradient(closest-side, rgb(255 255 255 / 18%), transparent); animation: drift 9s ease-in-out infinite alternate;
}
@keyframes drift { from { transform: translate(0, 0); } to { transform: translate(-12%, 8%); } }
@media (prefers-reduced-motion: reduce) { .hero::after { animation: none; } }
.hero-text { display: grid; gap: .35rem; position: relative; z-index: 1; }
.hero-kicker { font-size: .78rem; opacity: .8; text-transform: uppercase; letter-spacing: .06em; }
.hero-value { font-size: 2.1rem; line-height: 1.1; font-variant-numeric: tabular-nums; }
.hero-delta { display: inline-flex; align-items: center; gap: .3rem; font-size: .8rem; background: rgb(255 255 255 / 16%); border-radius: 999px; padding: .15rem .55rem; width: fit-content; }
.hero-delta.down { background: rgb(244 63 94 / 30%); }
.hero-mini { display: flex; flex-wrap: wrap; gap: .4rem 1rem; font-size: .78rem; opacity: .9; margin-top: .4rem; }
.hero-chart { position: relative; z-index: 1; --grid: rgb(255 255 255 / 14%); --muted: rgb(255 255 255 / 70%); --axis: rgb(255 255 255 / 50%); --text-secondary: rgb(255 255 255 / 85%); }
@media (max-width: 900px) { .hero { grid-template-columns: 1fr; } }

.kpi-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(190px, 1fr)); gap: .9rem; margin-bottom: 1.1rem; }
.kpi { padding: .85rem .95rem; display: grid; gap: .35rem; transition: transform .15s, box-shadow .15s; }
.kpi:hover { transform: translateY(-2px); box-shadow: var(--shadow-md); }
.kpi-top { display: flex; align-items: center; gap: .5rem; }
.kpi-icon { width: 28px; height: 28px; border-radius: 8px; display: grid; place-items: center; }
.kpi-label { font-size: .8rem; color: var(--text-secondary); }
.kpi-value { font-size: 1.45rem; font-weight: 650; font-variant-numeric: tabular-nums; }
.kpi-foot { display: flex; align-items: flex-end; justify-content: space-between; gap: .5rem; min-height: 28px; }
.delta { font-size: .76rem; font-weight: 600; padding: .1rem .45rem; border-radius: 999px; }
.delta.good { color: var(--good); background: var(--good-soft); }
.delta.bad { color: var(--bad); background: var(--bad-soft); }
</style>
