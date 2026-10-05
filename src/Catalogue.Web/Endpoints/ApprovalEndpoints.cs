using Catalogue.Application;
using Catalogue.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace Catalogue.Web.Endpoints;

/// <summary>Review queue for supplier-fed changes (new products, large price moves).</summary>
public static class ApprovalEndpoints
{
    public sealed record ChangeRequestDto(long Id, long ProductId, string ProductSku, string ProductName, string? Brand,
        long SupplierId, string SupplierCode, long? ImportId, string Kind, string Status, string? Currency,
        decimal? OldPrice, decimal? NewPrice, decimal? ChangePercent, string? Summary,
        DateTime CreatedAtUtc, string? DecidedBy, DateTime? DecidedAtUtc, string? DecisionNote);
    public sealed record DecideRequest(IReadOnlyList<long> Ids, bool Approve, string? Note);
    public sealed record DecideMatchingRequest(string? Kind, long? SupplierId, long? ImportId, bool Approve, string? Note);

    public static void Map(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/approvals").WithTags("Approvals");

        group.MapGet("", async (ICatalogueDbContext db, HttpResponse response, string? status, string? kind, long? supplierId,
            long? importId, string? search, int? page, int? pageSize, CancellationToken ct) =>
        {
            var pg = Math.Max(1, page ?? 1);
            var ps = Math.Clamp(pageSize ?? 25, 1, 200);
            var st = Enum.TryParse<ChangeStatus>(status, true, out var s) ? s : ChangeStatus.Pending;
            var q = db.ProductChangeRequests.AsNoTracking().Where(r => r.Status == st);
            if (Enum.TryParse<ChangeKind>(kind, true, out var k)) q = q.Where(r => r.Kind == k);
            if (supplierId is not null) q = q.Where(r => r.SupplierId == supplierId);
            if (importId is not null) q = q.Where(r => r.ImportId == importId);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim().ToUpperInvariant();
                q = q.Where(r => r.Product!.Sku.StartsWith(term) || r.Product.Name.StartsWith(search));
            }
            var total = await q.CountAsync(ct);
            var rows = await (st == ChangeStatus.Pending ? q.OrderBy(r => r.Id) : q.OrderByDescending(r => r.DecidedAtUtc))
                .Skip((pg - 1) * ps).Take(ps)
                .Select(r => new
                {
                    r.Id, r.ProductId, Sku = r.Product!.Sku, ProductName = r.Product.Name, r.Product.Brand, r.SupplierId,
                    SupplierCode = r.Supplier!.Code, r.ImportId, r.Kind, r.Status, r.Currency, r.OldPrice, r.NewPrice, r.Summary,
                    r.CreatedAtUtc, r.DecidedBy, r.DecidedAtUtc, r.DecisionNote
                }).ToListAsync(ct);
            return Results.Ok(new ProductEndpoints.PagedResult<ChangeRequestDto>(rows.Select(r => new ChangeRequestDto(
                r.Id, r.ProductId, r.Sku, r.ProductName, r.Brand, r.SupplierId, r.SupplierCode, r.ImportId, r.Kind.ToString(), r.Status.ToString(),
                r.Currency, r.OldPrice, r.NewPrice,
                r.OldPrice is > 0 && r.NewPrice is { } n ? Math.Round((n - r.OldPrice.Value) / r.OldPrice.Value * 100, 1) : null,
                r.Summary, r.CreatedAtUtc, r.DecidedBy, r.DecidedAtUtc, r.DecisionNote)).ToList(), total, pg, ps));
        }).RequireAuthorization("ImportOps");

        group.MapGet("/summary", async (ICatalogueDbContext db, CancellationToken ct) =>
        {
            var pending = await db.ProductChangeRequests.Where(r => r.Status == ChangeStatus.Pending)
                .GroupBy(r => new { r.Kind, r.SupplierId, r.Supplier!.Code })
                .Select(g => new { kind = g.Key.Kind.ToString(), supplierId = g.Key.SupplierId, supplierCode = g.Key.Code, count = g.Count() })
                .ToListAsync(ct);
            var decided24h = await db.ProductChangeRequests
                .Where(r => r.Status != ChangeStatus.Pending && r.DecidedAtUtc >= DateTime.UtcNow.AddDays(-1)).CountAsync(ct);
            return Results.Ok(new { totalPending = pending.Sum(p => p.count), pending, decided24h });
        }).RequireAuthorization("ImportOps");

        group.MapPost("/decide", async (DecideRequest req, ApprovalService approvals, HybridCache cache, CancellationToken ct) =>
        {
            if (req.Ids.Count == 0) return Results.BadRequest(new { error = "Select at least one change." });
            if (req.Ids.Count > ApprovalService.MaxPerCall) return Results.BadRequest(new { error = $"At most {ApprovalService.MaxPerCall} per call." });
            var decided = await approvals.DecideAsync(req.Ids, req.Approve, req.Note, ct);
            await cache.RemoveByTagAsync(CacheTags.Products, ct);
            return Results.Ok(new { decided });
        }).RequireAuthorization("CatalogueAdmin");

        // "Approve all 250,000 new products from this import" - works through them in batches per call.
        group.MapPost("/decide-matching", async (DecideMatchingRequest req, ApprovalService approvals, HybridCache cache, CancellationToken ct) =>
        {
            ChangeKind? kind = Enum.TryParse<ChangeKind>(req.Kind, true, out var k) ? k : null;
            var (decided, remaining) = await approvals.DecideMatchingAsync(new ApprovalFilter(kind, req.SupplierId, req.ImportId), req.Approve, req.Note, ct);
            await cache.RemoveByTagAsync(CacheTags.Products, ct);
            return Results.Ok(new { decided, remaining });
        }).RequireAuthorization("CatalogueAdmin").WithRequestTimeout(Policies.LongRunning);
    }
}
