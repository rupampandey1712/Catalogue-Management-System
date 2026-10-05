using Catalogue.Application;
using Catalogue.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace Catalogue.Web.Endpoints;

/// <summary>Trade price lists, quantity breaks, customer accounts and price quotes.</summary>
public static class PricingEndpoints
{
    public sealed record PriceListDto(long Id, string Code, string Name, string Kind, string Currency, decimal DiscountPercent,
        long? ParentPriceListId, string? ParentCode, bool IsActive, int EntryCount, int CustomerCount);
    public sealed record UpsertPriceListRequest(string Code, string Name, string Kind, decimal DiscountPercent, long? ParentPriceListId, bool IsActive = true);
    public sealed record EntryDto(long Id, long ProductId, string Sku, string ProductName, int MinQuantity, decimal UnitPrice,
        decimal? ListPrice, DateTime ValidFromUtc, DateTime? ValidToUtc);
    public sealed record AddEntryRequest(string Sku, int MinQuantity, decimal UnitPrice, DateTime? ValidFromUtc, DateTime? ValidToUtc);
    public sealed record CustomerDto(long Id, string AccountNumber, string Name, long? PriceListId, string? PriceListCode, bool IsActive);
    public sealed record CreateCustomerRequest(string AccountNumber, string Name, long? PriceListId);

    public static void Map(IEndpointRouteBuilder api)
    {
        var lists = api.MapGroup("/pricelists").WithTags("Pricing");

        lists.MapGet("", async (ICatalogueDbContext db, CancellationToken ct) =>
        {
            var entryCounts = await db.PriceListEntries.GroupBy(e => e.PriceListId).Select(g => new { g.Key, C = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.C, ct);
            var customerCounts = await db.CustomerAccounts.Where(c => c.PriceListId != null).GroupBy(c => c.PriceListId!.Value)
                .Select(g => new { g.Key, C = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.C, ct);
            var all = await db.PriceLists.AsNoTracking().OrderBy(p => p.Kind).ThenBy(p => p.Code).ToListAsync(ct);
            return Results.Ok(all.Select(p => new PriceListDto(p.Id, p.Code, p.Name, p.Kind.ToString(), p.Currency, p.DiscountPercent,
                p.ParentPriceListId, all.FirstOrDefault(x => x.Id == p.ParentPriceListId)?.Code, p.IsActive,
                entryCounts.GetValueOrDefault(p.Id), customerCounts.GetValueOrDefault(p.Id))));
        }).RequireAuthorization("ReadCatalogue");

        lists.MapPost("", async (UpsertPriceListRequest req, ICatalogueDbContext db, IActorContext actor, CancellationToken ct) =>
        {
            if (!Enum.TryParse<PriceListKind>(req.Kind, true, out var kind)) return Results.BadRequest(new { error = $"Unknown kind '{req.Kind}'." });
            if (await db.PriceLists.AnyAsync(p => p.Code == req.Code.ToUpper(), ct)) return Results.Conflict(new { error = "Price list code already exists." });
            var list = new PriceList(req.Code, req.Name, kind, req.DiscountPercent, req.ParentPriceListId);
            db.PriceLists.Add(list);
            db.AuditEvents.Add(new AuditEvent(actor.ActorId, "PriceListCreated", "PriceList", list.Code, $"{req.DiscountPercent}% off list", actor.CorrelationId));
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/pricelists/{list.Id}", new { list.Id });
        }).RequireAuthorization("CatalogueAdmin");

        lists.MapPut("/{id:long}", async (long id, UpsertPriceListRequest req, ICatalogueDbContext db, IActorContext actor, CancellationToken ct) =>
        {
            var all = await db.PriceLists.ToDictionaryAsync(p => p.Id, ct);
            if (!all.TryGetValue(id, out var list)) return Results.NotFound();
            // Reject fallback cycles (A → B → A).
            if (req.ParentPriceListId is { } parent && PriceResolver.Chain(parent, all).Any(l => l.Id == id))
                return Results.BadRequest(new { error = "That fallback would create a cycle." });
            list.Update(req.Name, req.DiscountPercent, req.ParentPriceListId, req.IsActive);
            db.AuditEvents.Add(new AuditEvent(actor.ActorId, "PriceListUpdated", "PriceList", list.Code,
                $"{req.DiscountPercent}% parent={req.ParentPriceListId} active={req.IsActive}", actor.CorrelationId));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization("CatalogueAdmin");

        lists.MapGet("/{id:long}/entries", async (long id, ICatalogueDbContext db, HttpResponse response, string? search,
            int? page, int? pageSize, CancellationToken ct) =>
        {
            var pg = Math.Max(1, page ?? 1);
            var ps = Math.Clamp(pageSize ?? 25, 1, 200);
            var q = db.PriceListEntries.AsNoTracking().Where(e => e.PriceListId == id);
            if (!string.IsNullOrWhiteSpace(search)) q = q.Where(e => e.Product!.Sku.StartsWith(search.ToUpper()) || e.Product.Name.StartsWith(search));
            var total = await q.CountAsync(ct);
            var items = await q.OrderBy(e => e.Product!.Sku).ThenBy(e => e.MinQuantity).Skip((pg - 1) * ps).Take(ps)
                .Select(e => new EntryDto(e.Id, e.ProductId, e.Product!.Sku, e.Product.Name, e.MinQuantity, e.UnitPrice,
                    e.Product.Prices.Where(p => p.ValidToUtc == null).Select(p => (decimal?)p.Amount).FirstOrDefault(),
                    e.ValidFromUtc, e.ValidToUtc))
                .ToListAsync(ct);
            return Results.Ok(new ProductEndpoints.PagedResult<EntryDto>(items, total, pg, ps));
        }).RequireAuthorization("ReadCatalogue");

        lists.MapPost("/{id:long}/entries", async (long id, AddEntryRequest req, ICatalogueDbContext db, IActorContext actor,
            HybridCache cache, CancellationToken ct) =>
        {
            var list = await db.PriceLists.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (list is null) return Results.NotFound();
            var sku = req.Sku.Trim().ToUpperInvariant();
            var products = await db.Products.Where(p => p.Sku == sku).Select(p => p.Id).Take(2).ToListAsync(ct);
            if (products.Count == 0) return Results.BadRequest(new { error = $"No product with SKU {sku}." });
            if (products.Count > 1) return Results.BadRequest(new { error = $"SKU {sku} exists for several suppliers; use the product's own page." });
            var entry = new PriceListEntry(id, products[0], req.MinQuantity, req.UnitPrice, req.ValidFromUtc ?? DateTime.UtcNow.Date, req.ValidToUtc);
            db.PriceListEntries.Add(entry);
            db.AuditEvents.Add(new AuditEvent(actor.ActorId, "PriceListEntryAdded", "PriceList", list.Code,
                $"{sku} x{req.MinQuantity}+ @ {req.UnitPrice}", actor.CorrelationId));
            await db.SaveChangesAsync(ct);
            await cache.RemoveAsync(CacheTags.Product(products[0]), ct);
            return Results.Created($"/api/pricelists/{id}/entries/{entry.Id}", new { entry.Id });
        }).RequireAuthorization("CatalogueAdmin");

        lists.MapDelete("/{id:long}/entries/{entryId:long}", async (long id, long entryId, ICatalogueDbContext db, IActorContext actor, CancellationToken ct) =>
        {
            var entry = await db.PriceListEntries.FirstOrDefaultAsync(e => e.Id == entryId && e.PriceListId == id, ct);
            if (entry is null) return Results.NotFound();
            db.PriceListEntries.Remove(entry);
            db.AuditEvents.Add(new AuditEvent(actor.ActorId, "PriceListEntryRemoved", "PriceList", id.ToString(), entryId.ToString(), actor.CorrelationId));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization("CatalogueAdmin");

        var customers = api.MapGroup("/customers").WithTags("Pricing");

        customers.MapGet("", async (ICatalogueDbContext db, CancellationToken ct) =>
            Results.Ok(await db.CustomerAccounts.AsNoTracking().OrderBy(c => c.AccountNumber)
                .Select(c => new CustomerDto(c.Id, c.AccountNumber, c.Name, c.PriceListId, c.PriceList != null ? c.PriceList.Code : null, c.IsActive))
                .ToListAsync(ct)))
            .RequireAuthorization("ReadCatalogue");

        customers.MapPost("", async (CreateCustomerRequest req, ICatalogueDbContext db, IActorContext actor, CancellationToken ct) =>
        {
            if (await db.CustomerAccounts.AnyAsync(c => c.AccountNumber == req.AccountNumber.ToUpper(), ct))
                return Results.Conflict(new { error = "Account number already exists." });
            var customer = new CustomerAccount(req.AccountNumber, req.Name, req.PriceListId);
            db.CustomerAccounts.Add(customer);
            db.AuditEvents.Add(new AuditEvent(actor.ActorId, "CustomerCreated", "Customer", customer.AccountNumber, req.Name, actor.CorrelationId));
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/customers/{customer.Id}", new { customer.Id });
        }).RequireAuthorization("CatalogueAdmin");

        customers.MapPut("/{id:long}/pricelist", async (long id, long? priceListId, ICatalogueDbContext db, IActorContext actor, CancellationToken ct) =>
        {
            var customer = await db.CustomerAccounts.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (customer is null) return Results.NotFound();
            customer.AssignPriceList(priceListId);
            db.AuditEvents.Add(new AuditEvent(actor.ActorId, "CustomerPriceListChanged", "Customer", customer.AccountNumber,
                $"priceList={priceListId}", actor.CorrelationId));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization("CatalogueAdmin");

        // Quote: net price + full price ladder for a product as a given customer (or price list) would see it.
        api.MapGet("/pricing/quote", async (long productId, long? customerId, long? priceListId, int? qty,
            ICatalogueDbContext db, PricingService pricing, CancellationToken ct) =>
        {
            var product = await db.Products.AsNoTracking().Where(p => p.Id == productId)
                .Select(p => new
                {
                    p.Id, p.Sku, p.Name,
                    Price = p.Prices.Where(x => x.ValidToUtc == null).Select(x => (decimal?)x.Amount).FirstOrDefault(),
                    Currency = p.Prices.Where(x => x.ValidToUtc == null).Select(x => x.Currency).FirstOrDefault()
                }).FirstOrDefaultAsync(ct);
            if (product is null) return Results.NotFound();
            var (customer, chain) = await pricing.ContextAsync(customerId, priceListId, ct);
            var quote = (await pricing.QuoteAsync([(product.Id, product.Price, product.Currency ?? "GBP")], chain, qty ?? 1, ct))[product.Id];
            return Results.Ok(new
            {
                product = new { product.Id, product.Sku, product.Name },
                customer = customer is null ? null : new { customer.Id, customer.AccountNumber, customer.Name },
                chain = chain.Select(l => new { l.Code, l.Name, kind = l.Kind.ToString(), l.DiscountPercent }),
                quote
            });
        }).RequireAuthorization("ReadCatalogue").WithTags("Pricing");
    }
}
