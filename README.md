# Catalogue Management System

Learning project implementing the architecture in
`Catalogue_Management_System_Full_Architecture_a11y.pdf`: Vue 3 + TypeScript frontend,
ASP.NET Core 10 modular monolith API, EF Core + SQL Server, async import pipeline
(CSV + simulated EDI), supplier adapters (REST via DummyJSON + fake), Logic Apps
Standard workflows, and Docker Compose for local stack.

## Repository layout

```
src/
  Catalogue.Web/            # ASP.NET Core API (minimal endpoints, JWT auth, RBAC, audit)
  Catalogue.Application/    # use cases, abstractions, import service/processor, validators
  Catalogue.Domain/         # entities, enums, business rules (no external dependencies)
  Catalogue.Infrastructure/ # EF Core, file storage, queues, CSV/EDI parsers, supplier clients
  Catalogue.Workers/        # standalone import worker (Service Bus / queue consumer)
  Catalogue.Contracts/      # shared DTOs + queue message contracts
  Catalogue.ServiceDefaults/ # Serilog, OpenTelemetry, correlation IDs, resilience, ProblemDetails, health
  Catalogue.Gateway/        # YARP edge gateway (routing, LB, health checks, rate limits, timeouts)
  Catalogue.Agents/         # Microsoft Agent Framework: operations assistant + review workflow on Foundry Local
web/catalogue-ui/           # Vue 3 + TypeScript SPA
logicapps/                  # Azure Logic Apps Standard workflows (local runnable)
samples/                    # CSV/EDI fixtures (samples/edi/large = generated, git-ignored)
infra/servicebus/           # Service Bus emulator queue configuration
infra/eventhubs/            # Event Hubs emulator config (hub catalogue-edi, 8 partitions)
tools/Catalogue.EdiGenerator/ # CLI generating multi-million line PRICAT interchanges
tests/                      # xUnit unit + worker tests
docs/api/                   # API guide: every endpoint, request flow, minimal API vs controller side by side
Dockerfile / Dockerfile.worker / docker-compose.yml / .github/workflows/ci.yml
```

## Quick start (local, no Docker)

Prereqs: .NET 10 SDK, Node 22, SQL Server LocalDB.

```powershell
dotnet run --project src/Catalogue.Web --urls http://localhost:5080
cd web/catalogue-ui; npm install; npm run dev   # http://localhost:5173
```

For product images and documents in Blob, start Azurite first (`docker compose up -d azurite`,
or the VS Code Azurite extension). Its data lives in **`./.azurite`** inside this repo. Without
Azurite, the API falls back to disk under **`./.storage`**, and the startup log says which one it chose.

Sign in at http://localhost:5173 with a seeded demo user. The passwords are deliberately weak and
dev-only; new accounts must meet the password policy.

| User | Password | Company | Can |
|---|---|---|---|
| admin | admin123 | Catalogue Direct Ltd (platform owner) | Everything, including approving companies |
| support | support123 | Catalogue Direct Ltd | Imports, reprocessing |
| supplier | supplier123 | Catalogue Direct Ltd | Catalogue editing, media |
| buyer | buyer123 | BriteHeat Installers Ltd | Buy (basket, orders) |
| seller | seller123 | Northwest Distribution Ltd | Buy **and** sell (seller portal) |
| hvx | hvx12345 | HVAC Express Ltd | Sell |
| pennine | pennine123 | Pennine Plumbing Supplies | Nothing yet: awaiting approval (login → 403) |

The API runs the import worker in-process by default (`Worker:InProcess=true`), so
uploading a CSV/EDI file from the Imports page is processed immediately. Swagger UI:
http://localhost:5080/swagger.

## Full stack with Docker Compose

```powershell
docker compose up --build
```

- API: http://localhost:8080 · Web UI: http://localhost:8081
- SQL Server 2022, Azurite (Blob), Service Bus emulator (queue `catalogue-imports`,
  duplicate detection + dead-lettering) — see `infra/servicebus/config.json`.
- With `Worker:InProcess=false` + `ServiceBus:Enabled=true`, the standalone
  `worker` container processes imports off the Service Bus queue.

## Event Hub streaming (large-scale EDI ingestion)

Large PRICAT interchanges (millions of line items, up to 2 GB) skip the 25 MB file-import path
and are streamed through **Azure Event Hubs**:

```
 EDI file ──► Producer ──► Event Hub (N partitions) ──► Partition consumers ──► Bulk writer ──► SQL
  (library     reads segment by     chunk events, ≤1 MB      EventProcessorClient,      SqlBulkCopy + MERGE,
   or upload)  segment, cuts        batches, round-robin     checkpoints in Blob,       chunk ledger makes
               UNH..UNT chunks                               load-balanced              redelivery a no-op
```

- **Producer** (`EdiStreamPublisher`): streams segments in constant memory and cuts self-contained
  `UNH…UNT` chunks of N line items (default 1,000; UI slider 250–5,000).
- **Consumers** (`EventHubIngestionService`): an `EventProcessorClient` per instance. Partition
  ownership is balanced across every API/worker in the consumer group, so scaling the worker out
  scales ingestion. Checkpoints live in Azurite/Blob.
- **Bulk writer** (`SqlBulkCatalogueWriter`): one transaction per chunk. It bulk-copies rows into a temp
  table and applies set-based `MERGE`s for products, price history, inventory and categories. A
  `(ImportId, ChunkIndex)` ledger row in the same transaction means a redelivered event changes nothing
  (at-least-once delivery, applied effectively once). Hot tables use row locks only (no page locks, no
  escalation), so 8 partitions can write in parallel without deadlocking.
- **Live console** (`/event-hub` in the UI): rows/s, events in vs out, lag and ownership per partition,
  p50/p95 end-to-end latency, progress and ETA for active streams, a live event feed, stream history.

Measured locally (LocalDB on a laptop): **5,000,000 rows in 3m17s (~25K rows/s)** on the in-memory hub,
and **1,000,000 rows in 35s (~30K rows/s)** through the Event Hubs emulator with 8 partitions.

### Modes

| `EventHub:Enabled` | Hub | Notes |
|---|---|---|
| `false` (default) | In-memory, `EventHub:PartitionCount` partitions | Works with plain `dotnet run`; bounded partitions give real back-pressure and lag |
| `true` | Azure Event Hubs / emulator | Needs `ConnectionStrings:EventHub` + `ConnectionStrings:CheckpointStorage` |

Run the API against the emulator. Host port 5673 lets it run next to the Service Bus emulator:

```powershell
docker compose up -d azurite eventhubs
$env:EventHub__Enabled = "true"
$env:ConnectionStrings__EventHub = "Endpoint=sb://localhost:5673;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true"
$env:ConnectionStrings__CheckpointStorage = "UseDevelopmentStorage=true"
dotnet run --project src/Catalogue.Web --urls http://localhost:5080
```

`docker compose up --build` wires all of this up automatically for the containerised API.

### Large EDI sample files

The UI lists the library folder (`EventHub:SourceDirectory`, default `samples/edi`). Generate
interchanges with the CLI, or with the **Generate** button on the Event Hub page (up to 10M items):

```powershell
dotnet run -c Release --project tools/Catalogue.EdiGenerator -- 100000  samples/edi/large/pricat-100k.edi
dotnet run -c Release --project tools/Catalogue.EdiGenerator -- 1000000 samples/edi/large/pricat-1m.edi
dotnet run -c Release --project tools/Catalogue.EdiGenerator -- 5000000 samples/edi/large/pricat-5m.edi 0.003
```

Files contain UNB/UNH envelopes and `LIN/IMD/PRI/QTY` items across 10 categories. About 0.3–0.4% of
rows are deliberately invalid (negative price or quantity, unsupported currency, unknown category,
missing name), so the diagnostics have something to show. That's about 93 bytes per line item:
1M ≈ 89 MB, 5M ≈ 444 MB.

### Event Hub API

| Endpoint | Purpose |
|---|---|
| `GET /api/eventhub/overview` | Live telemetry: rates, partitions, lag, timeline, recent events, runs |
| `GET /api/eventhub/sources` · `POST /api/eventhub/sources/generate` | List the library / generate a sample |
| `POST /api/eventhub/streams` | Stream a library file `{ supplierId, source, chunkSize }` |
| `POST /api/eventhub/streams/upload` | Multipart upload (≤ 2 GB), then stream |
| `POST /api/eventhub/streams/{id}/restream` | Replay a streamed import (also via `/imports/{id}/reprocess`) |

## UI

A Vue 3 SPA with a collapsible sidebar, breadcrumbs, `Ctrl+K` catalogue search, a light/dark/system
theme, toasts, and navigation that adapts to the user's role.

- **Dashboard**: KPIs, rows ingested per day, live ingestion rate, import health, top categories, activity.
- **Catalogue**: server-side paging and sorting over millions of rows. Prefix search is indexed; *Deep
  search* opts into substring matching. Also filters, a product drawer with price history, and CSV export.
- **Suppliers**: card and table views with product/import counts, plus a register-supplier dialog.
- **Imports**: drag-and-drop upload, status tabs, live progress, reprocess. **Import detail** adds a
  timeline, errors grouped by field, a paged/searchable error table and CSV export.
- **Event Hub**: the live streaming console described above.
- **Audit log**: a filterable, paged audit trail with correlation IDs.

## HVAC / B2B catalogue domain

The architecture document rates *"HVAC attributes, supplier/customer pricing, availability, business rules"*
as High criticality. The domain implements it in four parts:

### 1. Typed specifications per category
- `AttributeDefinition` (Number / Enum / Text / Boolean, unit, allowed values, min–max). Seeded with 28 HVAC
  specifications such as output (kW), refrigerant (R32, R290, …; R22 rejected), SCOP, ErP energy class,
  phase, boiler type, fuel, radiator ΔT50 output, pipe size and IP rating.
- `CategoryAttribute`: each category's template, with required/optional flags. Admins edit it on
  **Categories & specs**.
- `ProductAttributeValue`: typed values (text / number / bool columns) with
  `(AttributeId, NumberValue)` and `(AttributeId, TextValue)` indexes, so filters seek instead of scanning.
- Every import row (CSV `attr.CODE` columns, EDI `CAV+CODE:value`) and every editor save is validated
  against the category template: unknown specs, specs not in the category, out-of-range values and
  disallowed enum values become row errors. The catalogue gets a facet panel per category (enum
  checkboxes with counts, numeric ranges), and an admin "Missing specs" filter flags incomplete products.

### 2. Brand, MPN, GTIN and supplier offers
- Products carry `Brand`, `ManufacturerPartNumber` and `Gtin` (GS1 check digit validated, normalised to GTIN-14).
- `SupplierOffer` holds each supplier's SKU for a product, plus cost, lead time, MOQ, pack size and availability.
- **Consolidation:** an incoming row matches, in order, (1) this supplier's existing offer, (2) a product
  with the same GTIN owned by another supplier, which gets an *offer* added instead of a duplicate
  product, (3) this supplier's own SKU. Product content is owned by the supplier that created it; stock
  stays per supplier and is summed.
- `Availability` (InStock / LowStock / OnOrder / Discontinued) is derived from stock and lead time. The
  catalogue shows the best availability across offers.

### 3. B2B pricing
- `PriceList` (Trade / Tier / Customer / Promotion) has a discount % off list and a **fallback parent**
  (customer → tier → trade). `PriceListEntry` holds explicit net prices with **quantity breaks** and
  validity dates. `CustomerAccount` links an account to its list.
- `PriceResolver`: the highest qualifying break on the first list in the chain that has entries wins;
  otherwise the first discount in the chain applies; otherwise list price.
- The catalogue, product drawer and **Pricing** page show net prices "as" a customer. The demo
  `buyer` login is linked to ACC-1001 (BriteHeat), so buyers see their own prices automatically.

### 4. Category tree + approval workflow
- Two-level tree (Heating / Cooling & Ventilation / Renewables / Installation & Tools) with move (cycle-checked),
  rename, reorder and activate. Filtering by a parent includes its children.
- Per-supplier **approval policy**: hold *new products*, and/or hold *list-price moves above X%*.
  Held new products are `PendingApproval` (hidden until approved). Held prices leave the current price
  live and create a `ProductChangeRequest`.
- The **Approvals** page offers single and bulk approve/reject with an audit note, plus "approve all
  matching", which works through very large queues 20k per call. Both import pipelines apply the same
  rules, set-based in SQL for the Event Hub path.

### 5. Accounts, companies and sign-up
- `POST /api/auth/register` creates a company (buyer, seller or both) plus its first user (OrgAdmin).
  The company stays *PendingVerification* until a platform admin approves it under **Governance → Companies**.
  Approval creates its Supplier (to sell) and/or CustomerAccount on the TRADE price list (to buy).
- Passwords are PBKDF2-hashed (ASP.NET Core Identity hasher), with ≥ 10 characters, upper, lower and a digit,
  and must not contain the email name. Accounts lock for 15 minutes after 5 failures. Register is rate-limited (5/hour/IP).
- Sessions use a 30-minute JWT held in memory by the SPA, plus a rotating refresh token in an httpOnly,
  SameSite=Strict cookie scoped to `/api/auth`. Only token hashes are stored. Replaying a used refresh token revokes
  the whole session family. Password change, suspension and disabling invalidate all sessions (security stamp).
- Outside Development the API refuses to start without `Jwt:SigningKey` (≥ 32 bytes).

### 6. B2B marketplace: sell, compare, buy
- **Sell:** sellers manage their offers (price, stock, lead time, MOQ, pack) in the **Seller portal**, see where a
  competitor is cheaper, list new products (consolidated by GTIN when the product already exists) and give
  discounts to one buyer or all buyers.
- **Compare:** every offer for a product is priced for the signed-in buyer. Pricing starts from the seller's price
  (falling back to list price), applies the buyer's tier/trade discount, then that seller's discount for the buyer.
  Quantities round up to MOQ and pack size, so the cheapest offer is chosen by **line total**. A company never buys
  from its own offer.
- **Buy:** the basket is re-priced on the server and becomes **one purchase order per supplier**
  (Submitted → Accepted/Rejected → Shipped, buyer can cancel before shipping). Prices are frozen on the PO.
- **Pay:** see *Payments* below. An order not paid online settles on the buyer's trade account.

### 6a. Payments
A buyer can pay any live order (Submitted or Accepted) from the order drawer. **Marketplace → Payments** lists payments
made and received, with totals.

| Method | When it is offered | Money moves? |
|---|---|---|
| **Test payment (simulated)** | `Payments:Simulated:Enabled=true` (on in `appsettings.Development.json` only) | **No.** An in-app checkout with Approve / Decline / Cancel only updates the database. |
| **Card via Stripe** | `Payments:Stripe:SecretKey` is set | With `sk_test_...` keys, no: Stripe's free test mode, test cards only. |
| On account | Always (default) | Settled outside the platform |

How it is kept safe and correct:
- The amount comes from the stored order, never the browser. Stripe gets the amount in minor units, with an
  `Idempotency-Key` per payment, so a retried request cannot create two checkouts or two refunds.
- Card details never touch this app: buyers pay on Stripe's hosted Checkout page.
- Every attempt is a `Payments` row (Pending → Succeeded/Failed/Cancelled → Refunded). A filtered unique index
  (`IX_Payments_OneSuccessPerOrder`) makes it impossible for one order to be paid twice. Confirmations are idempotent,
  and a double click reuses the pending attempt.
- Rejecting or cancelling a paid order refunds it automatically through the same provider. If the provider refuses
  the refund, the order is **not** changed and the user sees the error.
- Stripe confirmation arrives two ways. The webhook `POST /api/payments/webhooks/stripe` is verified with the
  `Stripe-Signature` HMAC and a 5-minute replay window. When the buyer returns from Checkout, the app also asks
  Stripe directly, so payments complete locally even without webhook forwarding.
- Live keys (`sk_live_...`) and publishable keys are refused at startup unless `Payments:Stripe:AllowLiveKeys=true`.

**Turn on Stripe test mode** (free, no card required to sign up):
1. Create a Stripe account, stay in **Test mode**, and copy the secret key from *Developers → API keys* (`sk_test_...`).
2. Store it outside the repo:
   ```powershell
   dotnet user-secrets --project src/Catalogue.Web set "Payments:Stripe:SecretKey" "sk_test_..."
   ```
3. Optional, for webhooks locally (Stripe CLI): `stripe listen --forward-to localhost:5080/api/payments/webhooks/stripe`,
   then set the printed `whsec_...` as `Payments:Stripe:WebhookSecret` the same way.
4. Restart the API. Pay an order with test card `4242 4242 4242 4242` (any future expiry, any CVC);
   `4000 0000 0000 0002` is declined.

Razorpay (INR-first) would be another `IPaymentGateway` implementation. Paying sellers out of a platform account would
need Stripe Connect; today the platform collects and sellers are settled outside the app.

### 7. Product images and documents
- Stored in a private Blob container (`catalogue-media`) with thumbnails, served through short-lived
  HMAC-signed URLs (`/api/media/{id}/t?exp&sig`).
- Files are accepted by their bytes, not their names: JPEG, PNG, WebP, GIF ≤ 10 MB and PDF ≤ 50 MB. SVG and HTML
  are refused. Files are de-duplicated by SHA-256.
- EDI price files carry links at most, so media arrives three ways:
  1. Uploads on the product page.
  2. Zip media packs (**Integration → Media library**), matched by GTIN → MPN → SKU from the file name
     (`5060000000009.png`, `5060000000009_2.png`, `ECOTEC-30_manual.pdf`).
  3. Links in feeds (CSV `image_url` / `datasheet_url` / `manual_url`, EDI `IMD+F+IMG` / `IMD+F+DOC`), downloaded in
     the background with retries. Links to private/loopback addresses are blocked (SSRF).
- Demo packs: `samples/media/hvac-media-pack-300.zip` (items 0–299) and `hvac-media-pack-300-b.zip` (300–599).
  Generate more with `dotnet run --project tools/Catalogue.EdiGenerator -- media <count> [out.zip] [startIndex]`.

### 8. AI operations agent (Microsoft Agent Framework + Foundry Local)
Runs entirely on this machine: no cloud model and no API key. It uses the model you downloaded with Foundry Local
(default `ministral-3-3b-instruct-2512`; `qwen3.5-9b` also works; `foundry cache list` shows what you have).

- **Assistant** (*Intelligence → Assistant*): a `ChatClientAgent` that answers with live data through tools:
  - `operations_overview`, `find_products` and `product_details` (with the buyer's best price);
  - `list_orders` and `order_details`;
  - `list_imports` and `pending_approvals`;
  - `business_metrics`;
  - `propose_action`.

  Conversations persist the agent session, so a chat continues across restarts.
- **Operations review** (*Intelligence → Operations review*, every 12 h and on demand): an Agent Framework **workflow**
  of four executors, `collect-signals → triage → briefing → publish`.
  - Signals come from every area (imports, approvals, orders, payments, companies, catalogue, media).
  - Rules turn them into findings.
  - The agent writes the briefing. If the model is missing or slow, a rule-based briefing is used, so a review always completes.
  - Suggested fixes become proposals.
- **Human in the loop:** the agent never changes anything. `propose_action` and the review create *proposals*
  (reprocess import, approve or reject a change, accept, reject, ship or cancel an order). Proposals are stored and expire
  after 3 days. They run only when someone with permission approves them. The approver is re-checked and the same
  services as the normal endpoints do the work.
- **Least privilege:** tools are filtered by the signed-in user's real API policies. A buyer gets no import or approval
  tools, and a seller can only propose actions on their own orders. Tool output is treated as data, not instructions
  (prompt-injection guard). Every turn is audited.

How it is tailored to a local model on a laptop GPU:

| Finding on this machine | What the code does |
|---|---|
| Foundry Local returns tool calls as **text**, not OpenAI `tool_calls`. Qwen: `<tool_call><function=…>` after a think block. Ministral: `list_orders{"status": "Submitted"}list_imports{…}` | `TextToolCallChatClient` parses both dialects (and Hermes JSON) into `FunctionCallContent` / `TextReasoningContent`, streaming included. Bare calls are only accepted for tools that were offered |
| Ministral's chat template drops the connection when an assistant turn has `content: null` (what the OpenAI SDK sends for tool-call-only turns) | `FoundryLocalChatClient.Normalize` gives such turns an explicit text before sending |
| Thinking models (Qwen 3) reason first; instruct models (Ministral) answer at once | `Agent:FoundryLocal:Thinking` = `auto` detects the family by name, so reasoning is shown separately and answers stream from the first token |
| Foundry reports finish reason `stop` even when it cut the output at the token limit | Truncation is detected from the token count: unfinished reasoning is never shown as an answer |
| Port changes every start; model must be loaded | `FoundryLocalRuntime` finds the endpoint (`foundry server status`), starts the service, resolves the alias to the model id, and runs `foundry model load` on first use |
| Prompt tokens are re-read on every call | A tool router sends only relevant tools per question, history is capped (8 messages), and tool results are compact JSON capped at 1,800 characters |
| Small models paraphrase enum values ("pending") and vary argument shapes | Tools normalise synonyms and return the valid values when an input is wrong. The briefing tool accepts strings, objects or one text for its priorities |
| One GPU | Runs are queued one at a time, with the queue position shown. Results stream over Server-Sent Events (replayable) with a 20-minute timeout and cancel |

**Models measured on this laptop** (RTX 4050, 6 GB):

| | `ministral-3-3b-instruct-2512` (default) | `qwen3.5-9b` |
|---|---|---|
| Size / fits in GPU memory | 3.6 GB / yes (4.7 of 6.1 GB) | 7.0 GB / no (spills into RAM) |
| Load time | 24 s | about 40 s, and it put the system under memory pressure |
| Two-tool question, end to end | **5 s** (tool calls at 2.6 s, answer streams from 3.2 s) | 4–5 min |
| Operations review briefing | **23 s**, written by the model | did not finish within the budget (rule-based fallback) |

To switch, set `Agent:FoundryLocal:Model` (thinking mode is detected automatically). Telemetry: Agent Framework and
Microsoft.Extensions.AI spans (`Catalogue.Agents`) appear in the Aspire dashboard. `/health/ready` reports the model as
*Degraded*, never *Unhealthy*, because the catalogue works without it.

### 9. Analytics dashboard
*Intelligence → Analytics* covers everything in the product. Filters: Today (hourly), 7 days, 15 days, 1 month, 3 months
(daily), 6 months (weekly), 1 year (monthly).
- **KPI tiles** compare with the equally long period before: orders, order value, average order, payments, refunds,
  products added, media, rows ingested and rejected, changes decided, new companies.
- **Charts:**
  - "breathing" line and area charts with gradient fills, for orders and value, payments, ingestion, approvals and activity;
  - gradient bar charts, for catalogue growth, top sellers, top products, categories, offers per supplier and company registrations;
  - donut and pie charts, for order, payment, import, change, availability and company status, payment methods and media types;
  - radial gauges, for image coverage and approval rate.
- Platform staff see the whole platform. A company sees its own orders and payments plus the shared catalogue.
- Aggregation runs in SQL. Per-day product counts for 7.2M products are computed once and cached for 10 minutes, so
  switching period takes under a second after the first load.

### Sample data
`tools/Catalogue.EdiGenerator` now emits full HVAC items: `PIA` MPN/GTIN, `IMD+F+BRN` brand,
`CAV` specs, `PRI+AAB` cost, `QTY+53/52` MOQ/pack, `DTM+169` lead time and `STS+DIS` discontinued,
with EDIFACT `?` escaping (e.g. `A?+?+?+`). Item identity and specs are a function of the item index,
so `startIndex` controls overlap: the same range for two suppliers consolidates; a disjoint range creates new products.

```powershell
dotnet run -c Release --project tools/Catalogue.EdiGenerator -- 100000 samples/edi/large/hvac-100k.edi 0.004 7
dotnet run -c Release --project tools/Catalogue.EdiGenerator -- 100000 samples/edi/large/hvac-100k-alt-prices.edi 0.004 99   # same items, other prices
dotnet run -c Release --project tools/Catalogue.EdiGenerator -- 50000 samples/edi/large/hvac-new-range-50k.edi 0.004 21 5000000  # new items
```

## Platform concerns (logging, tracing, caching, resilience, gateway)

Shared wiring lives in `src/Catalogue.ServiceDefaults` (Aspire "ServiceDefaults" style). The API, worker
and gateway each call `builder.AddServiceDefaults("<service>")` and `app.UseServiceDefaults()`.

| Concern | Implementation |
|---|---|
| **Correlation ID** | `CorrelationIdMiddleware` accepts or creates an `X-Correlation-ID` (validated) and echoes it plus `traceparent`. It becomes ambient through `CorrelationContext` (AsyncLocal), and then appears in every log event, every span (`correlation.id` tag and baggage), outbound HttpClient calls, Service Bus messages, Event Hub event properties, `ImportFile`/`AuditEvent` rows, and every ProblemDetails response. The SPA sends one per request and shows its reference in error toasts. |
| **Logging** | Serilog with structured events, enriched with CorrelationId, TraceId/SpanId, machine, thread, app and environment. Readable console in Dev, compact JSON in containers (`Logging:Json`), rolling JSON files (`Logging:File:Path`, 14 days / 100 MB), an OTLP sink when an endpoint is set, and request logging that records the user, client IP and slow requests. |
| **OpenTelemetry** | Traces for ASP.NET Core, HttpClient, SqlClient, Azure SDKs (Event Hubs/Service Bus/Blob), YARP, and custom `Catalogue.Ingestion` spans. Trace context crosses the hub: `http request → eventhub.stream.publish → eventhub.chunk.process`, and `import.file.process` for file imports. Metrics come from runtime, ASP.NET Core, HttpClient, Polly, rate limiting and HybridCache, plus custom counters/histograms (rows ingested/rejected, chunks, chunk duration, end-to-end latency). Exported via OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. |
| **Global exception handling** | `GlobalExceptionHandler` (`IExceptionHandler`) returns RFC 7807 ProblemDetails. It maps NotFound→404, InvalidOperation→409, Argument→400, circuit open / timeout / SQL outage→503 + `Retry-After`, and client abort→499. Responses always carry `correlationId` + `traceId`; stack details only appear in Development. |
| **Caching** | `HybridCache`: in-process L1 + Redis L2 (`ConnectionStrings:Redis`), stampede protection, tag invalidation on writes. It caches categories (10 min), suppliers (30 s), product detail (5 min), the dashboard (15 s), and expensive `COUNT(*)` totals per product filter (1 min). Redis is wrapped in a timeout + **circuit breaker** (`ResilientDistributedCache`): if Redis dies, the cache fails open and requests carry on from L1 + the database. |
| **Fault tolerance** | EF Core `EnableRetryOnFailure`. Polly pipeline on the bulk writer (jittered retry → circuit breaker → timeout). Event Hub consumers wait out transient outages without dead-lettering, and only poison chunks are dead-lettered after 5 attempts. Explicit exponential retry policies on the Event Hubs, Service Bus and Blob SDKs. `AddStandardResilienceHandler` (retry, breaker, timeouts) on every HttpClient. Request timeouts (30 s default, 15 min for uploads). Login rate limit. Readiness checks (`/health/ready`: database, Event Hub as *Degraded*) and `/health/live`. The SPA retries idempotent GETs on 408/429/5xx with backoff and honours `Retry-After`. |
| **YARP gateway** | `src/Catalogue.Gateway` is the single entry point (`:5000` dev, `:8000` compose). It routes `/api` to the API cluster (power-of-two-choices load balancing, active `/health/live` + passive health checks) and everything else to the SPA. It also handles per-client sliding-window rate limits (stricter on `/api/auth`), per-route timeouts, the 2 GB body limit on upload routes only, correlation-ID creation, and tracing. |

### Running it locally

```powershell
docker compose up -d redis otel-dashboard            # optional: L2 cache + telemetry UI
$env:ConnectionStrings__Redis = "localhost:6379,abortConnect=false"
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:4317"
dotnet run --project src/Catalogue.Web --urls http://localhost:5080
dotnet run --project src/Catalogue.Gateway           # http://localhost:5000 (API + SPA behind YARP)
cd web/catalogue-ui; $env:API_TARGET="http://localhost:5000"; npx vite   # optional: dev server via gateway
```

Traces, metrics and logs: **http://localhost:18888** (Aspire dashboard). Search by `correlation.id`
to see one user action across gateway → API → Event Hub → consumers. With Docker Compose everything
is pre-wired: gateway `http://localhost:8000`, dashboard `http://localhost:18888`.

## Logic Apps locally

See [logicapps/README.md](logicapps/README.md). Two workflows:

- `import-notify` — HTTP-triggered; returns an import's status to the caller.
- `import-monitor` — every 5 min scans imports and pulls row errors for failures.

Run with Azure Functions Core Tools + Azurite:

```powershell
azurite --silent
cd logicapps; func host start
curl -X POST http://localhost:7071/api/import-notify/triggers/manual/invoke -H "Content-Type: application/json" -d "{`"importId`": 1}"
```

## Architecture highlights

- **Canonical model**: CSV columns and EDI `LIN/PRI/QTY` segments map to
  `CanonicalProductImport`; the domain never sees supplier formats.
- **Idempotent imports**: files are SHA-256 hashed per supplier; re-uploading the
  same bytes returns the existing `ImportFile` without re-queuing.
- **State machine**: `Received → Processing → Completed | CompletedWithErrors | Failed | Rejected`,
  with a startup recovery sweep that flags stranded `Processing` imports for reprocessing.
- **Row-level diagnostics**: validation failures land in `ImportItemError` with row,
  field, message and raw value; the rest of the file still processes.
- **Replaceable queue/storage**: in-memory channel + disk locally; Service Bus +
  Blob under `ServiceBus:Enabled` / `FileStorage:UseBlob`.
- **RBAC**: JWT roles (Admin/SupplierManager/Buyer/Support/Viewer) map to endpoint
  policies; swap dev JWT for Entra ID by changing issuer/authority config.
- **Audit**: product create/update/activate, imports, reprocess actions recorded in
  `AuditEvent` with actor and correlation ID.

## Tests

```powershell
dotnet test   # parsers (CSV/EDI + media links), row validation, idempotency, worker state machine,
              # password policy/lockout, company + PO state machines, marketplace pricing & sourcing,
              # media sniffing, signed URLs, SSRF guard, media-pack matching,
              # payments (idempotency, refunds, webhook signatures, Stripe request shape, live-key guard),
              # agent: Qwen tool-call parsing (incl. split stream chunks), function invocation end to end,
              # Foundry Local discovery/start/load, tool routing & permissions, proposals, run queue/replay,
              # review triage + fallback briefing, analytics periods and buckets
```

## Configuration reference

| Key | Purpose | Default |
|---|---|---|
| `ConnectionStrings:CatalogueDb` | SQL connection | LocalDB |
| `FileStorage:UseBlob` / `ConnectionStrings:BlobStorage` | Blob (Azurite) for raw files and media; disk when unreachable | Blob, `UseDevelopmentStorage=true` |
| `Storage:Root` | Disk fallback root, relative to the app's content root | `../../.storage` (the repo's `.storage`) |
| `Media:SigningKey` | HMAC key for media URLs | falls back to `Jwt:SigningKey` |
| `Payments:Simulated:Enabled` | In-app test checkout (database only, no money) | `false`; `true` in Development |
| `Payments:Stripe:SecretKey` / `WebhookSecret` | Stripe Checkout + webhook verification (use user-secrets) | empty = Stripe off |
| `Payments:Stripe:AllowLiveKeys` | Permit `sk_live_` keys | `false` |
| `Payments:ReturnBaseUrl` | Where Stripe Checkout returns the buyer | `http://localhost:5173` |
| `Agent:Enabled` | Assistant and operations review | `true` |
| `Agent:FoundryLocal:Model` | Foundry Local model alias (or exact id) | `ministral-3-3b-instruct-2512` |
| `Agent:FoundryLocal:Thinking` | `auto`, `true` or `false`: whether the model reasons in a think block first | `auto` |
| `Agent:FoundryLocal:Endpoint` | Fixed loopback endpoint; empty = discover via the `foundry` CLI | empty |
| `Agent:FoundryLocal:AutoStartService` / `AutoLoadModel` | Start Foundry Local / load the model on first use | `true` / `true` |
| `Agent:FoundryLocal:MaxOutputTokens` / `Temperature` / `RequestTimeout` | Generation limits per model call | `1200` / `0.2` / 10 min |
| `Agent:RunTimeout` / `MaxQueue` / `HistoryMessages` / `MaxToolCallsPerRun` | Run limits | 20 min / 20 / 8 / 6 |
| `Agent:Review:Enabled` / `Interval` / `UseModel` / `ModelTimeout` | Scheduled operations review | `true` / 12 h / `true` / 12 min |
| `ServiceBus:Enabled` / `ConnectionStrings:ServiceBus` | SB vs in-memory | in-memory |
| `Worker:InProcess` | run worker inside API (dev) | `true` |
| `Jwt:Issuer` / `Jwt:SigningKey` | auth config | dev values — replace via env vars |
| `SupplierApis:UseDummyJson` | live demo-supplier sync vs fake client | `false` |
| `AllowedOrigins` | CORS for the Vue app | `http://localhost:5173` |
| `EventHub:Enabled` / `ConnectionStrings:EventHub` | Event Hubs vs in-memory hub | in-memory |
| `ConnectionStrings:CheckpointStorage` | Blob store for consumer checkpoints | `UseDevelopmentStorage=true` |
| `EventHub:HubName` / `EventHub:ConsumerGroup` | hub + consumer group | `catalogue-edi` / `catalogue-ingest` |
| `EventHub:PartitionCount` | partitions of the in-memory hub | `4` |
| `EventHub:ConsumerInProcess` | run partition consumers in this process | API `true`, worker `false` |
| `EventHub:SourceDirectory` | server-side EDI library | `../../samples/edi` |
| `ConnectionStrings:Redis` | HybridCache L2 (empty = in-process only) | empty |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | OTLP traces/metrics/logs export | unset (no export) |
| `Logging:Json` / `Logging:File:Path` | JSON console / rolling JSON log file | Dev: text console + `logs/catalogue-*-.json` |
| `ReverseProxy:*` (gateway) | YARP routes/clusters/destinations | API `:5080`, SPA `:5173` |
| `Gateway:RateLimit:PerMinute` | per-client gateway limit | `1200` |

## Known limitations

- EDI parser is a simplified PRICAT-like simulation, not a certified EDI stack.
- `EnsureCreated` is used instead of migrations for the learning build.
- No malware scanning on uploads (noted as production requirement in the doc).
- No email verification or password-reset email (no mail service). Company approval is manual (KYB by an admin).
- The assistant runs one request at a time per API instance (one local GPU). Queued and running agent runs live in that
  process, so a multi-instance deployment would need a shared queue. Conversations, proposals and reviews are in SQL.
- Analytics periods are in UTC.
- Online payment covers the full order amount. There are no partial refunds, invoices or VAT documents, and no seller
  payouts (Stripe Connect).
- Event Hub telemetry is per process. With consumers spread across several workers, each instance
  shows only the partitions it owns. Durable totals are always on the import record.
- Name *substring* search (`deep=true`) scans the Products table. At millions of rows, use prefix
  search or add SQL full-text search.
- `GET /api/imports` and `/errors` still return arrays, because the Logic Apps depend on that shape.
  Totals come back in the `X-Total-Count` header.
