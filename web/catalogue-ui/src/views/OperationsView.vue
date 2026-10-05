<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import Icon from '../components/Icon.vue'
import EmptyState from '../components/EmptyState.vue'
import DonutChart from '../components/charts/DonutChart.vue'
import { api, errorText, followRun, type AgentStatus, type Proposal, type ReviewDetail, type ReviewSummary, type RunEvent } from '../api'
import { fmt, toast } from '../ui'

interface Step { step: string; label: string; state: 'waiting' | 'running' | 'done'; summary?: string }
const STEPS: Step[] = [
  { step: 'collect-signals', label: 'Collect signals', state: 'waiting' },
  { step: 'triage', label: 'Triage findings', state: 'waiting' },
  { step: 'briefing', label: 'Write briefing', state: 'waiting' },
  { step: 'publish', label: 'Publish & propose', state: 'waiting' }
]

const reviews = ref<ReviewSummary[]>([])
const selected = ref<ReviewDetail | null>(null)
const pending = ref<Proposal[]>([])
const status = ref<AgentStatus | null>(null)
const running = ref<{ runId: string; reviewId: number; message: string; steps: Step[]; startedAt: number } | null>(null)
const deciding = ref<number | null>(null)
const severity = ref('')
const now = ref(Date.now())
let abort: AbortController | null = null
let ticker: number | undefined

async function load() {
  const [r, p, s] = await Promise.all([
    api.get<ReviewSummary[]>('/agent/reviews/'), api.get<Proposal[]>('/agent/proposals', { params: { status: 'Pending' } }), api.get<AgentStatus>('/agent/status')
  ])
  reviews.value = r.data
  pending.value = p.data.filter(x => x.status === 'Pending')
  status.value = s.data
  if (!selected.value && reviews.value.length) await show(reviews.value.find(x => x.status === 'Completed')?.id ?? reviews.value[0].id)
}
async function show(id: number) { selected.value = (await api.get<ReviewDetail>(`/agent/reviews/${id}`)).data }

async function start() {
  try {
    const { data } = await api.post<{ reviewId: number; runId: string }>('/agent/reviews/')
    running.value = { runId: data.runId, reviewId: data.reviewId, message: 'Queued…', steps: STEPS.map(s => ({ ...s })), startedAt: Date.now() }
    abort = new AbortController()
    followRun(data.runId, onEvent, abort.signal)
    load()
  } catch (e: any) { toast('bad', 'Review not started', e.response?.data?.detail ?? errorText(e)) }
}

function onEvent(e: RunEvent) {
  const r = running.value
  if (!r) return
  switch (e.type) {
    case 'queued': r.message = `Waiting for the model (position ${e.data.position})`; break
    case 'started': r.message = 'Running'; r.startedAt = Date.now(); break
    case 'status': r.message = e.data.message; break
    case 'step': {
      const s = r.steps.find(x => x.step === e.data.step)
      if (s) { s.state = e.data.state; if (e.data.summary) s.summary = e.data.summary }
      if (e.data.state === 'running') r.message = e.data.label
      break
    }
    case 'review': toast('good', 'Review complete', `${e.data.findings} findings, ${e.data.proposals} proposal(s)`); break
    case 'done':
      if (e.data.status !== 'Completed') toast('bad', 'Review failed', e.data.error)
      const id = r.reviewId
      running.value = null
      abort?.abort()
      load().then(() => show(id))
      break
  }
}

async function decide(p: Proposal, approve: boolean) {
  deciding.value = p.id
  try {
    const { data } = await api.post(`/agent/proposals/${p.id}/${approve ? 'approve' : 'reject'}`, {})
    toast(approve ? 'good' : 'info', approve ? 'Done' : 'Rejected', data.message)
  } catch (e: any) { toast('bad', approve ? 'Not executed' : 'Not rejected', e.response?.data?.detail ?? errorText(e)) }
  finally {
    deciding.value = null
    await load()
    if (selected.value) show(selected.value.id)
  }
}

const findings = computed(() => (selected.value?.findings ?? []).filter(f => !severity.value || f.severity === severity.value))
const severitySlices = computed(() => selected.value ? [
  { label: 'Critical', value: selected.value.critical }, { label: 'Warning', value: selected.value.warnings }, { label: 'Info', value: selected.value.infos }
] : [])
const sevTone = (s: string) => ({ critical: 'bad', warning: 'warn', info: 'info' } as Record<string, string>)[s] ?? ''
const sevIcon = (s: string) => ({ critical: 'alertCircle', warning: 'alert', info: 'info' } as Record<string, string>)[s] ?? 'info'
const statusTone = (s: string) => ({ Completed: 'good', Running: 'info', Failed: 'bad' } as Record<string, string>)[s] ?? ''
const elapsed = computed(() => (running.value ? Math.round((now.value - running.value.startedAt) / 1000) : 0))

onMounted(() => { load(); ticker = window.setInterval(() => { now.value = Date.now() }, 1000) })
onUnmounted(() => { abort?.abort(); clearInterval(ticker) })
</script>

<template>
  <div class="page-header">
    <div class="page-title-group">
      <h1 class="page-title"><Icon name="activity" :size="20" /> Operations review</h1>
      <span class="page-subtitle">
        An Agent Framework workflow checks imports, approvals, orders, payments, companies and the catalogue, writes a briefing with the local model
        and proposes fixes for approval.<template v-if="status?.review.enabled"> Runs automatically every {{ status.review.intervalHours }} h.</template>
      </span>
    </div>
    <button class="btn-primary" :disabled="!!running" @click="start"><Icon :name="running ? 'refresh' : 'play'" :size="15" :class="{ spin: running }" /> Run review now</button>
  </div>

  <div v-if="running" class="card run">
    <div class="card-body stack">
      <div class="row"><span class="pulse-dot" /><b>{{ running.message }}</b><span class="spacer" /><span class="muted mono">{{ Math.floor(elapsed / 60) }}:{{ String(elapsed % 60).padStart(2, '0') }}</span></div>
      <ol class="steps">
        <li v-for="s in running.steps" :key="s.step" :class="s.state">
          <span class="node"><Icon :name="s.state === 'done' ? 'check' : s.state === 'running' ? 'refresh' : 'clock'" :size="13" :class="{ spin: s.state === 'running' }" /></span>
          <div><b>{{ s.label }}</b><div class="muted" style="font-size: .76rem">{{ s.summary ?? (s.state === 'running' ? 'in progress…' : '') }}</div></div>
        </li>
      </ol>
      <span class="muted" style="font-size: .76rem">Writing the briefing with the local model takes a few minutes; if it is unavailable or slow, a rule-based briefing is used.</span>
    </div>
  </div>

  <div class="grid grid-main-side" style="align-items: start">
    <div class="stack">
      <div v-if="selected" class="card">
        <div class="card-header">
          <h3 class="card-title">Review #{{ selected.id }}</h3>
          <span class="badge" :class="statusTone(selected.status)">{{ selected.status }}</span>
          <span class="spacer" />
          <span class="card-sub">{{ fmt.date(selected.startedAtUtc) }} · {{ selected.trigger }} · {{ selected.durationMs ? fmt.duration(selected.durationMs) : '' }}</span>
        </div>
        <div class="card-body stack">
          <div class="briefing" :class="{ model: selected.generatedByModel }">
            <div class="row" style="gap: .4rem; margin-bottom: .4rem">
              <Icon :name="selected.generatedByModel ? 'sparkles' : 'layers'" :size="14" />
              <b style="font-size: .82rem">{{ selected.generatedByModel ? `Briefing by ${selected.modelId}` : 'Rule-based briefing' }}</b>
              <span v-if="selected.error && !selected.generatedByModel" class="muted" style="font-size: .76rem">({{ selected.error }})</span>
            </div>
            <div class="text">{{ selected.briefing ?? selected.error ?? 'No briefing.' }}</div>
          </div>
          <div class="row wrap" style="align-items: center; gap: 1.5rem">
            <DonutChart :slices="severitySlices" center-label="findings" :size="130" />
          </div>
          <div class="toolbar" style="padding: 0">
            <div class="segmented">
              <button v-for="f in ['', 'critical', 'warning', 'info']" :key="f" :class="{ active: severity === f }" @click="severity = f">{{ f ? f[0].toUpperCase() + f.slice(1) : 'All' }}</button>
            </div>
          </div>
          <div class="list" style="border: 1px solid var(--border); border-radius: var(--radius)">
            <div v-for="(f, i) in findings" :key="i" class="list-item">
              <span class="list-icon" :class="sevTone(f.severity)"><Icon :name="sevIcon(f.severity)" :size="15" /></span>
              <div class="list-main"><div class="list-title">{{ f.title }}</div><div class="list-sub">{{ f.area }} · {{ f.detail }}</div></div>
              <span v-if="f.action" class="badge primary" style="font-size: .68rem">action proposed</span>
            </div>
            <EmptyState v-if="!findings.length" icon="checkCircle" title="Nothing to report" />
          </div>
        </div>
      </div>
      <EmptyState v-else-if="!running" icon="activity" title="No reviews yet" text="Run the first review to get a briefing and proposed actions." />
    </div>

    <div class="stack">
      <div class="card">
        <div class="card-header"><h3 class="card-title"><Icon name="checkCircle" :size="16" /> Waiting for approval</h3><span class="badge warn">{{ pending.length }}</span></div>
        <div class="list">
          <div v-for="p in pending" :key="p.id" class="list-item proposal">
            <div class="list-main">
              <div class="list-title">{{ p.targetLabel }}</div>
              <div class="list-sub">{{ p.reason }} · {{ p.source === 'assistant' ? `asked by ${p.requestedBy}` : 'operations review' }} · expires {{ fmt.ago(p.expiresAtUtc).replace(' ago', '') }}</div>
              <div class="row" style="gap: .35rem; margin-top: .35rem">
                <button class="btn-primary btn-sm" :disabled="deciding === p.id" @click="decide(p, true)"><Icon name="check" :size="13" /> Approve</button>
                <button class="btn-ghost btn-sm" :disabled="deciding === p.id" @click="decide(p, false)">Reject</button>
              </div>
            </div>
          </div>
          <EmptyState v-if="!pending.length" icon="checkCircle" title="Nothing waiting" />
        </div>
      </div>
      <div class="card">
        <div class="card-header"><h3 class="card-title">History</h3></div>
        <div class="list">
          <div v-for="r in reviews" :key="r.id" class="list-item clickable" :class="{ active: selected?.id === r.id }" @click="show(r.id)">
            <div class="list-main">
              <div class="list-title">#{{ r.id }} · {{ fmt.ago(r.startedAtUtc) }} <span class="muted">({{ r.trigger }})</span></div>
              <div class="list-sub">
                <span v-if="r.critical" style="color: var(--bad)">{{ r.critical }} critical · </span>{{ r.warnings }} warnings · {{ r.proposalCount }} proposals
                · {{ r.generatedByModel ? 'model' : 'rules' }}
              </div>
            </div>
            <span class="badge" :class="statusTone(r.status)">{{ r.status }}</span>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.run { margin-bottom: 1.1rem; border-color: var(--primary); }
.pulse-dot { width: 10px; height: 10px; border-radius: 50%; background: var(--primary); animation: pulse 1.4s ease-in-out infinite; }
@keyframes pulse { 0%, 100% { opacity: .35; transform: scale(.85); } 50% { opacity: 1; transform: scale(1.15); } }
.steps { list-style: none; margin: 0; padding: 0; display: grid; grid-template-columns: repeat(4, 1fr); gap: .6rem; }
@media (max-width: 900px) { .steps { grid-template-columns: 1fr 1fr; } }
.steps li { display: flex; gap: .5rem; align-items: flex-start; padding: .55rem; border-radius: 10px; background: var(--surface-2); border: 1px solid var(--border); font-size: .84rem; }
.steps li.running { border-color: var(--primary); background: var(--primary-soft); }
.steps li.done { border-color: var(--good); }
.node { width: 22px; height: 22px; border-radius: 50%; display: grid; place-items: center; background: var(--surface-3); flex: none; }
.steps li.done .node { background: var(--good-soft); color: var(--good); }
.steps li.running .node { color: var(--primary); }
.briefing { border-radius: 12px; padding: .8rem 1rem; background: var(--surface-2); border: 1px solid var(--border); }
.briefing.model { background: linear-gradient(135deg, var(--primary-soft), transparent 70%); border-color: color-mix(in srgb, var(--primary) 35%, var(--border)); }
.briefing .text { white-space: pre-wrap; line-height: 1.55; }
.list-icon.bad { color: var(--bad); } .list-icon.warn { color: var(--warn); } .list-icon.info { color: var(--info); }
.list-item.active { background: var(--primary-soft); }
.clickable { cursor: pointer; }
</style>
