using Catalogue.Application;
using Catalogue.Contracts;
using Catalogue.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace Catalogue.Web.Endpoints;

public static class SupplierEndpoints
{
    public sealed record SupplierDto(long Id, string Code, string Name, string IntegrationType,
        bool IsActive, string? MappingVersion, int ProductCount = 0, int ImportCount = 0, DateTime? LastImportUtc = null,
        bool RequireApprovalForNewProducts = false, decimal? PriceChangeApprovalThresholdPercent = null, int OfferCount = 0, int PendingApprovals = 0);
    public sealed record UpdateSupplierRequest(string Name, string? MappingVersion, bool IsActive,
        bool RequireApprovalForNewProducts, decimal? PriceChangeApprovalThresholdPercent);
    public sealed record CreateSupplierRequest(string Code, string Name, string IntegrationType);

    public static void Map(IEndpointRouteBuilder api)
    {
        // Counts over millions of products are expensive; 30s staleness is fine for this view.
        api.MapGet("/suppliers", async (ICatalogueDbContext db, HybridCache cache, CancellationToken ct) =>
            Results.Ok(await cache.GetOrCreateAsync("suppliers:all", async token => await db.Suppliers.AsNoTracking().OrderBy(s => s.Code)
                .Select(s => new SupplierDto(s.Id, s.Code, s.Name, s.IntegrationType.ToString(), s.IsActive, s.MappingVersion,
                    db.Products.Count(p => p.SupplierId == s.Id),
                    db.ImportFiles.Count(i => i.SupplierId == s.Id),
                    db.ImportFiles.Where(i => i.SupplierId == s.Id).Max(i => (DateTime?)i.ReceivedAtUtc),
                    s.RequireApprovalForNewProducts, s.PriceChangeApprovalThresholdPercent,
                    db.SupplierOffers.Count(o => o.SupplierId == s.Id),
                    db.ProductChangeRequests.Count(r => r.SupplierId == s.Id && r.Status == ChangeStatus.Pending)))
                .ToListAsync(token),
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(30), LocalCacheExpiration = TimeSpan.FromSeconds(30) },
                [CacheTags.Suppliers], ct)))
            .RequireAuthorization("SupplierAdmin").WithTags("Suppliers");

        api.MapPost("/suppliers", async (CreateSupplierRequest req, ICatalogueDbContext db,
            IActorContext actor, HybridCache cache, CancellationToken ct) =>
        {
            if (!Enum.TryParse<IntegrationType>(req.IntegrationType, true, out var type))
                return Results.BadRequest(new { error = $"Unknown integration type '{req.IntegrationType}'." });
            if (await db.Suppliers.AnyAsync(s => s.Code == req.Code.ToUpper(), ct))
                return Results.Conflict(new { error = "Supplier code already exists." });
            var supplier = new Supplier(req.Code, req.Name, type);
            db.Suppliers.Add(supplier);
            db.AuditEvents.Add(new AuditEvent(actor.ActorId, "SupplierCreated", "Supplier",
                supplier.Code, req.Name, actor.CorrelationId));
            await db.SaveChangesAsync(ct);
            await cache.RemoveByTagAsync(CacheTags.Suppliers, ct);
            return Results.Created($"/api/suppliers/{supplier.Id}",
                new SupplierDto(supplier.Id, supplier.Code, supplier.Name,
                    supplier.IntegrationType.ToString(), supplier.IsActive, supplier.MappingVersion));
        }).RequireAuthorization("SupplierAdmin").WithTags("Suppliers");

        api.MapPut("/suppliers/{id:long}", async (long id, UpdateSupplierRequest req, ICatalogueDbContext db,
            IActorContext actor, HybridCache cache, IReferenceDataProvider refData, CancellationToken ct) =>
        {
            var supplier = await db.Suppliers.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (supplier is null) return Results.NotFound();
            supplier.Update(req.Name, req.MappingVersion);
            if (req.IsActive) supplier.Activate(); else supplier.Deactivate();
            supplier.SetApprovalPolicy(req.RequireApprovalForNewProducts, req.PriceChangeApprovalThresholdPercent);
            db.AuditEvents.Add(new AuditEvent(actor.ActorId, "SupplierUpdated", "Supplier", supplier.Code,
                $"active={req.IsActive} approveNew={req.RequireApprovalForNewProducts} priceThreshold={req.PriceChangeApprovalThresholdPercent}",
                actor.CorrelationId));
            await db.SaveChangesAsync(ct);
            await cache.RemoveByTagAsync(CacheTags.Suppliers, ct);
            refData.Invalidate(); // import pipelines pick up the new policy immediately
            return Results.NoContent();
        }).RequireAuthorization("SupplierAdmin").WithTags("Suppliers");

        // FR-06: supplier REST sync via adapter boundary (canonical model only).
        api.MapGet("/suppliers/demo-products", async (ISupplierProductClient client, CancellationToken ct) =>
            Results.Ok(await client.GetProductsAsync(ct)))
            .RequireAuthorization("SupplierAdmin").WithTags("Suppliers");
    }
}
