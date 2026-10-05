using Catalogue.Domain;
using Microsoft.EntityFrameworkCore;

namespace Catalogue.Application;

public sealed record ApprovalFilter(ChangeKind? Kind, long? SupplierId, long? ImportId);

/// <summary>
/// Applies catalogue-admin decisions on supplier-fed changes. Approving a new product publishes it;
/// approving a price change closes the current list price and starts the new one. Rejecting leaves
/// the catalogue as it was (a rejected new product is deactivated).
/// </summary>
public sealed class ApprovalService(ICatalogueDbContext db, IActorContext actor)
{
    public const int MaxPerCall = 20_000;
    private const int Batch = 1_000;

    public async Task<int> DecideAsync(IReadOnlyCollection<long> ids, bool approve, string? note, CancellationToken ct)
    {
        var done = 0;
        foreach (var chunk in ids.Distinct().Chunk(Batch))
        {
            var requests = await db.ProductChangeRequests
                .Where(r => chunk.Contains(r.Id) && r.Status == ChangeStatus.Pending).ToListAsync(ct);
            done += await ApplyAsync(requests, approve, note, ct);
        }
        await AuditAsync(approve, done, note, "selection", ct);
        return done;
    }

    /// <summary>Decides every pending request matching the filter, up to <see cref="MaxPerCall"/> per call.</summary>
    public async Task<(int Decided, int Remaining)> DecideMatchingAsync(ApprovalFilter filter, bool approve, string? note, CancellationToken ct)
    {
        var done = 0;
        while (done < MaxPerCall)
        {
            var requests = await Pending(filter).OrderBy(r => r.Id).Take(Batch).ToListAsync(ct);
            if (requests.Count == 0) break;
            done += await ApplyAsync(requests, approve, note, ct);
        }
        await AuditAsync(approve, done, note, $"filter kind={filter.Kind} supplier={filter.SupplierId} import={filter.ImportId}", ct);
        return (done, await Pending(filter).CountAsync(ct));
    }

    public IQueryable<ProductChangeRequest> Pending(ApprovalFilter f)
    {
        var q = db.ProductChangeRequests.Where(r => r.Status == ChangeStatus.Pending);
        if (f.Kind is { } k) q = q.Where(r => r.Kind == k);
        if (f.SupplierId is { } s) q = q.Where(r => r.SupplierId == s);
        if (f.ImportId is { } i) q = q.Where(r => r.ImportId == i);
        return q;
    }

    private async Task<int> ApplyAsync(List<ProductChangeRequest> requests, bool approve, string? note, CancellationToken ct)
    {
        if (requests.Count == 0) return 0;
        var productIds = requests.Select(r => r.ProductId).Distinct().ToList();
        var products = await db.Products
            .Include(p => p.Prices.Where(x => x.ValidToUtc == null))
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);
        var now = DateTime.UtcNow;

        // Oldest first, so a later price request for the same product ends up as the current price.
        foreach (var request in requests.OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.Id))
        {
            request.Decide(approve, actor.ActorId, note);
            if (!products.TryGetValue(request.ProductId, out var product)) continue;

            switch (request.Kind)
            {
                case ChangeKind.NewProduct when approve && product.Status == ProductStatus.PendingApproval:
                    product.Activate();
                    break;
                case ChangeKind.NewProduct when !approve && product.Status == ProductStatus.PendingApproval:
                    product.Deactivate();
                    break;
                case ChangeKind.PriceChange when approve && request.NewPrice is { } price:
                    var currency = request.Currency ?? "GBP";
                    var active = product.Prices.FirstOrDefault(p => p.Currency == currency && p.ValidToUtc is null);
                    if (active?.Amount == price) break;
                    active?.Close(now);
                    var next = ProductPrice.For(product, currency, price, now);
                    product.Prices.Add(next);
                    break;
            }
        }
        await db.SaveChangesAsync(ct);
        return requests.Count;
    }

    private async Task AuditAsync(bool approve, int count, string? note, string scope, CancellationToken ct)
    {
        if (count == 0) return;
        db.AuditEvents.Add(new AuditEvent(actor.ActorId, approve ? "ChangesApproved" : "ChangesRejected", "ProductChangeRequest",
            scope, $"{count} change(s){(note is null ? "" : $": {note}")}", actor.CorrelationId));
        await db.SaveChangesAsync(ct);
    }
}
