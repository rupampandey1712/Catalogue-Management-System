import axios, { type AxiosResponse, type InternalAxiosRequestConfig } from 'axios'
import { session, type UserProfile } from './session'

export const api = axios.create({ baseURL: '/api', timeout: 60_000 })

const newCorrelationId = () =>
  (crypto.randomUUID?.() ?? `${Date.now().toString(16)}${Math.random().toString(16).slice(2)}`).replace(/-/g, '')

type RetryConfig = InternalAxiosRequestConfig & { __attempt?: number; __refreshed?: boolean }

export interface SessionResponse { token: string; expiresAtUtc: string; user: UserProfile }

// Single-flight refresh: concurrent 401s share one /auth/refresh call.
let refreshing: Promise<boolean> | null = null
export function refreshSession(): Promise<boolean> {
  refreshing ??= axios.post<SessionResponse>('/api/auth/refresh', null, { withCredentials: true })
    .then(r => { session.set(r.data.token, r.data.expiresAtUtc, r.data.user); return true })
    .catch(() => { session.clear(); return false })
    .finally(() => { setTimeout(() => { refreshing = null }, 0) })
  return refreshing
}

/** Restores the session from the refresh cookie on page load (if one may exist). */
export async function restoreSession() {
  if (session.hasSessionHint && !session.isAuthenticated) await refreshSession()
  session.markRestored()
}

/** Refresh shortly before the access token expires so active users never see a 401. */
setInterval(() => {
  if (session.isAuthenticated && session.expiresAt - Date.now() < 2 * 60_000) refreshSession()
}, 30_000)

api.interceptors.request.use(config => {
  const token = session.token
  if (token) config.headers.Authorization = `Bearer ${token}`
  // One ID per user action; reused across retries so the server logs show them as one operation.
  if (!config.headers['X-Correlation-ID']) config.headers['X-Correlation-ID'] = newCorrelationId()
  return config
})

const RETRYABLE = new Set([408, 429, 502, 503, 504])
const sleep = (ms: number) => new Promise(r => setTimeout(r, ms))

api.interceptors.response.use(r => r, async err => {
  const config = err.config as RetryConfig | undefined
  const status: number | undefined = err.response?.status

  // Expired access token: refresh once via the httpOnly cookie and replay the request.
  if (status === 401 && config && !config.url?.includes('/auth/') && !config.__refreshed) {
    config.__refreshed = true
    if (await refreshSession()) return api.request(config)
    window.location.href = '/login?expired=1'
    return Promise.reject(err)
  }

  // Fault tolerance: retry idempotent requests on network errors and transient statuses,
  // with exponential backoff + jitter, honouring Retry-After when the server sends it.
  const idempotent = ['get', 'head', 'options'].includes((config?.method ?? 'get').toLowerCase())
  const transient = !err.response || (status !== undefined && RETRYABLE.has(status))
  const attempt = config?.__attempt ?? 0
  if (config && idempotent && transient && attempt < 2 && err.code !== 'ERR_CANCELED') {
    config.__attempt = attempt + 1
    const retryAfter = Number(err.response?.headers?.['retry-after'])
    const delay = Number.isFinite(retryAfter) && retryAfter > 0
      ? Math.min(retryAfter * 1000, 10_000)
      : 400 * 2 ** attempt + Math.random() * 250
    await sleep(delay)
    return api.request(config)
  }
  return Promise.reject(err)
})

/** Correlation ID of a failed request: from the server's problem details, else the one we sent. */
export function correlationOf(e: any): string | undefined {
  return e?.response?.data?.correlationId
    ?? e?.response?.headers?.['x-correlation-id']
    ?? e?.config?.headers?.['X-Correlation-ID']
}

/** User-facing error message (RFC 7807 problem details aware) with a support reference. */
export function errorText(e: any, fallback = 'Something went wrong.'): string {
  const status = e?.response?.status
  const data = e?.response?.data
  let msg: string
  if (status === 403) msg = 'You do not have permission for this action.'
  else if (status === 429) msg = 'Too many requests — please wait a moment and try again.'
  else if (!e?.response) msg = e?.code === 'ECONNABORTED' ? 'The request timed out.' : 'Cannot reach the server. Check your connection.'
  else msg = data?.detail ?? data?.error ?? data?.title ?? fallback
  const ref = correlationOf(e)
  return status && status >= 500 && ref ? `${msg} (ref ${String(ref).slice(0, 8)})` : msg
}

/** Reads the X-Total-Count header used by array endpoints that stay Logic-App compatible. */
export function totalOf(r: AxiosResponse, fallback = 0): number {
  const raw = r.headers['x-total-count']
  return raw != null ? Number(raw) : fallback
}

export interface Paged<T> { items: T[]; total: number; page: number; pageSize: number }

export type Availability = 'InStock' | 'LowStock' | 'OnOrder' | 'Unknown' | 'Discontinued'
export interface Spec { code: string; name: string; unit?: string; text?: string; number?: number; bool?: boolean; display: string }
export interface Asset {
  id: number; kind: string; title: string; fileName: string; contentType: string; sizeBytes: number
  width?: number; height?: number; isPrimary: boolean; sortOrder: number; source: string; sourceUrl?: string
  uploadedBy: string; uploadedAtUtc: string; url: string; thumbnailUrl?: string
}
export interface OfferQuote {
  offerId: number; productId: number; supplierId: number; supplierCode: string; supplierName: string; supplierSku: string
  isContentOwner: boolean; availability: string; leadTimeDays?: number; minOrderQuantity: number; packSize: number
  requestedQuantity: number; orderQuantity: number; currency: string; basePrice: number; listPrice?: number
  netUnitPrice: number; totalDiscountPercent: number; lineTotal: number; explanation: string
  purchasable: boolean; notPurchasableReason?: string; isCheapest: boolean
}
export interface PricedLine {
  productId: number; productName: string; sku: string; quantity: number; chosen?: OfferQuote
  alternatives: OfferQuote[]; ownerLineTotal?: number; problem?: string
}
export interface BasketQuote {
  lines: PricedLine[]; suppliers: { supplierId: number; supplierCode: string; supplierName: string; currency: string; lines: number; subtotal: number }[]
  total: number; ownerOnlyTotal?: number; savings?: number; currency: string
}
export interface OrderSummary {
  id: number; number: string; status: string; buyerName: string; supplierId: number; supplierCode: string; supplierName: string
  currency: string; subtotal: number; listValue: number; lines: number; buyerReference?: string; createdAtUtc: string; updatedAtUtc: string; createdBy: string
  paymentStatus: 'Unpaid' | 'Pending' | 'Paid' | 'Refunded'
}
export interface PaymentMethod { provider: 'Simulated' | 'Stripe'; name: string; testMode: boolean }
export interface Payment {
  id: number; orderId: number; orderNumber: string; provider: string; status: 'Pending' | 'Succeeded' | 'Failed' | 'Cancelled' | 'Refunded'
  isTest: boolean; amount: number; currency: string; checkoutUrl?: string; paymentReference?: string; refundReference?: string
  failureReason?: string; createdBy: string; createdAtUtc: string; completedAtUtc?: string; refundedAtUtc?: string
  buyerName: string; supplierCode: string; supplierName: string
}
export interface StartPaymentResponse { paymentId: number; provider: string; status: string; amount: number; currency: string; isTest: boolean; redirectUrl?: string }
export interface Organization {
  id: number; name: string; vatNumber?: string; country: string; canBuy: boolean; canSell: boolean; status: string; isPlatformOwner: boolean
  supplierId?: number; supplierCode?: string; customerAccountId?: number; customerAccountNumber?: string; userCount: number
  createdAtUtc: string; reviewedAtUtc?: string; reviewedBy?: string; reviewNote?: string
}
export interface OrgUser { id: number; email: string; username?: string; displayName: string; roles: string[]; status: string; lastLoginUtc?: string; lockoutEndUtc?: string }
export interface SellerOffer {
  id: number; productId: number; sku: string; productName: string; brand?: string; supplierSku: string; price?: number; listPrice?: number
  cost?: number; currency: string; stock?: number; leadTimeDays?: number; minOrderQuantity: number; packSize: number; availability: string
  isContentOwner: boolean; cheapestCompetitorPrice?: number; competitors: number
}

export interface Product {
  id: number; sku: string; name: string; description?: string; status: string
  imageUrl?: string
  price?: number; currency?: string; availableQuantity?: number; categories: string[]
  supplierId?: number; supplierCode?: string; updatedAtUtc?: string
  brand?: string; manufacturerPartNumber?: string; gtin?: string
  availability: Availability; offerCount: number; specs?: Spec[]
  netPrice?: number; priceSource?: string; savingPercent?: number
}
export interface SpecDetail {
  attributeId: number; code: string; name: string; unit?: string; dataType: 'Number' | 'Text' | 'Enum' | 'Boolean'
  allowedValues: string[]; required: boolean; value?: string; display: string
}
export interface Offer {
  id: number; supplierId: number; supplierCode: string; supplierName: string; supplierSku: string
  cost?: number; currency: string; leadTimeDays?: number; minOrderQuantity: number; packSize: number
  availability: Availability; isPreferred: boolean; stockQuantity?: number; updatedAtUtc: string
}
export interface PriceBreak { minQuantity: number; unitPrice: number }
export interface PriceQuote {
  productId: number; currency: string; listPrice?: number; netUnitPrice?: number; quantity: number
  source: string; priceListCode?: string; savingPercent?: number; breaks: PriceBreak[]; lineTotal?: number
}
export interface ProductDetail extends Product {
  categoryIds: number[]; createdAtUtc: string; updatedAtUtc: string
  priceHistory: { currency: string; amount: number; validFromUtc: string; validToUtc?: string }[]
  specs: SpecDetail[]; missingRequired: string[]; offers: Offer[]
  pendingChanges: { id: number; kind: string; summary?: string; oldPrice?: number; newPrice?: number; createdAtUtc: string }[]
  quote?: PriceQuote
  assets: Asset[]
}
export interface Facet {
  code: string; name: string; unit?: string; dataType: string; required: boolean
  values: { value: string; count: number }[]; min?: number; max?: number
}
export interface CategoryNode {
  id: number; code: string; name: string; parentCategoryId?: number; isActive: boolean; sortOrder: number
  productCount: number; attributeCount: number; requiredAttributeCount: number
}
export interface TemplateItem { attributeId: number; code: string; name: string; dataType: string; unit?: string; required: boolean; sortOrder: number; inherited: boolean }
export interface AttributeDef {
  id: number; code: string; name: string; dataType: string; unit?: string; allowedValues: string[]
  minValue?: number; maxValue?: number; isFilterable: boolean; sortOrder: number; description?: string; usedByCategories: number
}
export interface ChangeRequest {
  id: number; productId: number; productSku: string; productName: string; brand?: string
  supplierId: number; supplierCode: string; importId?: number; kind: 'NewProduct' | 'PriceChange'; status: string
  currency?: string; oldPrice?: number; newPrice?: number; changePercent?: number; summary?: string
  createdAtUtc: string; decidedBy?: string; decidedAtUtc?: string; decisionNote?: string
}
export interface PriceListInfo {
  id: number; code: string; name: string; kind: string; currency: string; discountPercent: number
  parentPriceListId?: number; parentCode?: string; isActive: boolean; entryCount: number; customerCount: number
}
export interface PriceEntry { id: number; productId: number; sku: string; productName: string; minQuantity: number; unitPrice: number; listPrice?: number; validFromUtc: string; validToUtc?: string }
export interface CustomerAccount { id: number; accountNumber: string; name: string; priceListId?: number; priceListCode?: string; isActive: boolean }
export interface ImportItem {
  id: number; supplierId: number; supplierCode?: string; fileName: string; format: string; status: string
  totalRows: number; processedRows: number; errorRows: number; failureReason?: string
  receivedAtUtc: string; completedAtUtc?: string; correlationId?: string
}
export interface ImportError { id: number; rowNumber: number; field: string; message: string; rawValue?: string }
export interface Supplier {
  id: number; code: string; name: string; integrationType: string; isActive: boolean; mappingVersion?: string
  productCount: number; importCount: number; lastImportUtc?: string
  requireApprovalForNewProducts: boolean; priceChangeApprovalThresholdPercent?: number; offerCount: number; pendingApprovals: number
}
export interface Category { id: number; code: string; name: string; parentCategoryId?: number; isActive: boolean }
export interface AuditEvent {
  id: number; actorId: string; action: string; entityType: string; entityId: string
  timestampUtc: string; details?: string; correlationId?: string
}

// ----- Event Hub -----
export interface HubInfo {
  mode: 'EventHubs' | 'InMemory'; name: string; consumerGroup: string; namespace: string
  partitionCount: number; consumerInProcess: boolean; connected: boolean; connectionError?: string
}
export interface PartitionView {
  partitionId: string; owned: boolean; events: number; rows: number; errorRows: number; failures: number
  lastEnqueuedSequenceNumber: number; lastProcessedSequenceNumber: number; lag: number
  lastProcessedUtc?: string; lastEnqueuedUtc?: string
}
export interface TimelinePoint { t: number; eventsIn: number; eventsOut: number; rowsOut: number; errors: number }
export interface RecentEvent {
  partitionId: string; sequenceNumber: number; importId: number; chunkIndex: number
  rows: number; errors: number; durationMs: number; latencyMs: number; duplicate: boolean; atUtc: string
}
export interface StreamRun {
  importId: number; fileName: string; supplier: string; state: 'Publishing' | 'Published' | 'Failed'; error?: string
  totalBytes: number; bytesRead: number; chunkSize: number; publishedEvents: number; publishedRows: number
  processedEvents: number; processedRows: number; errorRows: number; startedUtc: string; finishedUtc?: string
}
export interface HubOverview {
  hub: HubInfo
  startedUtc: string
  totals: { publishedEvents: number; publishedRows: number; processedEvents: number; processedRows: number; errorRows: number; duplicateEvents: number; failures: number }
  rates: { eventsInPerSec: number; eventsOutPerSec: number; rowsPerSec: number; peakRowsPerSec: number; latencyP50Ms: number; latencyP95Ms: number }
  totalLag: number
  partitions: PartitionView[]
  timeline: TimelinePoint[]
  recent: RecentEvent[]
  runs: StreamRun[]
}
export interface EdiSource { name: string; sizeBytes: number; modifiedUtc: string; estimatedLineItems: number }
export interface StreamImport {
  id: number; supplierId: number; supplierCode: string; fileName: string; status: string
  totalRows: number; processedRows: number; errorRows: number; receivedAtUtc: string; completedAtUtc?: string; failureReason?: string
}

export interface Dashboard {
  products: { total: number; active: number; inactive: number; draft: number }
  suppliers: { total: number; active: number }
  categories: number
  imports: { total: number; byStatus: Record<string, number>; rowsLast14Days: number; errorsLast14Days: number }
  daily: { day: string; rows: number; errors: number; imports: number }[]
  topCategories: { code: string; name: string; count: number }[]
  recentImports: (ImportItem & { supplierCode: string })[]
  activity: AuditEvent[]
  pendingApprovals: number; multiSourcedProducts: number; pendingProducts: number
  generatedUtc: string
}

// ----- analytics -----
export type AnalyticsRange = 'today' | '7d' | '15d' | '1m' | '3m' | '6m' | '1y'
export interface Kpi { key: string; label: string; value: number; previous: number | null; unit: string; spark: number[] }
export interface ChartSeries { key: string; label: string; values: number[] }
export interface ChartSlice { label: string; value: number }
export interface AnalyticsSnapshot {
  products: number; activeProducts: number; offers: number; suppliers: number; activeCompanies: number; pendingCompanies: number
  users: number; pendingApprovals: number; productsWithImage: number; computedAtUtc: string
}
export interface AnalyticsReport {
  range: AnalyticsRange; fromUtc: string; toUtc: string; bucket: 'Hour' | 'Day' | 'Week' | 'Month'; labels: string[]; scope: 'platform' | 'company'
  kpis: Kpi[]; series: Record<string, ChartSeries[]>; breakdowns: Record<string, ChartSlice[]>; rankings: Record<string, ChartSlice[]>
  snapshot: AnalyticsSnapshot; computedAtUtc: string
}

// ----- assistant -----
export interface AgentStatus {
  enabled: boolean
  model: { model: string; modelId?: string; serviceRunning: boolean; modelCached: boolean; modelLoaded: boolean; ready: boolean; supportsToolCalling?: boolean; device?: string; error?: string; endpoint?: string; checkedAtUtc: string }
  queue: { waiting: number; running: number; myRun?: string }
  limits: { runTimeoutMinutes: number; maxToolCallsPerRun: number; historyMessages: number }
  review: { enabled: boolean; intervalHours: number; useModel: boolean; last?: { id: number; status: string; startedAtUtc: string; completedAtUtc?: string } }
}
export interface ToolTrace { name: string; arguments: string; result: string; failed: boolean; durationMs: number }
export interface AgentMessage {
  id: number; role: 'user' | 'assistant'; content: string; reasoning?: string; error?: string; durationMs?: number
  inputTokens?: number; outputTokens?: number; createdAtUtc: string; tools?: ToolTrace[]
}
export interface Proposal {
  id: number; kind: string; targetId: string; targetLabel: string; reason: string; source: string; conversationId?: string; reviewId?: number
  requestedBy: string; status: 'Pending' | 'Executed' | 'Rejected' | 'Failed' | 'Expired'; decidedBy?: string; decidedAtUtc?: string; result?: string
  createdAtUtc: string; expiresAtUtc: string
}
export interface ConversationSummary { id: string; title: string; messageCount: number; createdAtUtc: string; updatedAtUtc: string }
export interface Conversation extends ConversationSummary { activeRunId?: string; messages: AgentMessage[]; proposals: Proposal[] }
export interface ReviewSummary {
  id: number; trigger: string; requestedBy: string; status: string; startedAtUtc: string; completedAtUtc?: string; durationMs?: number
  critical: number; warnings: number; infos: number; proposalCount: number; generatedByModel: boolean; modelId?: string; error?: string
}
export interface Finding { severity: 'critical' | 'warning' | 'info'; area: string; title: string; detail: string; action?: { kind: string; targetId: string; reason: string } }
export interface ReviewDetail extends ReviewSummary { briefing?: string; findings: Finding[]; proposals: Proposal[] }

export interface RunEvent { seq: number; type: string; data: any }

/**
 * Follows an agent run over Server-Sent Events. fetch (not EventSource) so the bearer token can be sent;
 * reconnects from the last event id if the connection drops while the model is still working.
 */
export async function followRun(runId: string, onEvent: (e: RunEvent) => void, signal: AbortSignal): Promise<void> {
  let last = 0
  for (let attempt = 0; attempt < 20 && !signal.aborted; attempt++) {
    try {
      const token = session.token
      const res = await fetch(`/api/agent/runs/${runId}/events?after=${last}`, { headers: token ? { Authorization: `Bearer ${token}` } : {}, signal })
      if (res.status === 401) { await refreshSession(); continue }
      if (!res.ok || !res.body) throw new Error(`HTTP ${res.status}`)
      const reader = res.body.getReader()
      const decoder = new TextDecoder()
      let buffer = ''
      let done = false
      while (!done) {
        const chunk = await reader.read()
        if (chunk.done) break
        buffer += decoder.decode(chunk.value, { stream: true })
        let sep: number
        while ((sep = buffer.indexOf('\n\n')) >= 0) {
          const block = buffer.slice(0, sep)
          buffer = buffer.slice(sep + 2)
          let id = 0, type = 'message', data = ''
          for (const line of block.split('\n')) {
            if (line.startsWith('id: ')) id = Number(line.slice(4))
            else if (line.startsWith('event: ')) type = line.slice(7)
            else if (line.startsWith('data: ')) data += line.slice(6)
          }
          if (!data) continue
          if (id) last = id
          const e: RunEvent = { seq: id, type, data: JSON.parse(data) }
          onEvent(e)
          if (type === 'done') done = true
        }
      }
      if (done) return
    } catch (err) {
      if (signal.aborted) return
      await new Promise(r => setTimeout(r, Math.min(8000, 1000 * 2 ** attempt)))
    }
  }
}
