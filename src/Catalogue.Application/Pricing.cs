using Catalogue.Domain;
using Microsoft.EntityFrameworkCore;

namespace Catalogue.Application;

public sealed record PriceBreak(int MinQuantity, decimal UnitPrice);

/// <summary>Net price for one product, customer and quantity, with how it was derived.</summary>
public sealed record PriceQuote(
    long ProductId,
    string Currency,
    decimal? ListPrice,
    decimal? NetUnitPrice,
    int Quantity,
    string Source,
    string? PriceListCode,
    decimal? SavingPercent,
    IReadOnlyList<PriceBreak> Breaks)
{
    public decimal? LineTotal => NetUnitPrice * Quantity;
}

/// <summary>
/// Resolves B2B prices. The customer's list is tried first, then each parent in its fallback chain
/// (customer → tier → trade). On each list an explicit entry with the highest qualifying quantity
/// break wins; if the chain has no entry, the first list with a percentage discount applies to the
/// list price; with no lists at all the list price stands.
/// </summary>
public static class PriceResolver
{
    public static PriceQuote Resolve(long productId, decimal? listPrice, string currency,
        IReadOnlyList<PriceList> chain, IReadOnlyList<PriceListEntry> entries, int quantity, DateTime nowUtc)
    {
        quantity = Math.Max(1, quantity);
        foreach (var list in chain.Where(l => l.IsActive))
        {
            var valid = entries.Where(e => e.PriceListId == list.Id && e.ProductId == productId && e.IsValidAt(nowUtc))
                .OrderBy(e => e.MinQuantity).ToList();
            if (valid.Count == 0) continue;
            var hit = valid.LastOrDefault(e => e.MinQuantity <= quantity);
            if (hit is null) continue; // breaks start above this quantity; keep falling back
            return Quote(productId, listPrice, list.Currency, hit.UnitPrice, quantity, $"{list.Kind} price list", list.Code,
                valid.Select(e => new PriceBreak(e.MinQuantity, e.UnitPrice)).ToList());
        }

        var discountList = chain.FirstOrDefault(l => l.IsActive && l.DiscountPercent > 0);
        if (discountList is not null && listPrice is { } lp)
        {
            var net = Math.Round(lp * (1 - discountList.DiscountPercent / 100m), 2, MidpointRounding.AwayFromZero);
            return Quote(productId, listPrice, currency, net, quantity, $"{discountList.DiscountPercent:0.##}% {discountList.Kind.ToString().ToLowerInvariant()} discount",
                discountList.Code, [new PriceBreak(1, net)]);
        }

        return Quote(productId, listPrice, currency, listPrice, quantity, "List price", null,
            listPrice is { } p ? [new PriceBreak(1, p)] : []);
    }

    private static PriceQuote Quote(long productId, decimal? list, string currency, decimal? net, int qty, string source,
        string? code, IReadOnlyList<PriceBreak> breaks) =>
        new(productId, currency, list, net, qty, source, code,
            list is > 0 && net is { } n ? Math.Round((list.Value - n) / list.Value * 100, 1) : null, breaks);

    /// <summary>Customer list followed by its parents; stops on cycles and after 10 levels.</summary>
    public static IReadOnlyList<PriceList> Chain(long? startPriceListId, IReadOnlyDictionary<long, PriceList> lists)
    {
        var chain = new List<PriceList>();
        var seen = new HashSet<long>();
        var id = startPriceListId;
        while (id is { } current && chain.Count < 10 && seen.Add(current) && lists.TryGetValue(current, out var list))
        {
            chain.Add(list);
            id = list.ParentPriceListId;
        }
        return chain;
    }
}

/// <summary>Loads the data PriceResolver needs, in two queries per page of products.</summary>
public sealed class PricingService(ICatalogueDbContext db)
{
    public async Task<(CustomerAccount? Customer, IReadOnlyList<PriceList> Chain)> ContextAsync(
        long? customerId, long? priceListId, CancellationToken ct)
    {
        CustomerAccount? customer = null;
        if (customerId is { } cid)
        {
            customer = await db.CustomerAccounts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct)
                ?? throw new KeyNotFoundException($"Customer {cid} not found.");
            priceListId = customer.PriceListId;
        }
        if (priceListId is null) return (customer, []);
        var lists = await db.PriceLists.AsNoTracking().ToDictionaryAsync(l => l.Id, ct);
        return (customer, PriceResolver.Chain(priceListId, lists));
    }

    public async Task<IReadOnlyDictionary<long, PriceQuote>> QuoteAsync(
        IReadOnlyCollection<(long ProductId, decimal? ListPrice, string Currency)> products,
        IReadOnlyList<PriceList> chain, int quantity, CancellationToken ct)
    {
        var listIds = chain.Select(l => l.Id).ToList();
        var productIds = products.Select(p => p.ProductId).ToList();
        var entries = listIds.Count == 0 ? [] : await db.PriceListEntries.AsNoTracking()
            .Where(e => listIds.Contains(e.PriceListId) && productIds.Contains(e.ProductId))
            .ToListAsync(ct);
        var now = DateTime.UtcNow;
        return products.ToDictionary(p => p.ProductId,
            p => PriceResolver.Resolve(p.ProductId, p.ListPrice, p.Currency, chain, entries, quantity, now));
    }
}
