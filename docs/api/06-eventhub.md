# Event Hub streaming: `EventHubEndpoints.cs`

Base route **`/api/eventhub`**: route group `api.MapGroup("/eventhub").WithTags("Event Hub").RequireAuthorization("ImportOps")`.

Very large EDI price catalogues (millions of lines) are not processed as one file. The **producer** reads the
interchange, splits it into chunks of line items and publishes the chunks as events to an Event Hub with several
partitions. **Partition consumers** then process the chunks in parallel. Each chunk is bulk-upserted with
`SqlBulkCopy` and `MERGE`, and a chunk ledger makes reprocessing idempotent. Progress is tracked on a normal
`ImportFile` with format `EDI-STREAM`, so [Imports](05-imports.md) shows it too.

```mermaid
flowchart LR
    S[EDI source: library file or upload] --> P[Producer: segment & chunk]
    P -->|events, round-robin| EH[(Event Hub: partitions 0..N)]
    EH --> C0[Consumer p0] & C1[Consumer p1] & Cn[Consumer pN]
    C0 & C1 & Cn --> DB[(SQL: bulk MERGE, chunk ledger, row errors)]
    C0 & C1 & Cn --> CP[(Checkpoints in Blob)]
    DB --> I[ImportFile progress → /api/imports/{id}]
```

### Authorization: group policy + endpoint policy

The group requires **`ImportOps`** (Admin, SupplierManager, Support). Endpoints that start work also call
`.RequireAuthorization("SupplierAdmin")`. Requirements **add up**: the caller must satisfy *both* policies, which in
practice means Admin or SupplierManager. Controllers behave the same way: an `[Authorize]` on the class plus an
`[Authorize]` on the action are both enforced.

| # | Method | Route | Access | Purpose |
|---|---|---|---|---|
| 1 | GET | `/api/eventhub/overview` | `ImportOps` | Live telemetry: partitions, lag, rates, timeline, runs |
| 2 | GET | `/api/eventhub/streams` | `ImportOps` | The latest 25 streamed imports |
| 3 | GET | `/api/eventhub/sources` | `ImportOps` | EDI files available on the server |
| 4 | POST | `/api/eventhub/sources/generate` | `ImportOps` + `SupplierAdmin` · 15 min | Generate a synthetic EDI file |
| 5 | POST | `/api/eventhub/streams` | `ImportOps` + `SupplierAdmin` | Stream a library file |
| 6 | POST | `/api/eventhub/streams/upload` | `ImportOps` + `SupplierAdmin` · 15 min · up to 2 GB | Upload and stream a file |
| 7 | POST | `/api/eventhub/streams/{id}/restream` | `ImportOps` | Replay a streamed import |

```csharp
[ApiController]
[Route("api/eventhub")]
[Authorize(Policy = "ImportOps")]          // = the group's RequireAuthorization
[Tags("Event Hub")]
public sealed class EventHubController(EdiStreamService streams) : ControllerBase { }
```

---

### 1. `GET /api/eventhub/overview` · Live telemetry

| | |
|---|---|
| Responses | **200** `{ hub: { mode, name, consumerGroup, namespace, partitionCount, consumerInProcess, connected, connectionError }, startedUtc, totals, rates, totalLag, partitions, timeline, recent, runs }` |

**Flow**
1. Ask the hub for partition metadata with a **4-second timeout** of its own. If the emulator or Azure is unreachable,
   the page still loads and shows `connected: false` with the error, instead of the request hanging.
2. `StreamTelemetry.Snapshot` combines the in-process counters for each partition (events, rows, lag) with the
   partition metadata.

```csharp
// Minimal API (abridged)
group.MapGet("/overview", async (IEventHubProducer hub, StreamTelemetry telemetry, EventHubOptions options,
    ILoggerFactory logs, CancellationToken ct) =>
{
    IReadOnlyList<PartitionInfo> partitions = [];
    string? connectionError = null;
    try
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        partitions = await hub.GetPartitionsAsync(timeout.Token);
    }
    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { connectionError = ex.Message; }
    var snapshot = telemetry.Snapshot(partitions);
    return Results.Ok(new { hub = new { /* ... */ connected = connectionError is null, connectionError }, snapshot.Totals, /* ... */ });
});
```

```csharp
// Controller
[HttpGet("overview")]
public async Task<IActionResult> Overview([FromServices] IEventHubProducer hub, [FromServices] StreamTelemetry telemetry,
    [FromServices] EventHubOptions options, CancellationToken ct) { /* same */ return Ok(result); }
```

---

### 2. `GET /api/eventhub/streams` · Recent streamed imports

| | |
|---|---|
| Responses | **200** `StreamImportDto[] { id, supplierId, supplierCode, fileName, status, totalRows, processedRows, errorRows, receivedAtUtc, completedAtUtc, failureReason }` (latest 25) |

```csharp
// Minimal API
group.MapGet("/streams", async (ICatalogueDbContext db, CancellationToken ct) =>
    Results.Ok(await db.ImportFiles.Where(i => i.Format == ImportFormats.EdiStream).OrderByDescending(i => i.ReceivedAtUtc).Take(25)
        .Select(i => new StreamImportDto(/* ... */)).ToListAsync(ct)));
```

```csharp
// Controller
[HttpGet("streams")]
public async Task<IActionResult> Streams([FromServices] ICatalogueDbContext db, CancellationToken ct) =>
    Ok(await db.ImportFiles.Where(i => i.Format == ImportFormats.EdiStream).OrderByDescending(i => i.ReceivedAtUtc).Take(25).Select(/* ... */).ToListAsync(ct));
```

*Note:* `GET /streams` and `POST /streams` share a path but not a method, so they are different endpoints in both styles.

---

### 3. `GET /api/eventhub/sources` · Server-side EDI library

| | |
|---|---|
| Responses | **200** `{ root, files: [{ name, sizeBytes, modifiedUtc, … }] }` |

**Flow:** lists the files under `EventHub:SourceDirectory` (default `samples/edi`), so huge files can be streamed
without uploading them through the browser.

```csharp
// Minimal API: a synchronous handler is fine when there is no I/O to await
group.MapGet("/sources", (IEdiSourceLibrary library) => Results.Ok(new { root = library.Root, files = library.List() }));
```

```csharp
// Controller
[HttpGet("sources")]
public IActionResult Sources([FromServices] IEdiSourceLibrary library) => Ok(new { root = library.Root, files = library.List() });
```

---

### 4. `POST /api/eventhub/sources/generate` · Generate a synthetic EDI file

| | |
|---|---|
| Access | `ImportOps` **and** `SupplierAdmin` · timeout `long-running` (15 min) |
| Body | `GenerateRequest { lineItems (1–10,000,000), errorRate? (0–0.5, default 0.004), name?, startIndex?, seed? }` |
| Responses | **200** the new file's entry · **400** count out of range, or an invalid name |

**Flow**
1. Validate the count, and build the file name (`generated/pricat-1m-<timestamp>.edi` by default).
2. **Path-traversal guard:** the full path must stay inside the library root, so `../../windows/...` is rejected.
3. `EdiSampleGenerator` writes realistic HVAC PRICAT items with a controlled share of deliberately invalid lines.
   `startIndex` controls overlap with other files, so two suppliers can offer the same GTINs.
4. Audit `EdiSampleGenerated`.

```csharp
// Minimal API (abridged)
group.MapPost("/sources/generate", async (GenerateRequest req, IEdiSourceLibrary library,
    ICatalogueDbContext db, IActorContext actor, CancellationToken ct) =>
{
    if (req.LineItems is < 1 or > MaxGeneratedItems) return Results.BadRequest(new { error = "..." });
    var path = Path.GetFullPath(Path.Combine(library.Root, name));
    if (!path.StartsWith(library.Root, StringComparison.OrdinalIgnoreCase)) return Results.BadRequest(new { error = "Invalid file name." });
    var size = await EdiSampleGenerator.WriteAsync(path, req.LineItems, Math.Clamp(req.ErrorRate ?? 0.004, 0, 0.5), seed: req.Seed ?? 42, ct: ct, startIndex: Math.Max(0, req.StartIndex ?? 0));
    // audit
    return Results.Ok(library.List().FirstOrDefault(f => f.Name == name.Replace('\\', '/')));
}).RequireAuthorization("SupplierAdmin").WithRequestTimeout(Policies.LongRunning);
```

```csharp
// Controller
[HttpPost("sources/generate")]
[Authorize(Policy = "SupplierAdmin")]        // in addition to the class-level ImportOps
[RequestTimeout(Policies.LongRunning)]
public async Task<IActionResult> Generate([FromBody] GenerateRequest req, [FromServices] IEdiSourceLibrary library, CancellationToken ct) { /* same */ }
```

---

### 5. `POST /api/eventhub/streams` · Stream a library file

| | |
|---|---|
| Access | `ImportOps` + `SupplierAdmin` |
| Body | `StartStreamRequest { supplierId, source (library file name), chunkSize? }` |
| Responses | **202** `{ id, status, existing }` with `Location: /api/imports/{id}` · **400** · **404** supplier or file not found |

**Flow:** `EdiStreamService.StartFromLibraryAsync`:
1. Hash the source, so the same file for the same supplier returns the existing import with `existing: true`.
2. Create the `ImportFile` (`EDI-STREAM`).
3. Start the background producer, which segments the file and publishes chunk events.

The request returns immediately; progress appears in the overview and on the import.

```csharp
// Minimal API
group.MapPost("/streams", async (StartStreamRequest req, EdiStreamService streams, CancellationToken ct) =>
{
    try
    {
        var result = await streams.StartFromLibraryAsync(req.SupplierId, req.Source, req.ChunkSize, ct);
        return Results.Accepted($"/api/imports/{result.Import.Id}", new { id = result.Import.Id, status = result.Import.Status.ToString(), existing = result.Existing });
    }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireAuthorization("SupplierAdmin");
```

```csharp
// Controller
[HttpPost("streams")]
[Authorize(Policy = "SupplierAdmin")]
public async Task<IActionResult> Start([FromBody] StartStreamRequest req, CancellationToken ct)
{
    try
    {
        var result = await streams.StartFromLibraryAsync(req.SupplierId, req.Source, req.ChunkSize, ct);
        return Accepted($"/api/imports/{result.Import.Id}", new { id = result.Import.Id, status = result.Import.Status.ToString(), existing = result.Existing });
    }
    catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
}
```

---

### 6. `POST /api/eventhub/streams/upload` · Upload and stream

| | |
|---|---|
| Access | `ImportOps` + `SupplierAdmin` · timeout `long-running` · antiforgery disabled · request size limit = `MaxUploadBytes` (2 GB) + 1 MB |
| Request | `multipart/form-data`: one `.edi` file, `supplierId`, optional `chunkSize` |
| Responses | **202** `{ id, status, existing }` · **400** · **404** |

**Flow:** read the form asynchronously (`ReadFormAsync`), which spools large uploads to disk instead of memory. Then
`StartFromUploadAsync` saves the file and starts the producer, as in endpoint 5.

The global Kestrel limit is 30 MB (set in `Program.cs`). This one endpoint raises it with
`.WithMetadata(new RequestSizeLimitAttribute(...))`. That is how a minimal API attaches attribute-style metadata. A
controller would put `[RequestSizeLimit(...)]` directly on the action.

```csharp
// Minimal API (abridged)
group.MapPost("/streams/upload", async (HttpRequest http, EdiStreamService streams, CancellationToken ct) =>
{
    if (!http.HasFormContentType) return Results.BadRequest(new { error = "Multipart form with a file is required." });
    var form = await http.ReadFormAsync(ct);
    // file, supplierId, optional chunkSize
    var result = await streams.StartFromUploadAsync(supplierId, stream, file.FileName, chunkSize, ct);
    return Results.Accepted($"/api/imports/{result.Import.Id}", new { id = result.Import.Id, status = result.Import.Status.ToString(), existing = result.Existing });
}).RequireAuthorization("SupplierAdmin").DisableAntiforgery().WithRequestTimeout(Policies.LongRunning)
  .WithMetadata(new RequestSizeLimitAttribute(EdiStreamService.MaxUploadBytes + 1024 * 1024));
```

```csharp
// Controller
[HttpPost("streams/upload")]
[Authorize(Policy = "SupplierAdmin")]
[RequestTimeout(Policies.LongRunning)]
[RequestSizeLimit(EdiStreamService.MaxUploadBytes + 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = EdiStreamService.MaxUploadBytes + 1024 * 1024)]
public async Task<IActionResult> Upload(IFormFile file, [FromForm] long supplierId, [FromForm] int? chunkSize, CancellationToken ct) { /* same */ }
```

---

### 7. `POST /api/eventhub/streams/{id}/restream` · Replay

| | |
|---|---|
| Access | `ImportOps` |
| Body | optional `RestreamRequest { chunkSize? }`; the parameter is nullable (`RestreamRequest? req`), so an empty body is allowed |
| Responses | **202** `{ id }` · **404** · **409** the source file is gone, or the stream is still running |

**Flow:** `EdiStreamService.RestreamAsync` resets the import's counters and publishes the chunks again. The chunk
ledger skips chunks that were already applied, so a replay never doubles data. This is also what
[Imports](05-imports.md) reprocess does for streamed imports.

```csharp
// Minimal API
group.MapPost("/streams/{id:long}/restream", async (long id, RestreamRequest? req, EdiStreamService streams, CancellationToken ct) =>
{
    try
    {
        var import = await streams.RestreamAsync(id, req?.ChunkSize, ct);
        return Results.Accepted($"/api/imports/{import.Id}", new { id = import.Id });
    }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});
```

```csharp
// Controller
[HttpPost("streams/{id:long}/restream")]
public async Task<IActionResult> Restream(long id, [FromBody] RestreamRequest? req, CancellationToken ct) { /* same */ }
```

*Difference:* with `[ApiController]`, an empty body for a `[FromBody]` parameter returns 400 unless the parameter is
nullable or `EmptyBodyBehavior.Allow` is set. In a minimal API, the nullable `RestreamRequest?` is enough.
