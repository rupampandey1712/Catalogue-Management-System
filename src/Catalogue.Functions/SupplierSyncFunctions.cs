using Catalogue.Application;
using Catalogue.Contracts;
using Catalogue.Domain;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Catalogue.Functions;

/// <summary>
/// Durable orchestration for supplier REST synchronisation (architecture doc, Scenario D).
///
/// Why Durable Functions here:
///  - Supplier sync is a long-running fan-out/fan-in job with retries; the orchestrator
///    checkpoints progress so a host restart resumes instead of restarting.
///  - Each activity gets an automatic retry policy (exponential backoff) for flaky
///    supplier APIs, without hand-rolled retry loops.
///  - Scheduled (timer) client + on-demand HTTP client share the same orchestration.
///
/// Services come in through the constructor: the isolated worker only binds trigger/binding
/// parameters on function methods, so a service parameter there would always be null.
/// </summary>
public sealed class SupplierSyncFunctions(
    CatalogueDbContextFactory dbFactory,
    SupplierClientResolver clients,
    ILogger<SupplierSyncFunctions> logger)
{
    private static readonly TaskOptions RetryPolicy = new(new RetryPolicy(
        maxNumberOfAttempts: 3,
        firstRetryInterval: TimeSpan.FromSeconds(5),
        backoffCoefficient: 2.0));

    // ---------- Orchestrators ----------

    [Function(nameof(SupplierSyncOrchestration))]
    public static async Task<SyncSummary> SupplierSyncOrchestration(
        [OrchestrationTrigger] TaskOrchestrationContext ctx)
    {
        var suppliers = await ctx.CallActivityAsync<List<SupplierRef>>(
            nameof(GetRestSuppliersActivity), (object?)null);

        // Fan-out: one sub-orchestration per supplier, in parallel.
        var results = await Task.WhenAll(suppliers.Select(s =>
            ctx.CallSubOrchestratorAsync<SupplierSyncResult>(
                nameof(SingleSupplierSyncOrchestration), s, RetryPolicy)));

        var summary = new SyncSummary(suppliers.Count,
            results.Sum(r => r.Fetched), results.Sum(r => r.Upserted), results.Sum(r => r.Errors));
        await ctx.CallActivityAsync(nameof(AuditSyncActivity), summary);
        return summary;
    }

    [Function(nameof(SingleSupplierSyncOrchestration))]
    public static async Task<SupplierSyncResult> SingleSupplierSyncOrchestration(
        [OrchestrationTrigger] TaskOrchestrationContext ctx)
    {
        var supplier = ctx.GetInput<SupplierRef>()!;
        var products = await ctx.CallActivityAsync<IReadOnlyList<SupplierProductDto>>(
            nameof(FetchSupplierProductsActivity), supplier, RetryPolicy);

        // Chunk and fan out upserts; each chunk is independently retried.
        const int chunkSize = 25;
        var chunks = products
            .Select((p, i) => (p, i))
            .GroupBy(x => x.i / chunkSize)
            .Select(g => g.Select(x => x.p).ToList())
            .ToList();

        var upserted = 0;
        var results = await Task.WhenAll(chunks.Select(chunk =>
            ctx.CallActivityAsync<int>(nameof(UpsertChunkActivity),
                new UpsertChunkRequest(supplier.Id, chunk), RetryPolicy)));
        upserted = results.Sum();

        return new SupplierSyncResult(supplier.Code, products.Count, upserted,
            products.Count - results.Sum());
    }

    // ---------- Clients (entry points) ----------

    [Function(nameof(SyncTimerClient))]
    public static async Task SyncTimerClient(
        [TimerTrigger("0 */30 * * * *")] TimerInfo _,
        [DurableClient] DurableTaskClient client,
        FunctionContext context)
    {
        var logger = context.GetLogger(nameof(SyncTimerClient));
        var running = client.GetAllInstancesAsync(new OrchestrationQuery
        {
            Statuses = [OrchestrationRuntimeStatus.Running, OrchestrationRuntimeStatus.Pending]
        });
        await foreach (var instance in running)
        {
            logger.LogInformation("Sync already running as {InstanceId}; skipping.", instance.InstanceId);
            return;
        }
        var id = await client.ScheduleNewOrchestrationInstanceAsync(nameof(SupplierSyncOrchestration));
        logger.LogInformation("Scheduled supplier sync {InstanceId}.", id);
    }

    [Function(nameof(SyncHttpClient))]
    public static async Task<HttpResponseData> SyncHttpClient(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sync/start")] HttpRequestData req,
        [DurableClient] DurableTaskClient client)
    {
        var instanceId = await client.ScheduleNewOrchestrationInstanceAsync(nameof(SupplierSyncOrchestration));
        var response = req.CreateResponse(System.Net.HttpStatusCode.Accepted);
        var baseUrl = $"{req.Url.Scheme}://{req.Url.Authority}";
        await response.WriteAsJsonAsync(new
        {
            instanceId,
            statusQueryGetUri = $"{baseUrl}/runtime/webhooks/durabletask/instances/{instanceId}"
        });
        return response;
    }

    // ---------- Activities ----------

    [Function(nameof(GetRestSuppliersActivity))]
    public async Task<List<SupplierRef>> GetRestSuppliersActivity([ActivityTrigger] object? input)
    {
        try
        {
            await using var db = dbFactory.Create();
            return await db.Suppliers
                .Where(s => s.IsActive && s.IntegrationType == IntegrationType.Rest)
                .Select(s => new SupplierRef(s.Id, s.Code))
                .ToListAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("GetRestSuppliersActivity failed", ex);
        }
    }

    [Function(nameof(FetchSupplierProductsActivity))]
    public async Task<IReadOnlyList<SupplierProductDto>> FetchSupplierProductsActivity([ActivityTrigger] SupplierRef supplier)
    {
        logger.LogInformation("Fetching products from supplier {SupplierCode}.", supplier.Code);
        var client = clients.Resolve();
        return await client.GetProductsAsync(CancellationToken.None);
    }

    [Function(nameof(UpsertChunkActivity))]
    public async Task<int> UpsertChunkActivity([ActivityTrigger] UpsertChunkRequest request)
    {
        await using var db = dbFactory.Create();
        var existing = await db.Products
            .Where(p => p.SupplierId == request.SupplierId)
            .ToListAsync();
        var bySku = existing.ToDictionary(p => p.Sku);
        var upserted = 0;

        foreach (var dto in request.Products)
        {
            if (RowValidator.Validate(new CanonicalProductImport(
                    dto.SupplierSku, dto.Name, dto.Description, dto.Category,
                    dto.Price, dto.Currency, dto.Quantity)).Count > 0)
                continue;

            var sku = dto.SupplierSku.Trim().ToUpperInvariant();
            if (!bySku.TryGetValue(sku, out var product))
            {
                product = new Product(sku, dto.Name, dto.Description, request.SupplierId);
                product.Activate();
                db.Products.Add(product);
                bySku[sku] = product;
            }
            else
            {
                product.Update(dto.Name, dto.Description);
                product.Activate();
            }
            if (dto.Price is { } price)
            {
                var currency = (dto.Currency ?? "GBP").ToUpperInvariant();
                if (product.Prices.All(p => p.Currency != currency || p.ValidToUtc is not null) ||
                    product.Prices.Any(p => p.Currency == currency && p.ValidToUtc is null && p.Amount != price))
                {
                    // Close out existing price of that currency, then add the new one.
                    product.Prices.Add(ProductPrice.For(product, currency, price, DateTime.UtcNow));
                }
            }
            if (dto.Quantity is { } qty)
            {
                var inv = product.InventoryRecords.FirstOrDefault(i => i.SupplierId == request.SupplierId);
                if (inv is null) product.InventoryRecords.Add(Inventory.For(product, request.SupplierId, qty));
                else inv.SetQuantity(qty);
            }
            upserted++;
        }
        await db.SaveChangesAsync();
        return upserted;
    }

    [Function(nameof(AuditSyncActivity))]
    public async Task AuditSyncActivity([ActivityTrigger] SyncSummary summary)
    {
        await using var db = dbFactory.Create();
        db.AuditEvents.Add(new AuditEvent("durable-functions", "SupplierSyncCompleted", "Sync", "all",
            $"suppliers={summary.Suppliers} fetched={summary.Fetched} upserted={summary.Upserted} errors={summary.Errors}"));
        await db.SaveChangesAsync();
    }

    // DTOs
    public sealed record SupplierRef(long Id, string Code);
    public sealed record UpsertChunkRequest(long SupplierId, List<SupplierProductDto> Products);
    public sealed record SupplierSyncResult(string SupplierCode, int Fetched, int Upserted, int Errors);
    public sealed record SyncSummary(int Suppliers, int Fetched, int Upserted, int Errors);
}
