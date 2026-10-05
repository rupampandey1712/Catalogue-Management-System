using Catalogue.Application;
using Catalogue.Domain;
using Catalogue.Web.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace Catalogue.Web.Endpoints;

/// <summary>
/// B2B marketplace: every company can sell (its supplier offers) and buy (from whichever seller is
/// cheapest after its discounts). Baskets become one purchase order per seller; sellers accept,
/// reject and ship from the seller portal.
/// </summary>
public static class MarketplaceEndpoints
{
    public sealed record QuoteRequest(IReadOnlyList<BasketLine> Lines, long? CustomerAccountId);
    public sealed record PlaceOrderRequest(IReadOnlyList<BasketLine> Lines, string? BuyerReference, string? Note);
    public sealed record StatusRequest(string Status, string? Note);
    public sealed record OrderSummaryDto(long Id, string Number, string Status, string BuyerName, long SupplierId, string SupplierCode,
        string SupplierName, string Currency, decimal Subtotal, decimal ListValue, int Lines, string? BuyerReference,
        DateTime CreatedAtUtc, DateTime UpdatedAtUtc, string CreatedBy, string PaymentStatus);
    public sealed record SellerOfferDto(long Id, long ProductId, string Sku, string ProductName, string? Brand, string SupplierSku,
        decimal? Price, decimal? ListPrice, decimal? Cost, string Currency, int? Stock, int? LeadTimeDays, int MinOrderQuantity, int PackSize,
        string Availability, bool IsContentOwner, decimal? CheapestCompetitorPrice, int Competitors);
    public sealed record UpdateOfferRequest(decimal? Price, int? Quantity, int? LeadTimeDays, int? MinOrderQuantity, int? PackSize, bool Discontinued);
    public sealed record DiscountDto(long Id, long? CustomerAccountId, string? CustomerName, decimal DiscountPercent, string? Note,
        DateTime ValidFromUtc, DateTime? ValidToUtc, bool IsActive);
    public sealed record CreateDiscountRequest(long? CustomerAccountId, decimal DiscountPercent, DateTime? ValidToUtc, string? Note);
    public sealed record ListProductRequest(ProductEndpoints.UpsertProductRequest Product, int? LeadTimeDays, int? MinOrderQuantity, int? PackSize);

    public static void Map(IEndpointRouteBuilder api)
    {
        var market = api.MapGroup("/marketplace").WithTags("Marketplace");

        // Compare all sellers' offers for one product, priced for the buyer.
        market.MapGet("/products/{productId:long}/offers", async (long productId, int? qty, long? customerAccountId, CurrentUser me,
            MarketplacePricing pricing, CancellationToken ct) =>
        {
            var customer = BuyerAccount(me, customerAccountId);
            var result = await pricing.CompareAsync(new Dictionary<long, int> { [productId] = qty ?? 1 }, customer, me.SupplierId, ct);
            return Results.Ok(new { customerAccountId = customer, offers = result[productId] });
        }).RequireAuthorization("Marketplace");

        market.MapPost("/quote", async (QuoteRequest req, CurrentUser me, SourcingService sourcing, CancellationToken ct) =>
            Results.Ok(await sourcing.QuoteAsync(req.Lines, BuyerAccount(me, req.CustomerAccountId), me.SupplierId, ct)))
            .RequireAuthorization("Marketplace");

        market.MapPost("/orders", async (PlaceOrderRequest req, CurrentUser me, ICatalogueDbContext db, SourcingService sourcing,
            CancellationToken ct) =>
        {
            if (me.OrganizationId is not { } orgId) return Results.Problem(statusCode: 403, title: "Only company users can place orders.");
            var org = await db.Organizations.AsNoTracking().FirstAsync(o => o.Id == orgId, ct);
            if (!org.CanBuy || org.CustomerAccountId is null) return Results.Problem(statusCode: 403, title: "Your company is not set up to buy.");
            try
            {
                var orders = await sourcing.PlaceAsync(req.Lines, orgId, org.CustomerAccountId, org.SupplierId, me.Name, req.BuyerReference, req.Note, ct);
                return Results.Ok(orders.Select(o => new { o.Id, o.Number, o.SupplierId, o.Subtotal, o.Currency, lines = o.Lines.Count }));
            }
            catch (InvalidOperationException ex) { return Results.Problem(statusCode: 422, title: "Order not placed", detail: ex.Message); }
        }).RequireAuthorization("Buyer");

        // role=buyer: orders my company placed. role=seller: orders to my company (staff: managed suppliers).
        market.MapGet("/orders", async (string? role, string? status, string? payment, long? supplierId, int? page, int? pageSize, CurrentUser me,
            ICatalogueDbContext db, CancellationToken ct) =>
        {
            var pg = Math.Max(1, page ?? 1);
            var ps = Math.Clamp(pageSize ?? 25, 1, 100);
            var q = db.PurchaseOrders.AsNoTracking();
            if (role == "seller")
            {
                if (me.SupplierId is { } sid && !me.IsPlatformStaff) q = q.Where(o => o.SupplierId == sid);
                else if (me.IsPlatformStaff)
                {
                    // Staff run the suppliers that are not their own company on the platform (legacy EDI/CSV feeds).
                    var managed = db.Suppliers.Where(s => !db.Organizations.Any(o => o.SupplierId == s.Id && !o.IsPlatformOwner)).Select(s => s.Id);
                    q = supplierId is { } only ? q.Where(o => o.SupplierId == only) : q.Where(o => managed.Contains(o.SupplierId) || o.SupplierId == me.SupplierId);
                }
                else return Results.Ok(new ProductEndpoints.PagedResult<OrderSummaryDto>([], 0, pg, ps));
            }
            else
            {
                if (me.OrganizationId is not { } oid) return Results.Ok(new ProductEndpoints.PagedResult<OrderSummaryDto>([], 0, pg, ps));
                q = q.Where(o => o.BuyerOrganizationId == oid);
            }
            if (Enum.TryParse<PurchaseOrderStatus>(status, true, out var st)) q = q.Where(o => o.Status == st);
            if (Enum.TryParse<OrderPaymentStatus>(payment, true, out var pay)) q = q.Where(o => o.PaymentStatus == pay);
            var total = await q.CountAsync(ct);
            var items = await q.OrderByDescending(o => o.CreatedAtUtc).Skip((pg - 1) * ps).Take(ps)
                .Select(o => new OrderSummaryDto(o.Id, o.Number, o.Status.ToString(), o.BuyerOrganization!.Name, o.SupplierId, o.Supplier!.Code,
                    o.Supplier.Name, o.Currency, o.Subtotal, o.ListValue, o.Lines.Count, o.BuyerReference, o.CreatedAtUtc, o.UpdatedAtUtc, o.CreatedBy, o.PaymentStatus.ToString()))
                .ToListAsync(ct);
            return Results.Ok(new ProductEndpoints.PagedResult<OrderSummaryDto>(items, total, pg, ps));
        }).RequireAuthorization("Marketplace");

        market.MapGet("/orders/{id:long}", async (long id, CurrentUser me, ICatalogueDbContext db, CancellationToken ct) =>
        {
            var o = await db.PurchaseOrders.AsNoTracking().Include(x => x.Lines).Include(x => x.Supplier).Include(x => x.BuyerOrganization)
                .FirstOrDefaultAsync(x => x.Id == id, ct);
            if (o is null) return Results.NotFound();
            var access = await AccessAsync(o, me, db, ct);
            if (access == Access.None) return Results.NotFound(); // don't reveal other companies' orders
            var payments = await PaymentEndpoints.Project(db.Payments.Where(p => p.PurchaseOrderId == o.Id).OrderByDescending(p => p.CreatedAtUtc)).ToListAsync(ct);
            return Results.Ok(new
            {
                o.Id, o.Number, status = o.Status.ToString(), paymentStatus = o.PaymentStatus.ToString(), o.PaidAtUtc,
                canPay = access is Access.Buyer && o.CanBePaid, payments, o.Currency, o.Subtotal, o.ListValue, o.BuyerReference, o.Note, o.StatusNote,
                o.CreatedAtUtc, o.UpdatedAtUtc, o.CreatedBy,
                buyer = new { o.BuyerOrganization!.Id, o.BuyerOrganization.Name },
                supplier = new { o.Supplier!.Id, o.Supplier.Code, o.Supplier.Name },
                canAct = new { seller = access is Access.Seller or Access.Staff, buyer = access is Access.Buyer or Access.Staff },
                lines = o.Lines.Select(l => new { l.Id, l.ProductId, l.SupplierSku, l.Description, l.Quantity, l.UnitPrice, l.ListUnitPrice, l.LineTotal, l.PriceExplanation })
            });
        }).RequireAuthorization("Marketplace");

        market.MapPost("/orders/{id:long}/status", async (long id, StatusRequest req, CurrentUser me, ICatalogueDbContext db,
            IActorContext actor, OrderStatusService statusService, CancellationToken ct) =>
        {
            var o = await db.PurchaseOrders.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (o is null) return Results.NotFound();
            if (!Enum.TryParse<PurchaseOrderStatus>(req.Status, true, out var next)) return Results.BadRequest(new { error = $"Unknown status '{req.Status}'." });
            var access = await AccessAsync(o, me, db, ct);
            var sellerAction = next is PurchaseOrderStatus.Accepted or PurchaseOrderStatus.Rejected or PurchaseOrderStatus.Shipped;
            if (access == Access.None || (sellerAction && access == Access.Buyer) || (next == PurchaseOrderStatus.Cancelled && access == Access.Seller))
                return Results.Problem(statusCode: 403, title: "You cannot change this order.");
            // Money taken online goes back through the same gateway; if the refund fails nothing is saved.
            try { await statusService.ChangeAsync(o, next, req.Note, actor.ActorId, actor.CorrelationId, ct); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (PaymentException ex) { return Results.Problem(statusCode: ex.StatusCode, title: "Refund failed", detail: ex.Message); }
            return Results.Ok(new { o.Id, status = o.Status.ToString(), paymentStatus = o.PaymentStatus.ToString() });
        }).RequireAuthorization("Marketplace");

        // ----- seller portal -----
        var seller = api.MapGroup("/seller").WithTags("Seller portal").RequireAuthorization("Seller");

        seller.MapGet("/offers", async (CurrentUser me, ICatalogueDbContext db, long? supplierId, string? search, bool? undercut,
            int? page, int? pageSize, CancellationToken ct) =>
        {
            var sid = SellerSupplier(me, supplierId);
            if (sid is null) return Results.Problem(statusCode: 403, title: "Your company is not set up to sell.");
            var pg = Math.Max(1, page ?? 1);
            var ps = Math.Clamp(pageSize ?? 25, 1, 100);
            var q = db.SupplierOffers.AsNoTracking().Where(o => o.SupplierId == sid);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var t = search.Trim().ToUpperInvariant();
                q = q.Where(o => o.SupplierSku.StartsWith(t) || o.Product!.Name.StartsWith(search) || o.Product.Sku.StartsWith(t));
            }
            if (undercut == true) // offers where another seller is cheaper
                q = q.Where(o => db.SupplierOffers.Any(c => c.ProductId == o.ProductId && c.SupplierId != sid && c.Price != null && c.Price < o.Price));
            var total = await q.CountAsync(ct);
            var items = await q.OrderBy(o => o.SupplierSku).Skip((pg - 1) * ps).Take(ps)
                .Select(o => new SellerOfferDto(o.Id, o.ProductId, o.Product!.Sku, o.Product.Name, o.Product.Brand, o.SupplierSku, o.Price,
                    o.Product.Prices.Where(p => p.ValidToUtc == null).Select(p => (decimal?)p.Amount).FirstOrDefault(),
                    o.Cost, o.Currency,
                    db.Inventories.Where(i => i.ProductId == o.ProductId && i.SupplierId == sid).Select(i => (int?)i.Quantity).FirstOrDefault(),
                    o.LeadTimeDays, o.MinOrderQuantity, o.PackSize, o.Availability.ToString(), o.IsPreferred,
                    db.SupplierOffers.Where(c => c.ProductId == o.ProductId && c.SupplierId != sid && c.Price != null).Min(c => c.Price),
                    db.SupplierOffers.Count(c => c.ProductId == o.ProductId && c.SupplierId != sid)))
                .ToListAsync(ct);
            return Results.Ok(new ProductEndpoints.PagedResult<SellerOfferDto>(items, total, pg, ps));
        });

        seller.MapPut("/offers/{id:long}", async (long id, UpdateOfferRequest req, CurrentUser me, ICatalogueDbContext db,
            IActorContext actor, HybridCache cache, CancellationToken ct) =>
        {
            var offer = await db.SupplierOffers.FirstOrDefaultAsync(o => o.Id == id, ct);
            if (offer is null || (!me.IsInRole(AppRoles.Admin) && !me.IsInRole(AppRoles.SupplierManager) && offer.SupplierId != me.SupplierId))
                return Results.NotFound();
            if (req.Price is < 0 || req.Quantity is < 0 || req.LeadTimeDays is < 0 or > 365)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["offer"] = ["Price, stock and lead time must be non-negative (lead time ≤ 365)."] });
            offer.UpdateBySeller(req.Price, req.Quantity, req.LeadTimeDays, req.MinOrderQuantity, req.PackSize, req.Discontinued);
            if (req.Quantity is { } qty)
            {
                var inv = await db.Inventories.FirstOrDefaultAsync(i => i.ProductId == offer.ProductId && i.SupplierId == offer.SupplierId, ct);
                if (inv is null) db.Inventories.Add(new Inventory(offer.ProductId, offer.SupplierId, qty)); else inv.SetQuantity(qty);
            }
            db.AuditEvents.Add(new AuditEvent(actor.ActorId, "OfferUpdated", "SupplierOffer", offer.SupplierSku,
                $"price={req.Price} qty={req.Quantity} lead={req.LeadTimeDays}", actor.CorrelationId));
            await db.SaveChangesAsync(ct);
            await cache.RemoveAsync(CacheTags.Product(offer.ProductId), ct);
            return Results.Ok(new { offer.Id, availability = offer.Availability.ToString() });
        });

        // "We can provide products from our end": a seller lists a product. Same GTIN as an existing product
        // => it becomes another offer on that product; otherwise a new product owned by this seller.
        seller.MapPost("/products", async (ListProductRequest req, CurrentUser me, ICatalogueDbContext db, IReferenceDataProvider refProvider,
            IActorContext actor, HybridCache cache, CancellationToken ct) =>
        {
            var sid = SellerSupplier(me, req.Product.SupplierId);
            if (sid is null) return Results.Problem(statusCode: 403, title: "Your company is not set up to sell.");
            var p = req.Product;
            var problems = ProductEndpoints.ValidateProduct(p, await refProvider.GetAsync(ct), out var attributes);
            if (p.Price is null) problems["price"] = ["Set your selling price."];
            if (problems.Count > 0) return Results.ValidationProblem(problems);
            var sku = p.Sku.Trim().ToUpperInvariant();
            if (await db.SupplierOffers.AnyAsync(o => o.SupplierId == sid && o.SupplierSku == sku, ct))
                return Results.Conflict(new { error = $"You already list SKU {sku}." });

            var supplier = await db.Suppliers.FirstAsync(s => s.Id == sid, ct);
            var gtin = string.IsNullOrWhiteSpace(p.Gtin) ? null : Gtin.Normalize(p.Gtin);
            var product = gtin is null ? null : await db.Products.Where(x => x.Gtin == gtin).OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
            var created = product is null;
            if (product is null)
            {
                product = new Product(sku, p.Name, p.Description, sid);
                product.SetIdentity(p.Brand, p.ManufacturerPartNumber, p.Gtin);
                foreach (var a in attributes) product.SetAttribute(a.AttributeId, a.Value);
                foreach (var cid in p.CategoryIds ?? []) product.ProductCategories.Add(new ProductCategory { CategoryId = cid });
                product.Prices.Add(ProductPrice.For(product, p.Currency ?? "GBP", p.Price!.Value, DateTime.UtcNow));
                if (supplier.RequireApprovalForNewProducts) product.MarkPendingApproval(); else product.Activate();
                db.Products.Add(product);
                await db.SaveChangesAsync(ct);
                if (supplier.RequireApprovalForNewProducts) db.ProductChangeRequests.Add(ProductChangeRequest.ForNewProduct(product, sid.Value, null));
            }
            var offer = new SupplierOffer(product, sid.Value, sku);
            offer.MarkPreferred(created);
            offer.SetSellPrice(p.Price);
            offer.UpdateBySeller(p.Price, p.Quantity, req.LeadTimeDays, req.MinOrderQuantity, req.PackSize, false);
            db.SupplierOffers.Add(offer);
            if (p.Quantity is { } qty) db.Inventories.Add(new Inventory(product.Id, sid, qty));
            db.AuditEvents.Add(new AuditEvent(actor.ActorId, created ? "SellerListedProduct" : "SellerAddedOffer", "Product", product.Id.ToString(),
                $"{supplier.Code} {sku} @ {p.Price}", actor.CorrelationId));
            await db.SaveChangesAsync(ct);
            await cache.RemoveByTagAsync(CacheTags.Products, ct);
            return Results.Created($"/api/products/{product.Id}", new
            {
                productId = product.Id, offerId = offer.Id, consolidated = !created, status = product.Status.ToString()
            });
        });

        seller.MapGet("/discounts", async (CurrentUser me, ICatalogueDbContext db, long? supplierId, CancellationToken ct) =>
        {
            var sid = SellerSupplier(me, supplierId);
            if (sid is null) return Results.Problem(statusCode: 403, title: "Your company is not set up to sell.");
            return Results.Ok(await db.SupplierDiscounts.AsNoTracking().Where(d => d.SupplierId == sid).OrderByDescending(d => d.IsActive).ThenByDescending(d => d.Id)
                .Select(d => new DiscountDto(d.Id, d.CustomerAccountId, d.CustomerAccount != null ? d.CustomerAccount.Name : null, d.DiscountPercent,
                    d.Note, d.ValidFromUtc, d.ValidToUtc, d.IsActive)).ToListAsync(ct));
        });

        seller.MapPost("/discounts", async (CreateDiscountRequest req, CurrentUser me, ICatalogueDbContext db, IActorContext actor, CancellationToken ct) =>
        {
            var sid = SellerSupplier(me, null);
            if (sid is null) return Results.Problem(statusCode: 403, title: "Your company is not set up to sell.");
            var d = new SupplierDiscount(sid.Value, req.CustomerAccountId, req.DiscountPercent, DateTime.UtcNow, req.ValidToUtc, req.Note);
            db.SupplierDiscounts.Add(d);
            db.AuditEvents.Add(new AuditEvent(actor.ActorId, "SupplierDiscountCreated", "Supplier", sid.ToString()!,
                $"{req.DiscountPercent}% for {(req.CustomerAccountId?.ToString() ?? "all buyers")}", actor.CorrelationId));
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/seller/discounts/{d.Id}", new { d.Id });
        });

        seller.MapDelete("/discounts/{id:long}", async (long id, CurrentUser me, ICatalogueDbContext db, CancellationToken ct) =>
        {
            var d = await db.SupplierDiscounts.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (d is null || (d.SupplierId != me.SupplierId && !me.IsInRole(AppRoles.Admin))) return Results.NotFound();
            d.Deactivate();
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    /// <summary>Buyers price as their own account; platform staff may preview any account.</summary>
    private static long? BuyerAccount(CurrentUser me, long? requested) =>
        me.IsPlatformStaff && requested is not null ? requested : me.CustomerAccountId ?? (me.IsPlatformStaff ? requested : null);

    /// <summary>Sellers act for their own supplier; platform staff may act for a named supplier.</summary>
    private static long? SellerSupplier(CurrentUser me, long? requested) =>
        (me.IsInRole(AppRoles.Admin) || me.IsInRole(AppRoles.SupplierManager)) && requested is not null ? requested : me.SupplierId;

    internal enum Access { None, Buyer, Seller, Staff }

    internal static async Task<Access> AccessAsync(PurchaseOrder o, CurrentUser me, ICatalogueDbContext db, CancellationToken ct)
    {
        if (me.SupplierId == o.SupplierId && !me.IsInRole(AppRoles.Admin)) return Access.Seller;
        if (me.OrganizationId == o.BuyerOrganizationId && !me.IsInRole(AppRoles.Admin)) return Access.Buyer;
        if (me.IsPlatformStaff)
        {
            // Staff act as seller for suppliers that are not a member company (feeds managed by the platform).
            var memberSupplier = await db.Organizations.AnyAsync(x => x.SupplierId == o.SupplierId && !x.IsPlatformOwner, ct);
            if (!memberSupplier || me.IsInRole(AppRoles.Admin)) return Access.Staff;
        }
        return Access.None;
    }
}
