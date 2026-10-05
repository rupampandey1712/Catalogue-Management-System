using System.Globalization;
using Catalogue.Domain;
using Microsoft.EntityFrameworkCore;

namespace Catalogue.Application;

/// <summary>One supplier's offer priced for a specific buyer and quantity.</summary>
public sealed record OfferQuote(
    long OfferId, long ProductId, long SupplierId, string SupplierCode, string SupplierName, string SupplierSku, bool IsContentOwner,
    string Availability, int? LeadTimeDays, int MinOrderQuantity, int PackSize, int RequestedQuantity, int OrderQuantity,
    string Currency, decimal BasePrice, decimal? ListPrice, decimal NetUnitPrice, decimal TotalDiscountPercent, decimal LineTotal,
    string Explanation, bool Purchasable, string? NotPurchasableReason, bool IsCheapest);

/// <summary>
/// Prices every supplier offer of a product for a buyer, so the buyer can source from whichever
/// seller is cheapest after all discounts:
///   base     = the offer's own selling price (falls back to the product's list price)
///   platform = the buyer's price-list chain: negotiated quantity breaks apply to the content owner's
///              offer (they are agreements on that product), the tier/trade % applies to every offer
///   supplier = the best <see cref="SupplierDiscount"/> that seller grants this buyer (or all buyers)
/// Quantities are rounded up to the offer's MOQ and pack size; a company never buys from itself.
/// </summary>
public sealed class MarketplacePricing(ICatalogueDbContext db)
{
    public async Task<IReadOnlyDictionary<long, IReadOnlyList<OfferQuote>>> CompareAsync(
        IReadOnlyDictionary<long, int> quantities, long? customerAccountId, long? ownSupplierId, CancellationToken ct)
    {
        var productIds = quantities.Keys.ToList();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id))
            .Select(p => new
            {
                p.Id, p.Status,
                List = p.Prices.Where(x => x.ValidToUtc == null).OrderByDescending(x => x.ValidFromUtc).Select(x => (decimal?)x.Amount).FirstOrDefault(),
                Currency = p.Prices.Where(x => x.ValidToUtc == null).OrderByDescending(x => x.ValidFromUtc).Select(x => x.Currency).FirstOrDefault()
            }).ToDictionaryAsync(p => p.Id, ct);
        var offers = await db.SupplierOffers.AsNoTracking().Where(o => productIds.Contains(o.ProductId))
            .Select(o => new
            {
                o.Id, o.ProductId, o.SupplierId, o.SupplierSku, o.Price, o.Currency, o.Availability, o.LeadTimeDays,
                o.MinOrderQuantity, o.PackSize, o.IsPreferred, SupplierCode = o.Supplier!.Code, SupplierName = o.Supplier.Name, SupplierActive = o.Supplier.IsActive
            }).ToListAsync(ct);

        // Buyer's platform price-list chain and the explicit entries on it.
        IReadOnlyList<PriceList> chain = [];
        if (customerAccountId is { } cid)
        {
            var start = await db.CustomerAccounts.Where(c => c.Id == cid).Select(c => c.PriceListId).FirstOrDefaultAsync(ct);
            chain = PriceResolver.Chain(start, await db.PriceLists.AsNoTracking().ToDictionaryAsync(l => l.Id, ct));
        }
        var listIds = chain.Select(l => l.Id).ToList();
        var entries = listIds.Count == 0 ? [] : await db.PriceListEntries.AsNoTracking()
            .Where(e => listIds.Contains(e.PriceListId) && productIds.Contains(e.ProductId)).ToListAsync(ct);
        var supplierIds = offers.Select(o => o.SupplierId).Distinct().ToList();
        var now = DateTime.UtcNow;
        var discounts = (await db.SupplierDiscounts.AsNoTracking().Where(d => supplierIds.Contains(d.SupplierId) && d.IsActive).ToListAsync(ct))
            .Where(d => d.AppliesTo(customerAccountId, now)).GroupBy(d => d.SupplierId)
            .ToDictionary(g => g.Key, g => g.MaxBy(d => d.DiscountPercent)!);
        var tier = chain.FirstOrDefault(l => l.IsActive && l.DiscountPercent > 0);

        var result = new Dictionary<long, IReadOnlyList<OfferQuote>>();
        foreach (var pid in productIds)
        {
            if (!products.TryGetValue(pid, out var product)) { result[pid] = []; continue; }
            var qty = Math.Max(1, quantities[pid]);
            var quotes = new List<OfferQuote>();
            foreach (var o in offers.Where(x => x.ProductId == pid))
            {
                var orderQty = RoundUp(Math.Max(qty, o.MinOrderQuantity), Math.Max(1, o.PackSize));
                var basePrice = o.Price ?? product.List;
                var steps = new List<string>();
                string? blocked = null;
                if (basePrice is null) blocked = "No price";
                else if (product.Status != ProductStatus.Active) blocked = "Product not active";
                else if (!o.SupplierActive) blocked = "Supplier inactive";
                else if (o.Availability == Availability.Discontinued) blocked = "Discontinued";
                else if (ownSupplierId == o.SupplierId) blocked = "Your own offer";

                decimal net = basePrice ?? 0;
                if (basePrice is { } b)
                {
                    steps.Add($"{(o.Price is null ? "List" : o.SupplierCode)} {Money(b)}");
                    if (o.IsPreferred && chain.Count > 0)
                    {
                        var q = PriceResolver.Resolve(pid, b, o.Currency, chain, entries, orderQty, now);
                        if (q.NetUnitPrice is { } n && n != b) { net = n; steps.Add($"{q.PriceListCode ?? "list"} {q.Source.Split(' ')[0]} → {Money(n)}"); }
                    }
                    else if (tier is not null)
                    {
                        net = Math.Round(b * (1 - tier.DiscountPercent / 100m), 2, MidpointRounding.AwayFromZero);
                        steps.Add($"{tier.Code} −{tier.DiscountPercent:0.##}%");
                    }
                    if (discounts.TryGetValue(o.SupplierId, out var sd))
                    {
                        net = Math.Round(net * (1 - sd.DiscountPercent / 100m), 2, MidpointRounding.AwayFromZero);
                        steps.Add($"{o.SupplierCode} discount −{sd.DiscountPercent:0.##}%");
                    }
                }
                var discountPct = basePrice is > 0 ? Math.Round((basePrice.Value - net) / basePrice.Value * 100, 1) : 0;
                quotes.Add(new OfferQuote(o.Id, pid, o.SupplierId, o.SupplierCode, o.SupplierName, o.SupplierSku, o.IsPreferred,
                    o.Availability.ToString(), o.LeadTimeDays, o.MinOrderQuantity, o.PackSize, qty, orderQty, o.Currency,
                    basePrice ?? 0, product.List, net, discountPct, Math.Round(net * orderQty, 2),
                    string.Join(" → ", steps) + $" = {Money(net)}", blocked is null, blocked, false));
            }
            // Cheapest by line total (pack rounding can make a lower unit price cost more), then lead time.
            var best = quotes.Where(q => q.Purchasable).OrderBy(q => q.LineTotal).ThenBy(q => q.LeadTimeDays ?? 99).FirstOrDefault();
            result[pid] = quotes.Select(q => q with { IsCheapest = best is not null && q.OfferId == best.OfferId })
                .OrderBy(q => !q.Purchasable).ThenBy(q => q.LineTotal).ToList();
        }
        return result;
    }

    private static int RoundUp(int qty, int pack) => (qty + pack - 1) / pack * pack;
    private static string Money(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);
}

public sealed record BasketLine(long ProductId, int Quantity, long? OfferId);
public sealed record PricedLine(long ProductId, string ProductName, string Sku, int Quantity, OfferQuote? Chosen,
    IReadOnlyList<OfferQuote> Alternatives, decimal? OwnerLineTotal, string? Problem);
public sealed record SupplierGroup(long SupplierId, string SupplierCode, string SupplierName, string Currency, int Lines, decimal Subtotal);
public sealed record BasketQuote(IReadOnlyList<PricedLine> Lines, IReadOnlyList<SupplierGroup> Suppliers, decimal Total,
    decimal? OwnerOnlyTotal, decimal? Savings, string Currency);

/// <summary>Prices a basket (cheapest offer per line unless the buyer picked one) and turns it into POs.</summary>
public sealed class SourcingService(ICatalogueDbContext db, MarketplacePricing pricing)
{
    public const int MaxLines = 200;

    public async Task<BasketQuote> QuoteAsync(IReadOnlyList<BasketLine> lines, long? customerAccountId, long? ownSupplierId, CancellationToken ct)
    {
        if (lines.Count > MaxLines) throw new ArgumentException($"A basket can hold at most {MaxLines} lines.");
        var merged = lines.Where(l => l.Quantity > 0).GroupBy(l => l.ProductId)
            .ToDictionary(g => g.Key, g => (Qty: g.Sum(x => x.Quantity), Offer: g.Last().OfferId));
        var quotes = await pricing.CompareAsync(merged.ToDictionary(m => m.Key, m => m.Value.Qty), customerAccountId, ownSupplierId, ct);
        var names = await db.Products.AsNoTracking().Where(p => merged.Keys.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.Sku }).ToDictionaryAsync(p => p.Id, ct);

        var priced = new List<PricedLine>();
        foreach (var (pid, (qty, offerId)) in merged)
        {
            var options = quotes.GetValueOrDefault(pid) ?? [];
            var chosen = offerId is { } wanted
                ? options.FirstOrDefault(o => o.OfferId == wanted && o.Purchasable) ?? options.FirstOrDefault(o => o.IsCheapest)
                : options.FirstOrDefault(o => o.IsCheapest);
            var owner = options.FirstOrDefault(o => o.IsContentOwner && o.Purchasable);
            names.TryGetValue(pid, out var n);
            priced.Add(new PricedLine(pid, n?.Name ?? $"#{pid}", n?.Sku ?? "", qty, chosen, options, owner?.LineTotal,
                n is null ? "Product not found." : chosen is null ? "No purchasable offer." :
                offerId is not null && chosen.OfferId != offerId ? "Selected offer unavailable - switched to the cheapest." : null));
        }
        var groups = priced.Where(l => l.Chosen is not null).GroupBy(l => l.Chosen!.SupplierId)
            .Select(g => new SupplierGroup(g.Key, g.First().Chosen!.SupplierCode, g.First().Chosen!.SupplierName, g.First().Chosen!.Currency,
                g.Count(), g.Sum(l => l.Chosen!.LineTotal))).OrderByDescending(g => g.Subtotal).ToList();
        var total = groups.Sum(g => g.Subtotal);
        var ownerTotal = priced.All(l => l.OwnerLineTotal is not null) ? priced.Sum(l => l.OwnerLineTotal!.Value) : (decimal?)null;
        return new BasketQuote(priced, groups, total, ownerTotal, ownerTotal is null ? null : ownerTotal - total,
            groups.Select(g => g.Currency).FirstOrDefault() ?? "GBP");
    }

    /// <summary>Re-prices on the server (never trusts client prices) and creates one PO per supplier.</summary>
    public async Task<IReadOnlyList<PurchaseOrder>> PlaceAsync(IReadOnlyList<BasketLine> lines, long buyerOrganizationId, long? customerAccountId,
        long? ownSupplierId, string createdBy, string? buyerReference, string? note, CancellationToken ct)
    {
        var quote = await QuoteAsync(lines, customerAccountId, ownSupplierId, ct);
        var problems = quote.Lines.Where(l => l.Chosen is null).Select(l => $"{l.Sku}: {l.Problem}").ToList();
        if (quote.Lines.Count == 0) throw new InvalidOperationException("The basket is empty.");
        if (problems.Count > 0) throw new InvalidOperationException("Some lines cannot be ordered: " + string.Join("; ", problems));
        if (quote.Lines.Select(l => l.Chosen!.Currency).Distinct().Count() > 1)
            throw new InvalidOperationException("Mixed-currency baskets are not supported; order each currency separately.");

        var orders = new List<PurchaseOrder>();
        foreach (var group in quote.Lines.GroupBy(l => l.Chosen!.SupplierId))
        {
            var number = $"PO-{DateTime.UtcNow:yyMMdd}-{Convert.ToHexString(Guid.NewGuid().ToByteArray()[..3])}";
            var po = new PurchaseOrder(number, buyerOrganizationId, customerAccountId, group.Key, group.First().Chosen!.Currency,
                createdBy, buyerReference, note);
            foreach (var l in group)
                po.AddLine(new PurchaseOrderLine(l.ProductId, l.Chosen!.OfferId, l.Chosen.SupplierSku, l.ProductName, l.Chosen.OrderQuantity,
                    l.Chosen.NetUnitPrice, l.Chosen.BasePrice, l.Chosen.Currency, l.Chosen.Explanation));
            db.PurchaseOrders.Add(po);
            orders.Add(po);
        }
        db.AuditEvents.Add(new AuditEvent(createdBy, "PurchaseOrdersPlaced", "PurchaseOrder", string.Join(",", orders.Select(o => o.Number)),
            $"{orders.Count} PO(s), total {quote.Total:0.00} {quote.Currency}, savings {quote.Savings:0.00}", null));
        await db.SaveChangesAsync(ct);
        return orders;
    }
}
