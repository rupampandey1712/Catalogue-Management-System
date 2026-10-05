using Catalogue.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace Catalogue.Application;

public enum AnalyticsBucket { Hour, Day, Week, Month }

/// <summary>A reporting window: today, 7d, 15d, 1m, 3m, 6m, 1y (UTC), plus the equally long period before it.</summary>
public sealed record AnalyticsWindow(string Range, DateTime FromUtc, DateTime ToUtc, DateTime PreviousFromUtc, AnalyticsBucket Bucket, IReadOnlyList<DateTime> Buckets)
{
    public static readonly string[] Ranges = ["today", "7d", "15d", "1m", "3m", "6m", "1y"];

    public static AnalyticsWindow For(string? range, DateTime nowUtc)
    {
        var today = nowUtc.Date;
        var r = (range ?? "7d").Trim().ToLowerInvariant();
        (DateTime from, AnalyticsBucket bucket) = r switch
        {
            "today" => (today, AnalyticsBucket.Hour),
            "7d" => (today.AddDays(-6), AnalyticsBucket.Day),
            "15d" => (today.AddDays(-14), AnalyticsBucket.Day),
            "1m" => (today.AddDays(-29), AnalyticsBucket.Day),
            "3m" => (today.AddDays(-89), AnalyticsBucket.Day),
            "6m" => (StartOfWeek(today).AddDays(-7 * 25), AnalyticsBucket.Week),
            "1y" => (new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-11), AnalyticsBucket.Month),
            _ => throw new ArgumentException($"Unknown range '{range}'. Use one of: {string.Join(", ", Ranges)}.")
        };
        var buckets = new List<DateTime>();
        for (var b = from; b <= nowUtc; b = Next(b, bucket)) buckets.Add(b);
        if (bucket == AnalyticsBucket.Hour) // show the whole day, future hours stay empty
            for (var b = Next(buckets[^1], bucket); b < today.AddDays(1); b = Next(b, bucket)) buckets.Add(b);
        return new AnalyticsWindow(r, from, nowUtc, from - (nowUtc - from), bucket, buckets);
    }

    public DateTime BucketOf(DateTime t) => Bucket switch
    {
        AnalyticsBucket.Hour => new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc),
        AnalyticsBucket.Day => DateTime.SpecifyKind(t.Date, DateTimeKind.Utc),
        AnalyticsBucket.Week => StartOfWeek(DateTime.SpecifyKind(t.Date, DateTimeKind.Utc)),
        _ => new DateTime(t.Year, t.Month, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private static DateTime Next(DateTime b, AnalyticsBucket bucket) => bucket switch
    {
        AnalyticsBucket.Hour => b.AddHours(1),
        AnalyticsBucket.Day => b.AddDays(1),
        AnalyticsBucket.Week => b.AddDays(7),
        _ => b.AddMonths(1)
    };

    private static DateTime StartOfWeek(DateTime d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7)); // Monday
}

/// <summary>Platform staff see everything; a company sees its own orders and payments (as buyer or seller).</summary>
public sealed record AnalyticsScope(bool Platform, long? OrganizationId, long? SupplierId)
{
    public string Key => Platform ? "platform" : $"org{OrganizationId}-sup{SupplierId}";
}

public sealed record Kpi(string Key, string Label, decimal Value, decimal? Previous, string Unit, IReadOnlyList<decimal> Spark);
public sealed record Series(string Key, string Label, IReadOnlyList<decimal> Values);
public sealed record Slice(string Label, decimal Value);

public sealed record AnalyticsSnapshot(long Products, long ActiveProducts, long Offers, int Suppliers, int ActiveCompanies, int PendingCompanies,
    int Users, int PendingApprovals, long ProductsWithImage, IReadOnlyList<Slice> ProductStatus, IReadOnlyList<Slice> Availability,
    IReadOnlyList<Slice> CategoriesByProducts, IReadOnlyList<Slice> OffersBySupplier, IReadOnlyList<Slice> MediaKinds, IReadOnlyList<Slice> CompanyStatus,
    DateTime ComputedAtUtc);

public sealed record AnalyticsReport(
    string Range, DateTime FromUtc, DateTime ToUtc, string Bucket, IReadOnlyList<DateTime> Labels, string Scope,
    IReadOnlyList<Kpi> Kpis,
    IReadOnlyDictionary<string, IReadOnlyList<Series>> Series,
    IReadOnlyDictionary<string, IReadOnlyList<Slice>> Breakdowns,
    IReadOnlyDictionary<string, IReadOnlyList<Slice>> Rankings,
    AnalyticsSnapshot Snapshot,
    DateTime ComputedAtUtc);

/// <summary>
/// Everything the product does, over time: orders and revenue, payments, imports, catalogue growth, media,
/// approvals, companies and activity. Aggregation is in SQL (per hour or day), rolled up to weeks/months in
/// memory; results are cached briefly because some breakdowns scan millions of rows.
/// </summary>
public sealed class AnalyticsService(ICatalogueDbContext db, HybridCache cache, TimeProvider clock)
{
    public static readonly TimeSpan ReportTtl = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan SnapshotTtl = TimeSpan.FromMinutes(10);

    public async Task<AnalyticsReport> GetAsync(string? range, AnalyticsScope scope, CancellationToken ct)
    {
        var window = AnalyticsWindow.For(range, clock.GetUtcNow().UtcDateTime);
        return await cache.GetOrCreateAsync($"analytics:{window.Range}:{scope.Key}", async token => await BuildAsync(window, scope, token),
            new HybridCacheEntryOptions { Expiration = ReportTtl, LocalCacheExpiration = ReportTtl }, ["analytics"], ct);
    }

    public Task<AnalyticsSnapshot> SnapshotAsync(CancellationToken ct) =>
        cache.GetOrCreateAsync("analytics:snapshot", async token => await BuildSnapshotAsync(token),
            new HybridCacheEntryOptions { Expiration = SnapshotTtl, LocalCacheExpiration = SnapshotTtl }, ["analytics"], ct).AsTask();

    private sealed class Point { public DateTime At { get; init; } public decimal V { get; init; } }
    private sealed record Totals(decimal[] Count, decimal[] Sum, decimal CountTotal, decimal SumTotal, decimal PrevCount, decimal PrevSum);

    private async Task<AnalyticsReport> BuildAsync(AnalyticsWindow w, AnalyticsScope scope, CancellationToken ct)
    {
        var snapshot = await SnapshotAsync(ct);
        var series = new Dictionary<string, IReadOnlyList<Series>>();
        var breakdowns = new Dictionary<string, IReadOnlyList<Slice>>();
        var rankings = new Dictionary<string, IReadOnlyList<Slice>>();
        var kpis = new List<Kpi>();

        // ----- orders & revenue -----
        var orders = db.PurchaseOrders.AsNoTracking();
        if (!scope.Platform) orders = orders.Where(o => o.BuyerOrganizationId == scope.OrganizationId || o.SupplierId == scope.SupplierId);
        var ordersAll = await Totals_(orders.Select(o => new Point { At = o.CreatedAtUtc, V = o.Subtotal }), w, ct);
        var live = orders.Where(o => o.Status != PurchaseOrderStatus.Rejected && o.Status != PurchaseOrderStatus.Cancelled);
        var gmv = await Totals_(live.Select(o => new Point { At = o.CreatedAtUtc, V = o.Subtotal }), w, ct);
        var inRange = orders.Where(o => o.CreatedAtUtc >= w.FromUtc && o.CreatedAtUtc <= w.ToUtc);
        breakdowns["orderStatus"] = (await inRange.GroupBy(o => o.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct))
            .Select(x => new Slice(x.Key.ToString(), x.N)).ToList();
        rankings["topSuppliers"] = (await inRange.Where(o => o.Status != PurchaseOrderStatus.Rejected && o.Status != PurchaseOrderStatus.Cancelled)
            .GroupBy(o => o.Supplier!.Name).Select(g => new { g.Key, V = g.Sum(o => o.Subtotal) })
            .OrderByDescending(x => x.V).Take(8).ToListAsync(ct)).Select(x => new Slice(x.Key, x.V)).ToList();
        rankings["topProducts"] = (await db.PurchaseOrderLines.AsNoTracking()
            .Where(l => inRange.Any(o => o.Id == l.PurchaseOrderId))
            .GroupBy(l => l.Description).Select(g => new { g.Key, V = g.Sum(l => l.Quantity) })
            .OrderByDescending(x => x.V).Take(8).ToListAsync(ct)).Select(x => new Slice(x.Key, x.V)).ToList();
        series["orders"] = [new("orders", "Orders placed", ordersAll.Count), new("gmv", "Order value", gmv.Sum)];
        kpis.Add(new Kpi("orders", "Orders", ordersAll.CountTotal, ordersAll.PrevCount, "count", ordersAll.Count));
        kpis.Add(new Kpi("gmv", "Order value", gmv.SumTotal, gmv.PrevSum, "GBP", gmv.Sum));
        kpis.Add(new Kpi("aov", "Average order", gmv.CountTotal == 0 ? 0 : Math.Round(gmv.SumTotal / gmv.CountTotal, 2),
            gmv.PrevCount == 0 ? null : Math.Round(gmv.PrevSum / gmv.PrevCount, 2), "GBP", []));

        // ----- payments -----
        var payments = db.Payments.AsNoTracking();
        if (!scope.Platform) payments = payments.Where(p => p.PurchaseOrder!.BuyerOrganizationId == scope.OrganizationId || p.PurchaseOrder.SupplierId == scope.SupplierId);
        var collected = await Totals_(payments.Where(p => p.CompletedAtUtc != null && (p.Status == PaymentAttemptStatus.Succeeded || p.Status == PaymentAttemptStatus.Refunded))
            .Select(p => new Point { At = p.CompletedAtUtc!.Value, V = p.Amount }), w, ct);
        var refunded = await Totals_(payments.Where(p => p.RefundedAtUtc != null).Select(p => new Point { At = p.RefundedAtUtc!.Value, V = p.Amount }), w, ct);
        series["payments"] = [new("collected", "Collected", collected.Sum), new("refunded", "Refunded", refunded.Sum)];
        var paymentsInRange = payments.Where(p => p.CreatedAtUtc >= w.FromUtc && p.CreatedAtUtc <= w.ToUtc);
        breakdowns["paymentStatus"] = (await paymentsInRange.GroupBy(p => p.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct))
            .Select(x => new Slice(x.Key.ToString(), x.N)).ToList();
        breakdowns["paymentProvider"] = (await paymentsInRange.Where(p => p.Status == PaymentAttemptStatus.Succeeded || p.Status == PaymentAttemptStatus.Refunded)
            .GroupBy(p => p.Provider).Select(g => new { g.Key, V = g.Sum(p => p.Amount) }).ToListAsync(ct))
            .Select(x => new Slice(x.Key.ToString(), x.V)).ToList();
        kpis.Add(new Kpi("collected", "Payments collected", collected.SumTotal, collected.PrevSum, "GBP", collected.Sum));
        kpis.Add(new Kpi("refunded", "Refunded", refunded.SumTotal, refunded.PrevSum, "GBP", refunded.Sum));

        // ----- catalogue (shared by everyone) -----
        // Millions of rows: per-day counts for the whole table are computed once and shared by every range.
        var created = await ProductTotalsAsync("created", db.Products.AsNoTracking().Select(p => new Point { At = p.CreatedAtUtc, V = 0 }), w, ct);
        var updated = await ProductTotalsAsync("updated", db.Products.AsNoTracking().Select(p => new Point { At = p.UpdatedAtUtc, V = 0 }), w, ct);
        series["catalogue"] = [new("created", "Products added", created.Count), new("updated", "Products updated", updated.Count)];
        kpis.Add(new Kpi("productsAdded", "Products added", created.CountTotal, created.PrevCount, "count", created.Count));
        var media = await Totals_(db.ProductAssets.AsNoTracking().Select(a => new Point { At = a.UploadedAtUtc, V = a.SizeBytes }), w, ct);
        series["media"] = [new("uploads", "Files added", media.Count)];
        breakdowns["mediaKind"] = (await db.ProductAssets.AsNoTracking().Where(a => a.UploadedAtUtc >= w.FromUtc)
            .GroupBy(a => a.Kind).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct)).Select(x => new Slice(x.Key.ToString(), x.N)).ToList();
        kpis.Add(new Kpi("media", "Media files added", media.CountTotal, media.PrevCount, "count", media.Count));

        if (scope.Platform)
        {
            // ----- imports -----
            var imports = db.ImportFiles.AsNoTracking();
            var files = await Totals_(imports.Select(i => new Point { At = i.ReceivedAtUtc, V = i.ProcessedRows }), w, ct);
            var errors = await Totals_(imports.Select(i => new Point { At = i.ReceivedAtUtc, V = i.ErrorRows }), w, ct);
            series["imports"] = [new("files", "Files received", files.Count), new("rows", "Rows processed", files.Sum), new("errors", "Rows rejected", errors.Sum)];
            breakdowns["importStatus"] = (await imports.Where(i => i.ReceivedAtUtc >= w.FromUtc).GroupBy(i => i.Status)
                .Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct)).Select(x => new Slice(x.Key.ToString(), x.N)).ToList();
            kpis.Add(new Kpi("rows", "Rows ingested", files.SumTotal, files.PrevSum, "count", files.Sum));
            kpis.Add(new Kpi("importErrors", "Rows rejected", errors.SumTotal, errors.PrevSum, "count", errors.Sum));

            // ----- approvals -----
            var raised = await Totals_(db.ProductChangeRequests.AsNoTracking().Select(c => new Point { At = c.CreatedAtUtc, V = 0 }), w, ct);
            var decided = await Totals_(db.ProductChangeRequests.AsNoTracking().Where(c => c.DecidedAtUtc != null)
                .Select(c => new Point { At = c.DecidedAtUtc!.Value, V = 0 }), w, ct);
            series["approvals"] = [new("raised", "Changes raised", raised.Count), new("decided", "Changes decided", decided.Count)];
            breakdowns["changeStatus"] = (await db.ProductChangeRequests.AsNoTracking().Where(c => c.CreatedAtUtc >= w.FromUtc)
                .GroupBy(c => c.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct)).Select(x => new Slice(x.Key.ToString(), x.N)).ToList();
            kpis.Add(new Kpi("decided", "Changes decided", decided.CountTotal, decided.PrevCount, "count", decided.Count));

            // ----- companies & activity -----
            var companies = await Totals_(db.Organizations.AsNoTracking().Select(o => new Point { At = o.CreatedAtUtc, V = 0 }), w, ct);
            series["companies"] = [new("registered", "Companies registered", companies.Count)];
            kpis.Add(new Kpi("companies", "New companies", companies.CountTotal, companies.PrevCount, "count", companies.Count));
            var activity = await Totals_(db.AuditEvents.AsNoTracking().Select(a => new Point { At = a.TimestampUtc, V = 0 }), w, ct);
            series["activity"] = [new("events", "Audited actions", activity.Count)];
            breakdowns["topActions"] = (await db.AuditEvents.AsNoTracking().Where(a => a.TimestampUtc >= w.FromUtc)
                .GroupBy(a => a.Action).Select(g => new { g.Key, N = g.Count() }).OrderByDescending(x => x.N).Take(8).ToListAsync(ct))
                .Select(x => new Slice(x.Key, x.N)).ToList();
        }

        breakdowns["productStatus"] = snapshot.ProductStatus;
        breakdowns["availability"] = snapshot.Availability;
        breakdowns["companyStatus"] = snapshot.CompanyStatus;
        rankings["categories"] = snapshot.CategoriesByProducts;
        rankings["offersBySupplier"] = snapshot.OffersBySupplier;

        return new AnalyticsReport(w.Range, w.FromUtc, w.ToUtc, w.Bucket.ToString(), w.Buckets, scope.Platform ? "platform" : "company",
            kpis, series, breakdowns, rankings, snapshot, clock.GetUtcNow().UtcDateTime);
    }

    private sealed record DayCount(DateTime Day, int N);

    private async Task<Totals> ProductTotalsAsync(string name, IQueryable<Point> source, AnalyticsWindow w, CancellationToken ct)
    {
        var daily = await cache.GetOrCreateAsync($"analytics:products-{name}:daily", async token =>
                (await source.GroupBy(p => p.At.Date).Select(g => new { g.Key, N = g.Count() }).ToListAsync(token))
                    .Select(x => new DayCount(DateTime.SpecifyKind(x.Key, DateTimeKind.Utc), x.N)).ToList(),
            new HybridCacheEntryOptions { Expiration = SnapshotTtl, LocalCacheExpiration = SnapshotTtl }, ["analytics"], ct);
        var count = new decimal[w.Buckets.Count];
        if (w.Bucket == AnalyticsBucket.Hour)
        {
            var hours = await source.Where(p => p.At >= w.FromUtc && p.At <= w.ToUtc).GroupBy(p => p.At.Hour)
                .Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
            foreach (var h in hours) if (h.Key < count.Length) count[h.Key] += h.N;
        }
        else
        {
            var index = w.Buckets.Select((b, i) => (b, i)).ToDictionary(x => x.b, x => x.i);
            foreach (var d in daily.Where(d => d.Day >= w.FromUtc.Date && d.Day <= w.ToUtc))
                if (index.TryGetValue(w.BucketOf(d.Day), out var i)) count[i] += d.N;
        }
        // Previous period at day resolution (for "today" that is yesterday).
        var prev = daily.Where(d => d.Day >= w.PreviousFromUtc.Date && d.Day < w.FromUtc.Date).Sum(d => (decimal)d.N);
        return new Totals(count, new decimal[count.Length], count.Sum(), 0, prev, 0);
    }

    /// <summary>Counts and sums per bucket for the window, plus totals for the window and the period before it.</summary>
    private static async Task<Totals> Totals_(IQueryable<Point> source, AnalyticsWindow w, CancellationToken ct)
    {
        var inWindow = source.Where(p => p.At >= w.FromUtc && p.At <= w.ToUtc);
        List<(DateTime At, int N, decimal V)> rows;
        if (w.Bucket == AnalyticsBucket.Hour)
            rows = (await inWindow.GroupBy(p => new { p.At.Date, p.At.Hour }).Select(g => new { g.Key.Date, g.Key.Hour, N = g.Count(), V = g.Sum(p => p.V) }).ToListAsync(ct))
                .Select(r => (r.Date.AddHours(r.Hour), r.N, r.V)).ToList();
        else
            rows = (await inWindow.GroupBy(p => p.At.Date).Select(g => new { g.Key, N = g.Count(), V = g.Sum(p => p.V) }).ToListAsync(ct))
                .Select(r => (r.Key, r.N, r.V)).ToList();

        var index = w.Buckets.Select((b, i) => (b, i)).ToDictionary(x => x.b, x => x.i);
        var count = new decimal[w.Buckets.Count];
        var sum = new decimal[w.Buckets.Count];
        foreach (var (at, n, v) in rows)
            if (index.TryGetValue(w.BucketOf(at), out var i)) { count[i] += n; sum[i] += v; }

        var prev = await source.Where(p => p.At >= w.PreviousFromUtc && p.At < w.FromUtc)
            .GroupBy(_ => 1).Select(g => new { N = g.Count(), V = g.Sum(p => p.V) }).FirstOrDefaultAsync(ct);
        return new Totals(count, sum, count.Sum(), sum.Sum(), prev?.N ?? 0, prev?.V ?? 0);
    }

    private async Task<AnalyticsSnapshot> BuildSnapshotAsync(CancellationToken ct)
    {
        var productStatus = (await db.Products.AsNoTracking().GroupBy(p => p.Status).Select(g => new { g.Key, N = (long)g.Count() }).ToListAsync(ct))
            .Select(x => new Slice(x.Key.ToString(), x.N)).ToList();
        var availability = (await db.SupplierOffers.AsNoTracking().GroupBy(o => o.Availability).Select(g => new { g.Key, N = (long)g.Count() }).ToListAsync(ct))
            .Select(x => new Slice(x.Key.ToString(), x.N)).ToList();
        var categories = (await db.ProductCategories.AsNoTracking().GroupBy(pc => pc.CategoryId).Select(g => new { g.Key, N = (long)g.Count() })
            .OrderByDescending(x => x.N).Take(10).ToListAsync(ct));
        var catNames = await db.Categories.AsNoTracking().Where(c => categories.Select(x => x.Key).Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var offersBySupplier = await db.SupplierOffers.AsNoTracking().GroupBy(o => o.SupplierId).Select(g => new { g.Key, N = (long)g.Count() })
            .OrderByDescending(x => x.N).Take(10).ToListAsync(ct);
        var supNames = await db.Suppliers.AsNoTracking().Where(s => offersBySupplier.Select(x => x.Key).Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Code, ct);
        var mediaKinds = (await db.ProductAssets.AsNoTracking().GroupBy(a => a.Kind).Select(g => new { g.Key, N = (long)g.Count() }).ToListAsync(ct))
            .Select(x => new Slice(x.Key.ToString(), x.N)).ToList();
        var companyStatus = (await db.Organizations.AsNoTracking().GroupBy(o => o.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct))
            .Select(x => new Slice(x.Key.ToString(), x.N)).ToList();
        return new AnalyticsSnapshot(
            productStatus.Sum(s => (long)s.Value), (long)(productStatus.FirstOrDefault(s => s.Label == nameof(ProductStatus.Active))?.Value ?? 0),
            availability.Sum(s => (long)s.Value), await db.Suppliers.CountAsync(ct),
            (int)(companyStatus.FirstOrDefault(s => s.Label == nameof(OrganizationStatus.Active))?.Value ?? 0),
            (int)(companyStatus.FirstOrDefault(s => s.Label == nameof(OrganizationStatus.PendingVerification))?.Value ?? 0),
            await db.Users.CountAsync(ct),
            await db.ProductChangeRequests.CountAsync(c => c.Status == ChangeStatus.Pending, ct),
            await db.ProductAssets.Where(a => a.IsPrimary).LongCountAsync(ct),
            productStatus, availability,
            categories.Select(c => new Slice(catNames.GetValueOrDefault(c.Key, $"#{c.Key}"), c.N)).ToList(),
            offersBySupplier.Select(o => new Slice(supNames.GetValueOrDefault(o.Key, $"#{o.Key}"), o.N)).ToList(),
            mediaKinds, companyStatus, clock.GetUtcNow().UtcDateTime);
    }
}
