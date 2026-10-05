using Catalogue.Application;
using Catalogue.Domain;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Microsoft.Extensions.Caching.Hybrid;
using static Catalogue.Web.Endpoints.ProductEndpoints;

namespace Catalogue.Web.Endpoints;

public static class DashboardEndpoints
{
    public sealed record AuditDto(long Id, string ActorId, string Action, string EntityType, string EntityId,
        DateTime TimestampUtc, string? Details, string? CorrelationId);

    public static void Map(IEndpointRouteBuilder api)
    {
        // Aggregates scan large tables once millions of products exist, so cache briefly.
        api.MapGet("/dashboard", async (ICatalogueDbContext db, HybridCache cache, CancellationToken ct) =>
            Results.Content(await cache.GetOrCreateAsync("dashboard", async token =>
            {
                var ct = token;
                var since14 = DateTime.UtcNow.Date.AddDays(-13);

                var byStatus = await db.Products.GroupBy(p => p.Status)
                    .Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(ct);
                var importsByStatus = await db.ImportFiles.GroupBy(i => i.Status)
                    .Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(ct);
                var daily = await db.ImportFiles.Where(i => i.ReceivedAtUtc >= since14)
                    .GroupBy(i => i.ReceivedAtUtc.Date)
                    .Select(g => new { Day = g.Key, Rows = g.Sum(i => (long)i.ProcessedRows), Errors = g.Sum(i => (long)i.ErrorRows), Imports = g.Count() })
                    .ToListAsync(ct);
                var topCategories = await db.ProductCategories.GroupBy(pc => pc.CategoryId)
                    .Select(g => new { CategoryId = g.Key, Count = g.Count() })
                    .OrderByDescending(x => x.Count).Take(8)
                    .Join(db.Categories, x => x.CategoryId, c => c.Id, (x, c) => new { c.Code, c.Name, x.Count })
                    .ToListAsync(ct);
                var recentImports = await db.ImportFiles.OrderByDescending(i => i.ReceivedAtUtc).Take(6)
                    .Select(i => new { i.Id, i.FileName, i.Format, Status = i.Status.ToString(), i.TotalRows, i.ProcessedRows, i.ErrorRows, i.ReceivedAtUtc, SupplierCode = i.Supplier!.Code })
                    .ToListAsync(ct);
                var pendingApprovals = await db.ProductChangeRequests.CountAsync(r => r.Status == ChangeStatus.Pending, ct);
                var offerSuppliers = await db.SupplierOffers.GroupBy(o => o.ProductId).Where(g => g.Count() > 1).CountAsync(ct);
                var activity = await db.AuditEvents.OrderByDescending(a => a.Id).Take(8)
                    .Select(a => new AuditDto(a.Id, a.ActorId, a.Action, a.EntityType, a.EntityId, a.TimestampUtc, a.Details, a.CorrelationId))
                    .ToListAsync(ct);

                var result = new
                {
                    products = new
                    {
                        total = byStatus.Sum(s => s.Count),
                        active = byStatus.Where(s => s.Status == ProductStatus.Active).Sum(s => s.Count),
                        inactive = byStatus.Where(s => s.Status == ProductStatus.Inactive).Sum(s => s.Count),
                        draft = byStatus.Where(s => s.Status == ProductStatus.Draft).Sum(s => s.Count)
                    },
                    suppliers = new { total = await db.Suppliers.CountAsync(ct), active = await db.Suppliers.CountAsync(s => s.IsActive, ct) },
                    categories = await db.Categories.CountAsync(ct),
                    pendingApprovals,
                    multiSourcedProducts = offerSuppliers,
                    pendingProducts = byStatus.Where(s => s.Status == ProductStatus.PendingApproval).Sum(s => s.Count),
                    imports = new
                    {
                        total = importsByStatus.Sum(s => s.Count),
                        byStatus = importsByStatus.ToDictionary(s => s.Status.ToString(), s => s.Count),
                        rowsLast14Days = daily.Sum(d => d.Rows),
                        errorsLast14Days = daily.Sum(d => d.Errors)
                    },
                    daily = Enumerable.Range(0, 14).Select(o => since14.AddDays(o)).Select(d =>
                    {
                        var hit = daily.FirstOrDefault(x => x.Day == d);
                        return new { day = d, rows = hit?.Rows ?? 0, errors = hit?.Errors ?? 0, imports = hit?.Imports ?? 0 };
                    }),
                    topCategories,
                    recentImports,
                    activity,
                    generatedUtc = DateTime.UtcNow
                };
                return JsonSerializer.Serialize(result, JsonSerializerOptions.Web);
            }, new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(15), LocalCacheExpiration = TimeSpan.FromSeconds(15) },
            [CacheTags.Dashboard], ct), "application/json")).RequireAuthorization("ReadCatalogue").WithTags("Dashboard");

        api.MapGet("/audit", async (ICatalogueDbContext db, HttpResponse response, string? search, string? action,
            string? entityType, int? page, int? pageSize, CancellationToken ct) =>
        {
            var pg = Math.Max(1, page ?? 1);
            var ps = Math.Clamp(pageSize ?? 25, 1, 200);
            var q = db.AuditEvents.AsQueryable();
            if (!string.IsNullOrWhiteSpace(action)) q = q.Where(a => a.Action == action);
            if (!string.IsNullOrWhiteSpace(entityType)) q = q.Where(a => a.EntityType == entityType);
            if (!string.IsNullOrWhiteSpace(search))
                q = q.Where(a => a.ActorId.Contains(search) || a.EntityId.Contains(search)
                    || (a.Details != null && a.Details.Contains(search)) || (a.CorrelationId != null && a.CorrelationId.Contains(search)));
            var total = await q.CountAsync(ct);
            var items = await q.OrderByDescending(a => a.Id).Skip((pg - 1) * ps).Take(ps)
                .Select(a => new AuditDto(a.Id, a.ActorId, a.Action, a.EntityType, a.EntityId, a.TimestampUtc, a.Details, a.CorrelationId))
                .ToListAsync(ct);
            return Results.Ok(new PagedResult<AuditDto>(items, total, pg, ps));
        }).RequireAuthorization("ImportOps").WithTags("Audit");

        api.MapGet("/audit/actions", async (ICatalogueDbContext db, CancellationToken ct) =>
            Results.Ok(await db.AuditEvents.Select(a => a.Action).Distinct().OrderBy(a => a).ToListAsync(ct)))
            .RequireAuthorization("ImportOps").WithTags("Audit");
    }
}
