<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import Icon from '../components/Icon.vue'
import { api, errorText, followRun, type AgentStatus, type Conversation, type ConversationSummary, type Proposal, type RunEvent } from '../api'
import { session } from '../session'
import { fmt, toast } from '../ui'

interface LiveTool { name: string; arguments: string; preview?: string; failed?: boolean; ms?: number; done: boolean }
interface Live { runId: string; status: string; queue?: number; reasoning: string; text: string; tools: LiveTool[]; startedAt: number; question: string }

const route = useRoute()
const router = useRouter()
const status = ref<AgentStatus | null>(null)
const conversations = ref<ConversationSummary[]>([])
const current = ref<Conversation | null>(null)
const live = ref<Live | null>(null)
const input = ref('')
const sending = ref(false)
const now = ref(Date.now())
const showReasoning = ref(true)
const scroller = ref<HTMLDivElement>()
const deciding = ref<number | null>(null)
let abort: AbortController | null = null
let ticker: number | undefined
let statusPoll: number | undefined

const suggestions = computed(() => {
  const s = ['What needs my attention today?', 'Find heat pumps from Thermora', 'How did sales do in the last 7 days compared to the week before?']
  if (session.can('importOps')) s.splice(1, 0, 'Did any supplier imports fail recently, and why?', 'Which price changes are waiting for approval?')
  if (session.can('marketplace')) s.push('Which orders are waiting for the seller to accept?')
  return s.slice(0, 5)
})

async function loadStatus(refresh = false) {
  try { status.value = (await api.get<AgentStatus>('/agent/status', { params: { refresh } })).data } catch { /* shown as unavailable */ }
}
async function loadConversations() { conversations.value = (await api.get<ConversationSummary[]>('/agent/conversations')).data }
async function open(id: string) {
  current.value = (await api.get<Conversation>(`/agent/conversations/${id}`)).data
  router.replace({ query: { c: id } })
  scrollDown()
  if (current.value.activeRunId && live.value?.runId !== current.value.activeRunId) follow(current.value.activeRunId, '')
}
function newChat() {
  current.value = null
  live.value = null
  router.replace({ query: {} })
}

async function send(text?: string) {
  const q = (text ?? input.value).trim()
  if (!q || sending.value || live.value) return
  sending.value = true
  try {
    if (!current.value) {
      const { data } = await api.post<{ id: string }>('/agent/conversations')
      await open(data.id)
    }
    const id = current.value!.id
    const { data } = await api.post<{ runId: string }>(`/agent/conversations/${id}/messages`, { text: q })
    current.value!.messages.push({ id: -Date.now(), role: 'user', content: q, createdAtUtc: new Date().toISOString() })
    input.value = ''
    follow(data.runId, q)
  } catch (e: any) {
    toast('bad', 'Not sent', e.response?.data?.detail ?? errorText(e))
  } finally { sending.value = false }
}

function follow(runId: string, question: string) {
  abort?.abort()
  abort = new AbortController()
  live.value = { runId, status: 'Queued…', reasoning: '', text: '', tools: [], startedAt: Date.now(), question }
  scrollDown()
  followRun(runId, onEvent, abort.signal)
}

function onEvent(e: RunEvent) {
  const l = live.value
  if (!l) return
  switch (e.type) {
    case 'queued': l.queue = e.data.position; l.status = e.data.position > 1 ? `Waiting in line (position ${e.data.position})` : 'Next in line…'; break
    case 'started': l.queue = undefined; l.status = 'Starting…'; l.startedAt = Date.now(); break
    case 'status': l.status = e.data.message; break
    case 'reasoning': l.reasoning += e.data.delta; l.status = 'Thinking…'; break
    case 'tool_call': l.tools.push({ name: e.data.name, arguments: e.data.arguments, done: false }); l.status = `Using ${e.data.name}…`; break
    case 'tool_result': {
      const t = [...l.tools].reverse().find(x => x.name === e.data.name && !x.done)
      if (t) Object.assign(t, { preview: e.data.preview, failed: e.data.failed, ms: e.data.ms, done: true })
      l.status = 'Reading the results…'
      break
    }
    case 'text': l.text += e.data.delta; l.status = 'Answering…'; break
    case 'done':
      if (e.data.status !== 'Completed') toast(e.data.status === 'Cancelled' ? 'info' : 'bad', e.data.status === 'Cancelled' ? 'Stopped' : 'The assistant failed', e.data.error)
      finish()
      return
  }
  scrollDown()
}

async function finish() {
  const id = current.value?.id
  live.value = null
  abort?.abort()
  if (id) await open(id)
  loadConversations()
  loadStatus()
}

async function cancel() {
  if (!live.value) return
  try { await api.post(`/agent/runs/${live.value.runId}/cancel`) } catch (e) { toast('bad', 'Could not cancel', errorText(e)) }
}

async function remove(c: ConversationSummary) {
  try {
    await api.delete(`/agent/conversations/${c.id}`)
    if (current.value?.id === c.id) newChat()
    loadConversations()
  } catch (e: any) { toast('bad', 'Not deleted', e.response?.data?.error ?? errorText(e)) }
}

async function decide(p: Proposal, approve: boolean) {
  deciding.value = p.id
  try {
    const { data } = await api.post(`/agent/proposals/${p.id}/${approve ? 'approve' : 'reject'}`, {})
    toast(approve ? 'good' : 'info', approve ? 'Done' : 'Rejected', data.message)
  } catch (e: any) { toast('bad', approve ? 'Not executed' : 'Not rejected', e.response?.data?.detail ?? errorText(e)) }
  finally {
    deciding.value = null
    if (current.value) open(current.value.id)
  }
}

async function loadModel() {
  try { await api.post('/agent/model/load'); toast('info', 'Loading the model', 'This takes about a minute on first use.') }
  catch (e) { toast('bad', 'Could not start loading', errorText(e)) }
  setTimeout(() => loadStatus(true), 15000)
}

function scrollDown() { nextTick(() => scroller.value?.scrollTo({ top: scroller.value.scrollHeight, behavior: 'smooth' })) }
const elapsed = computed(() => (live.value ? Math.max(0, Math.round((now.value - live.value.startedAt) / 1000)) : 0))
const mmss = (s: number) => `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`
const argText = (a: string) => { try { const o = JSON.parse(a); return Object.entries(o).map(([k, v]) => `${k}: ${typeof v === 'string' ? v : JSON.stringify(v)}`).join(', ') } catch { return a } }
const ready = computed(() => status.value?.enabled && status.value.model.serviceRunning && status.value.model.modelCached)
const proposalTone = (s: string) => ({ Pending: 'warn', Executed: 'good', Rejected: '', Failed: 'bad', Expired: '' } as Record<string, string>)[s] ?? ''

onMounted(async () => {
  ticker = window.setInterval(() => { now.value = Date.now() }, 1000)
  statusPoll = window.setInterval(() => loadStatus(), 30000)
  await Promise.all([loadStatus(), loadConversations()])
  const id = (route.query.c as string) || undefined
  if (id) await open(id).catch(() => newChat())
})
onUnmounted(() => { abort?.abort(); clearInterval(ticker); clearInterval(statusPoll) })
</script>

<template>
  <div class="page-header">
    <div class="page-title-group">
      <h1 class="page-title"><Icon name="sparkles" :size="20" /> Operations assistant</h1>
      <span class="page-subtitle">Microsoft Agent Framework agent on your local Foundry model. It reads catalogue, order, import and approval data with your permissions and only <b>proposes</b> changes; a person approves them.</span>
    </div>
    <button class="btn-secondary" @click="newChat"><Icon name="plus" :size="15" /> New chat</button>
  </div>

  <div class="assistant">
    <aside class="side stack">
      <div class="card model">
        <div class="card-body stack" style="gap: .45rem">
          <div class="row" style="gap: .5rem">
            <span class="dot-status" :class="status?.model.ready ? 'on' : ready ? 'idle' : 'off'" />
            <b style="font-size: .88rem">{{ status?.model.modelId ?? status?.model.model ?? 'Local model' }}</b>
          </div>
          <span class="muted" style="font-size: .78rem">
            <template v-if="!status">Checking Foundry Local…</template>
            <template v-else-if="!status.enabled">Assistant disabled on this server.</template>
            <template v-else-if="status.model.ready">Loaded on {{ status.model.device ?? 'device' }} · tool calling {{ status.model.supportsToolCalling ? 'supported' : 'unknown' }}</template>
            <template v-else-if="ready">Downloaded; loads on the first question (about a minute).</template>
            <template v-else>{{ status.model.error ?? 'Foundry Local is not available.' }}</template>
          </span>
          <span v-if="status && (status.queue.waiting || status.queue.running)" class="muted" style="font-size: .76rem">{{ status.queue.running }} running · {{ status.queue.waiting }} waiting</span>
          <button v-if="session.can('platformAdmin') && status?.enabled && !status.model.ready && ready" class="btn-secondary btn-sm" @click="loadModel"><Icon name="download" :size="13" /> Load model now</button>
        </div>
      </div>
      <div class="card">
        <div class="card-header"><h3 class="card-title" style="font-size: .9rem">Conversations</h3></div>
        <div class="list conv">
          <div v-for="c in conversations" :key="c.id" class="list-item" :class="{ active: current?.id === c.id }" @click="open(c.id)">
            <div class="list-main"><div class="list-title">{{ c.title }}</div><div class="list-sub">{{ fmt.ago(c.updatedAtUtc) }} · {{ c.messageCount }} messages</div></div>
            <button class="btn-ghost btn-sm" aria-label="Delete conversation" @click.stop="remove(c)"><Icon name="trash" :size="13" /></button>
          </div>
          <div v-if="!conversations.length" class="list-item muted" style="font-size: .8rem">No conversations yet.</div>
        </div>
      </div>
    </aside>

    <section class="card chat">
      <div ref="scroller" class="messages">
        <div v-if="!current && !live" class="welcome">
          <div class="orb"><Icon name="sparkles" :size="26" /></div>
          <h2>What should we look at?</h2>
          <p class="muted">Answers come from live data through tools. With Ministral 3B a question takes a few seconds; larger thinking models (Qwen 3.5 9B) take minutes. You can watch the agent work.</p>
          <div class="chips">
            <button v-for="s in suggestions" :key="s" class="chip" @click="send(s)">{{ s }}</button>
          </div>
        </div>

        <template v-if="current">
          <div v-for="m in current.messages" :key="m.id" class="msg" :class="m.role">
            <div class="bubble">
              <div class="text">{{ m.content }}</div>
              <div v-if="m.error" class="alert bad" style="margin-top: .5rem"><Icon name="alertCircle" :size="15" /><span>{{ m.error }}</span></div>
              <div v-if="m.tools?.length" class="tools">
                <details v-for="(t, i) in m.tools" :key="i" class="tool" :class="{ failed: t.failed }">
                  <summary><Icon :name="t.failed ? 'x' : 'check'" :size="12" /> <code>{{ t.name }}</code> <span class="muted">{{ argText(t.arguments) }}</span> <span class="muted ms">{{ t.durationMs }} ms</span></summary>
                  <pre>{{ t.result }}</pre>
                </details>
              </div>
              <details v-if="m.reasoning" class="reasoning"><summary>Reasoning</summary><div>{{ m.reasoning }}</div></details>
              <div v-if="m.role === 'assistant' && m.durationMs" class="meta">{{ fmt.duration(m.durationMs) }}<template v-if="m.outputTokens"> · {{ fmt.n(m.inputTokens) }} in / {{ fmt.n(m.outputTokens) }} out tokens</template></div>
            </div>
          </div>
        </template>

        <div v-if="live" class="msg assistant">
          <div class="bubble live-bubble">
            <div class="live-head">
              <span class="pulse-dot" />
              <span>{{ live.status }}</span>
              <span class="spacer" />
              <span class="muted mono">{{ mmss(elapsed) }}</span>
              <button class="btn-ghost btn-sm" @click="cancel"><Icon name="x" :size="13" /> Stop</button>
            </div>
            <div v-if="live.tools.length" class="tools">
              <div v-for="(t, i) in live.tools" :key="i" class="tool live-tool" :class="{ failed: t.failed, pending: !t.done }">
                <Icon :name="!t.done ? 'refresh' : t.failed ? 'x' : 'check'" :size="12" :class="{ spin: !t.done }" />
                <code>{{ t.name }}</code> <span class="muted">{{ argText(t.arguments) }}</span>
                <span v-if="t.done" class="muted ms">{{ t.ms }} ms</span>
              </div>
            </div>
            <details v-if="live.reasoning" class="reasoning" :open="showReasoning" @toggle="showReasoning = ($event.target as HTMLDetailsElement).open">
              <summary>Thinking</summary><div>{{ live.reasoning.slice(-2500) }}</div>
            </details>
            <div v-if="live.text" class="text">{{ live.text }}<span class="caret" /></div>
          </div>
        </div>

        <div v-if="current?.proposals.length" class="proposals">
          <div class="muted" style="font-size: .78rem; margin-bottom: .4rem">Proposed actions — nothing happens until someone with permission approves</div>
          <div v-for="p in current.proposals" :key="p.id" class="proposal">
            <div class="list-main">
              <div class="list-title">{{ p.targetLabel }}</div>
              <div class="list-sub">{{ p.reason }}<template v-if="p.result"> · {{ p.result }}</template></div>
            </div>
            <span class="badge" :class="proposalTone(p.status)">{{ p.status }}</span>
            <template v-if="p.status === 'Pending'">
              <button class="btn-primary btn-sm" :disabled="deciding === p.id" @click="decide(p, true)"><Icon name="check" :size="13" /> Approve</button>
              <button class="btn-ghost btn-sm" :disabled="deciding === p.id" @click="decide(p, false)">Reject</button>
            </template>
          </div>
        </div>
      </div>

      <form class="composer" @submit.prevent="send()">
        <textarea v-model="input" rows="2" maxlength="2000" placeholder="Ask about orders, imports, approvals, products or sales…" :disabled="!!live"
          @keydown.enter.exact.prevent="send()" />
        <button class="btn-primary" type="submit" :disabled="!input.trim() || sending || !!live || !status?.enabled">
          <Icon :name="sending ? 'refresh' : 'arrowRight'" :size="15" :class="{ spin: sending }" /> Ask
        </button>
      </form>
    </section>
  </div>
</template>

<style scoped>
.assistant { display: grid; grid-template-columns: 280px minmax(0, 1fr); gap: 1rem; align-items: start; }
@media (max-width: 900px) { .assistant { grid-template-columns: 1fr; } }
.side { position: sticky; top: 4.5rem; }
.dot-status { width: 9px; height: 9px; border-radius: 50%; background: var(--muted); }
.dot-status.on { background: var(--good); box-shadow: 0 0 0 4px var(--good-soft); }
.dot-status.idle { background: var(--warn); box-shadow: 0 0 0 4px var(--warn-soft); }
.dot-status.off { background: var(--bad); }
.conv { max-height: 52vh; overflow: auto; }
.conv .list-item { cursor: pointer; }
.conv .list-item.active { background: var(--primary-soft); }
.chat { display: flex; flex-direction: column; height: calc(100vh - 11rem); min-height: 520px; }
.messages { flex: 1; overflow: auto; padding: 1.2rem; display: flex; flex-direction: column; gap: .9rem; }
.welcome { margin: auto; text-align: center; max-width: 560px; display: grid; gap: .6rem; justify-items: center; }
.orb { width: 64px; height: 64px; border-radius: 50%; display: grid; place-items: center; color: #fff;
  background: radial-gradient(circle at 30% 30%, #a5b4fc, #6366f1 45%, #0e7490); animation: breathe 3.5s ease-in-out infinite; }
@keyframes breathe { 0%, 100% { box-shadow: 0 0 0 0 rgb(99 102 241 / 35%); transform: scale(1); } 50% { box-shadow: 0 0 0 16px rgb(99 102 241 / 0%); transform: scale(1.04); } }
@media (prefers-reduced-motion: reduce) { .orb { animation: none; } }
.chips { display: flex; flex-wrap: wrap; gap: .45rem; justify-content: center; }
.chip { border: 1px solid var(--border); background: var(--surface-2); border-radius: 999px; padding: .4rem .8rem; font-size: .82rem; cursor: pointer; color: var(--text); }
.chip:hover { border-color: var(--primary); background: var(--primary-soft); }
.msg { display: flex; }
.msg.user { justify-content: flex-end; }
.bubble { max-width: min(760px, 92%); border-radius: 14px; padding: .7rem .9rem; background: var(--surface-2); border: 1px solid var(--border); }
.msg.user .bubble { background: var(--primary); color: var(--on-primary); border-color: transparent; }
.text { white-space: pre-wrap; line-height: 1.5; }
.meta { font-size: .72rem; color: var(--muted); margin-top: .4rem; }
.tools { display: grid; gap: .25rem; margin-top: .5rem; }
.tool { font-size: .78rem; border: 1px solid var(--border); border-radius: 8px; padding: .25rem .5rem; background: var(--surface); }
.tool summary { cursor: pointer; display: flex; gap: .4rem; align-items: center; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; }
.tool.failed { border-color: var(--bad); }
.tool pre { white-space: pre-wrap; word-break: break-word; font-size: .74rem; max-height: 220px; overflow: auto; margin: .4rem 0 .1rem; }
.live-tool { display: flex; gap: .4rem; align-items: center; }
.live-tool.pending { border-style: dashed; }
.ms { margin-left: auto; }
.reasoning { margin-top: .5rem; font-size: .78rem; color: var(--text-secondary); }
.reasoning summary { cursor: pointer; color: var(--muted); }
.reasoning div { white-space: pre-wrap; max-height: 220px; overflow: auto; border-left: 2px solid var(--border-strong); padding-left: .6rem; margin-top: .3rem; font-style: italic; }
.live-bubble { min-width: min(560px, 92%); }
.live-head { display: flex; align-items: center; gap: .5rem; font-size: .84rem; }
.pulse-dot { width: 10px; height: 10px; border-radius: 50%; background: var(--primary); animation: pulse 1.4s ease-in-out infinite; }
@keyframes pulse { 0%, 100% { opacity: .35; transform: scale(.85); } 50% { opacity: 1; transform: scale(1.15); } }
.caret { display: inline-block; width: 7px; height: 1em; background: var(--primary); margin-left: 2px; vertical-align: text-bottom; animation: blink 1s steps(2) infinite; }
@keyframes blink { 50% { opacity: 0; } }
.proposals { border-top: 1px dashed var(--border); padding-top: .8rem; }
.proposal { display: flex; align-items: center; gap: .6rem; padding: .5rem .6rem; border: 1px solid var(--border); border-radius: 10px; margin-bottom: .4rem; background: var(--surface); }
.composer { display: flex; gap: .6rem; padding: .8rem; border-top: 1px solid var(--border); align-items: flex-end; }
.composer textarea { flex: 1; resize: none; }
</style>
