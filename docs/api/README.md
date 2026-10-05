# API guide: minimal APIs in this project, and how they compare to controllers

This folder documents **every endpoint** in `src/Catalogue.Web/Endpoints`. There is one document per endpoint file
(each file is one feature area, usually one route group). Each endpoint is described with:

- what it does, who may call it, its inputs and its responses;
- the **flow**: what happens step by step when a request arrives;
- the **minimal API code** as it exists in this project (shortened where noted);
- the **controller equivalent**: the same endpoint written as an MVC controller action, so the two styles can be compared
  line by line. The controller versions are examples for learning; they are not compiled into the project.

| Document | Source file | Base route | Endpoints |
|---|---|---|---|
| [Authentication](01-auth.md) | `AuthEndpoints.cs` | `/api/auth` | 6 |
| [Products](02-products.md) | `ProductEndpoints.cs` | `/api/products` | 7 |
| [Categories & attributes](03-categories.md) | `CategoryEndpoints.cs` | `/api/categories`, `/api/attributes` | 9 |
| [Suppliers](04-suppliers.md) | `SupplierEndpoints.cs` | `/api/suppliers` | 4 |
| [Imports](05-imports.md) | `ImportEndpoints.cs` | `/api/imports` | 6 |
| [Event Hub streaming](06-eventhub.md) | `EventHubEndpoints.cs` | `/api/eventhub` | 7 |
| [Dashboard & audit log](07-dashboard.md) | `DashboardEndpoints.cs` | `/api/dashboard`, `/api/audit` | 3 |
| [Approvals](08-approvals.md) | `ApprovalEndpoints.cs` | `/api/approvals` | 4 |
| [Pricing](09-pricing.md) | `PricingEndpoints.cs` | `/api/pricelists`, `/api/customers`, `/api/pricing` | 10 |
| [Media](10-media.md) | `MediaEndpoints.cs` | `/api/media`, `/api/products/{productId}/assets` | 9 |
| [Organizations](11-organizations.md) | `OrganizationEndpoints.cs` | `/api/organizations` | 8 |
| [Marketplace & seller portal](12-marketplace.md) | `MarketplaceEndpoints.cs` | `/api/marketplace`, `/api/seller` | 12 |
| [Payments](13-payments.md) | `PaymentEndpoints.cs` | `/api/payments`, `/api/marketplace/orders/{id}/pay` | 6 |
| [Assistant, reviews & analytics](14-agent-analytics.md) | `AgentEndpoints.cs` (`AgentEndpoints` + `AnalyticsEndpoints`) | `/api/agent`, `/api/analytics` | 16 |
| **Total** | 14 files | | **107** |

The counts are checked against the source: the 14 files contain 120 `Map…` calls, 13 of them `MapGroup`, which leaves
107 endpoints. Each document lists every endpoint in its file.

---

## 1. What a minimal API is

A minimal API maps an HTTP method and route **directly to a function** (a lambda or a static method). There is no
controller class, no base class and no MVC filter pipeline. ASP.NET Core inspects the function's parameters and decides
where each value comes from.

```csharp
// Minimal API: the route, the handler and its dependencies on a single line.
app.MapGet("/api/suppliers/{id:long}", async (long id, ICatalogueDbContext db, CancellationToken ct) =>
    await db.Suppliers.FindAsync([id], ct) is { } s ? Results.Ok(s) : Results.NotFound())
   .RequireAuthorization("ReadCatalogue");
```

The same endpoint as a controller:

```csharp
[ApiController]
[Route("api/suppliers")]
[Authorize(Policy = "ReadCatalogue")]
public sealed class SuppliersController(ICatalogueDbContext db) : ControllerBase   // dependencies injected into the class
{
    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken ct) =>
        await db.Suppliers.FindAsync([id], ct) is { } s ? Ok(s) : NotFound();
}
```

### Parameter binding: where each value comes from

Minimal APIs infer the source of each parameter. Controllers with `[ApiController]` follow nearly the same rules, but
usually spell them out with attributes.

| Parameter in a minimal API handler | Source | Controller equivalent |
|---|---|---|
| Matches a route segment (`{id}`) | route | `[FromRoute] long id` (implicit for route names) |
| Simple type not in the route (`string? search`, `int? page`) | query string | `[FromQuery] string? search` |
| A record or class (`LoginRequest req`) | JSON body (one per endpoint) | `[FromBody] LoginRequest req` (implicit with `[ApiController]`) |
| A type registered in DI (`ICatalogueDbContext db`) | dependency injection, per request | constructor injection, or `[FromServices]` |
| `HttpContext`, `HttpRequest`, `ClaimsPrincipal`, `CancellationToken` | the current request | `HttpContext` / `Request` / `User` properties, `CancellationToken` parameter |
| `IFormFile`, `IFormFileCollection` | multipart form | `IFormFile file` |
| `[FromHeader(Name = "...")]` | request header | same attribute |

In this project almost every handler asks for its services as parameters (`ICatalogueDbContext db, HybridCache cache,
CancellationToken ct`). Each endpoint therefore shows exactly what it depends on, where a controller's constructor
would list the dependencies of all its actions together.

### Results

A minimal API returns an `IResult` built with the static `Results` (or `TypedResults`) class: `Results.Ok(dto)`,
`Results.NotFound()`, `Results.ValidationProblem(errors)`, `Results.Problem(statusCode: 409, title: "...")`,
`Results.Created(url, dto)`, `Results.Accepted(url)`, `Results.File(stream, contentType)`. A controller uses the
`ControllerBase` helpers: `Ok()`, `NotFound()`, `ValidationProblem()`, `Problem()`, `CreatedAtAction()`, `Accepted()`,
`File()`. They produce the same HTTP responses.

### Cross-cutting behaviour: metadata, not attributes

| Concern | Minimal API (this project) | Controller |
|---|---|---|
| Group of routes with a shared prefix | `var g = api.MapGroup("/products")` | `[Route("api/products")]` on the class |
| Authorization | `.RequireAuthorization("Policy")` on the endpoint or group | `[Authorize(Policy = "Policy")]` |
| Anonymous access | `.AllowAnonymous()` | `[AllowAnonymous]` |
| Rate limiting | `.RequireRateLimiting("login")` | `[EnableRateLimiting("login")]` |
| Request timeout | `.WithRequestTimeout(Policies.LongRunning)`, `.DisableRequestTimeout()` | `[RequestTimeout("long-running")]`, `[DisableRequestTimeout]` |
| Swagger grouping and visibility | `.WithTags("Products")`, `.ExcludeFromDescription()` | `[Tags("Products")]`, `[ApiExplorerSettings(IgnoreApi = true)]` |
| Upload size | `.WithMetadata(new RequestSizeLimitAttribute(...))` | `[RequestSizeLimit(...)]` |
| Antiforgery on form posts | `.DisableAntiforgery()` | `[IgnoreAntiforgeryToken]` |
| Per-endpoint filters | `.AddEndpointFilter(...)` | `IActionFilter` / `[ServiceFilter]` |
| Model validation | handler code returns `Results.ValidationProblem(...)` | `[ApiController]` validates DataAnnotations and returns 400 automatically |

### Key differences in practice

1. **Less ceremony.** No class, base type or attributes, so an endpoint is just its route and logic.
2. **Dependencies per endpoint.** A handler resolves only the services it needs. A controller's constructor resolves
   everything any of its actions needs, on every request to that controller.
3. **No MVC filter pipeline.** Controllers run through model binding, model validation, action filters, result
   filters and output formatters. Minimal APIs skip that machinery and bind directly, which is faster to start and
   cheaper per request. The trade-off: DataAnnotations validation is not automatic, which is why this project validates
   explicitly and returns `Results.ValidationProblem`.
4. **Organisation is up to you.** This project puts each feature in a static class with a `Map(IEndpointRouteBuilder api)`
   method (`ProductEndpoints.Map(api)`), called once from `Program.cs`. That gives the same grouping a controller class
   would.
5. **Same HTTP behaviour.** Routing, authentication, authorization, rate limiting, CORS, ProblemDetails,
   OpenAPI/Swagger and dependency injection are shared by both styles. A client cannot tell which one served a request.

---

## 2. How a request flows through this API

```mermaid
sequenceDiagram
    autonumber
    participant B as Browser (Vue SPA)
    participant V as Vite dev proxy :5173 / YARP gateway :5000
    participant K as Kestrel :5080
    participant M as Middleware pipeline
    participant E as Endpoint (minimal API handler)
    participant S as Services (Application layer)
    participant D as SQL Server / Blob / Cache

    B->>V: GET /api/products?search=boiler (Bearer token)
    V->>K: forwards /api/* to the API
    K->>M: HttpContext created
    M->>M: CorrelationId → ExceptionHandler → StatusCodePages → Serilog request log
    M->>M: CORS → RateLimiter → Authentication (JWT) → Authorization → RequestTimeouts
    M->>E: routing selected the endpoint; parameters bound (route, query, body, DI)
    E->>S: call services (PricingService, SourcingService, ...)
    S->>D: EF Core queries / HybridCache / Blob storage
    D-->>S: data
    S-->>E: result
    E-->>M: IResult (Results.Ok / Problem / ...)
    M-->>B: JSON response + X-Correlation-Id header
```

The pipeline order comes from `Program.cs` and `ServiceDefaults.UseServiceDefaults()`:

| # | Middleware | What it does here |
|---|---|---|
| 1 | `CorrelationIdMiddleware` | Reads or creates `X-Correlation-Id`, puts it on logs, traces and the response |
| 2 | `UseExceptionHandler()` + `GlobalExceptionHandler` | Turns an unhandled exception into an RFC 7807 problem response: `KeyNotFoundException` → 404, `ArgumentException` → 400, `InvalidOperationException` → 409, timeouts/circuit open → 503, anything else → 500 |
| 3 | `UseStatusCodePages()` | Gives empty 401/404/415 responses a ProblemDetails body |
| 4 | `UseSerilogRequestLogging()` | One structured log line per request with timing |
| 5 | `UseCors()` | Allows the SPA origin (`AllowedOrigins`) |
| 6 | `UseRateLimiter()` | Policies `login` (10/min per IP), `register` (5/hour per IP), `agent` (30/hour per user) |
| 7 | `UseAuthentication()` | Validates the JWT bearer token (issuer, signature, lifetime) and builds `HttpContext.User` |
| 8 | `UseAuthorization()` | Checks the endpoint's policy (`ReadCatalogue`, `CatalogueAdmin`, `Buyer`, ...) |
| 9 | `UseRequestTimeouts()` | 30 s by default; `long-running` (15 min) for uploads and heavy reports; none for event streams |
| 10 | **Endpoint** | The minimal API handler runs |

Every endpoint is mapped under `/api` by `Program.cs`:

```csharp
var api = app.MapGroup("/api");
Catalogue.Web.Endpoints.AuthEndpoints.Map(api);
Catalogue.Web.Endpoints.ProductEndpoints.Map(api);
// ... one line per endpoint file
```

### Authorization policies (defined in `Program.cs`)

| Policy | Roles allowed |
|---|---|
| `ReadCatalogue` | Admin, SupplierManager, Buyer, Seller, Support, Viewer |
| `CatalogueAdmin` | Admin |
| `PlatformAdmin` | Admin |
| `SupplierAdmin` | Admin, SupplierManager |
| `ImportOps` | Admin, SupplierManager, Support |
| `Buyer` | Buyer, Admin |
| `Seller` | Seller, Admin, SupplierManager |
| `Marketplace` | Buyer, Seller, Admin, SupplierManager, Support |
| `.RequireAuthorization()` with no policy | any signed-in user |

A controller expresses the same with `[Authorize(Policy = "ReadCatalogue")]`. The policy definitions are shared by both styles.

### Errors look the same everywhere

Handlers return `Results.Problem(...)` or `Results.ValidationProblem(...)` for expected failures, and throw for unexpected
ones (`GlobalExceptionHandler` converts those). Either way the client receives:

```json
{ "type": "https://httpstatuses.io/409", "title": "Conflict", "status": 409, "detail": "Order PO-1 is already paid.",
  "correlationId": "342353e5181a412da59ef15a1513640d", "traceId": "00-29a1f2...", "error": "Order PO-1 is already paid." }
```

### Endpoints outside the `Endpoints` folder

`Program.cs` maps three operational endpoints directly. They are not part of the feature documents:
`GET /health` (simple liveness and Event Hub mode), `GET /health/live` and `GET /health/ready` (health checks for the
database, Event Hub and local model).

## 3. Reading the per-file documents

Each endpoint section uses the same layout:

```
### METHOD /api/route  ·  short name
Access       – policy / anonymous, rate limit, timeout
Request      – route, query, body, headers
Responses    – status codes and what they mean
Flow         – numbered steps from request to response
Minimal API  – the handler as written in this project
Controller   – the same endpoint written as a controller action
```
