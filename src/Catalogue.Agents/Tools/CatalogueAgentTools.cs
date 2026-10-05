using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Catalogue.Application;
using Catalogue.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Catalogue.Agents.Tools;

/// <summary>A tool call as shown to the user and stored with the answer.</summary>
public sealed record ToolTraceEntry(string Name, string Arguments, string Result, bool Failed, int DurationMs);

/// <summary>
/// Wraps every tool: enforces the per-run call budget, turns exceptions into an error result the model can read
/// (instead of failing the run), truncates large results (each token costs prompt time on a local model) and
/// records a trace. Tool output is data for the model, never instructions - the system prompt says so too.
/// </summary>
public sealed class GuardedFunction(AIFunction inner, ToolBudget budget, Action<ToolTraceEntry, bool> onEvent) : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var args = JsonSerializer.Serialize(arguments.ToDictionary(a => a.Key, a => a.Value));
        if (!budget.TryTake())
        {
            var limit = $$"""{"error":"Tool call limit reached ({{budget.Limit}} per question). Answer with what you have."}""";
            onEvent(new ToolTraceEntry(Name, args, limit, true, 0), false);
            return limit;
        }
        onEvent(new ToolTraceEntry(Name, args, "", false, 0), true);
        var sw = Stopwatch.StartNew();
        string result;
        var failed = false;
        try
        {
            var value = await base.InvokeCoreAsync(arguments, cancellationToken);
            // Compact JSON: indentation is pure prompt-processing cost on a local model.
            result = value switch { null => "null", string s => s, JsonElement e => JsonSerializer.Serialize(e, ToolJson.Options), _ => JsonSerializer.Serialize(value, ToolJson.Options) };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            failed = true;
            result = JsonSerializer.Serialize(new { error = ex.Message });
        }
        catch (Exception)
        {
            failed = true;
            result = """{"error":"The tool failed unexpectedly. Do not retry it; tell the user."}""";
        }
        if (result.Length > budget.MaxChars) result = result[..budget.MaxChars] + "…(truncated)";
        onEvent(new ToolTraceEntry(Name, args, result, failed, (int)sw.ElapsedMilliseconds), false);
        return result;
    }
}

public sealed class ToolBudget(int limit, int maxChars)
{
    private int _used;
    public int Limit => limit;
    public int MaxChars => maxChars;
    public bool TryTake() => Interlocked.Increment(ref _used) <= limit;
}

internal static class ToolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = false };
}

/// <summary>
/// The agent's tools. Each one is read-only except <see cref="ProposeAction"/>, which only records a proposal for a
/// person to approve. Inputs are normalized (synonyms, case) because small local models paraphrase enum values.
/// </summary>
public sealed class CatalogueAgentTools(ICatalogueDbContext db, MarketplacePricing pricing, AnalyticsService analytics,
    AgentPrincipal user, Guid? conversationId, TimeProvider clock)
{
    public const string Overview = "operations_overview";

    [Description("Counts of everything that needs attention now: orders by status, unpaid orders, failed imports, pending approvals and companies.")]
    public async Task<object> OperationsOverview(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var orders = ScopedOrders();
        var byStatus = await orders.GroupBy(o => o.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key.ToString(), x => x.N, ct);
        var result = new Dictionary<string, object?>
        {
            ["ordersByStatus"] = byStatus,
            ["ordersAwaitingSellerOver48h"] = await orders.CountAsync(o => o.Status == PurchaseOrderStatus.Submitted && o.CreatedAtUtc < now.AddHours(-48), ct),
            ["ordersUnpaidOpen"] = await orders.CountAsync(o => (o.Status == PurchaseOrderStatus.Submitted || o.Status == PurchaseOrderStatus.Accepted) && o.PaymentStatus == OrderPaymentStatus.Unpaid, ct),
        };
        if (user.Can(AgentPolicies.ImportOps))
        {
            var since = now.AddDays(-7);
            result["importsFailedLast7d"] = await db.ImportFiles.CountAsync(i => i.ReceivedAtUtc >= since && (i.Status == ImportStatus.Failed || i.Status == ImportStatus.Rejected), ct);
            result["importsRunning"] = await db.ImportFiles.CountAsync(i => i.Status == ImportStatus.Processing || i.Status == ImportStatus.Received || i.Status == ImportStatus.Retrying, ct);
            result["approvalsPending"] = await db.ProductChangeRequests.GroupBy(c => c.Kind).Where(g => g.Any(c => c.Status == ChangeStatus.Pending))
                .Select(g => new { g.Key, N = g.Count(c => c.Status == ChangeStatus.Pending) }).ToDictionaryAsync(x => x.Key.ToString(), x => x.N, ct);
        }
        if (user.Can(AgentPolicies.PlatformAdmin))
            result["companiesAwaitingVerification"] = await db.Organizations.CountAsync(o => o.Status == OrganizationStatus.PendingVerification, ct);
        return result;
    }

    [Description("Find products by SKU, GTIN/EAN barcode, manufacturer part number, brand or name prefix. Returns up to 5.")]
    public async Task<object> FindProducts([Description("SKU, GTIN, MPN, brand or start of the name")] string query, CancellationToken ct = default)
    {
        var term = (query ?? "").Trim();
        if (term.Length < 2) throw new ArgumentException("Give at least 2 characters to search for.");
        var upper = term.ToUpperInvariant();
        var digits = new string(term.Where(char.IsAsciiDigit).ToArray());
        var q = db.Products.AsNoTracking();
        q = digits.Length >= 8 && digits.Length == term.Length && Gtin.IsValid(digits)
            ? q.Where(p => p.Gtin == Gtin.Normalize(digits))
            : q.Where(p => p.Sku.StartsWith(upper) || p.ManufacturerPartNumber == upper || p.Name.StartsWith(term) || (p.Brand != null && p.Brand.StartsWith(term)));
        var items = await q.OrderBy(p => p.Id).Take(5).Select(p => new
        {
            p.Sku, p.Name, p.Brand, mpn = p.ManufacturerPartNumber, status = p.Status.ToString(),
            listPrice = p.Prices.Where(x => x.ValidToUtc == null).Select(x => (decimal?)x.Amount).FirstOrDefault(),
            offers = p.Offers.Count
        }).ToListAsync(ct);
        return items.Count == 0 ? new { found = 0, hint = "No match. Try a shorter prefix or the GTIN." } : new { found = items.Count, items };
    }

    [Description("Full details of one product: identity, key specs, list price and every supplier offer with stock and lead time. For buyers also the best price for them.")]
    public async Task<object> ProductDetails([Description("Product SKU or GTIN")] string skuOrGtin, CancellationToken ct = default)
    {
        var product = await FindProductAsync(skuOrGtin, ct);
        var specs = await db.ProductAttributeValues.AsNoTracking().Where(v => v.ProductId == product.Id).OrderBy(v => v.Attribute!.SortOrder).Take(6)
            .Select(v => new { name = v.Attribute!.Name, unit = v.Attribute.Unit, v.TextValue, v.NumberValue, v.BoolValue }).ToListAsync(ct);
        var offers = await db.SupplierOffers.AsNoTracking().Where(o => o.ProductId == product.Id).Select(o => new
        {
            supplier = o.Supplier!.Code, o.Price, o.Currency, availability = o.Availability.ToString(), o.LeadTimeDays, moq = o.MinOrderQuantity, pack = o.PackSize
        }).Take(8).ToListAsync(ct);
        object? best = null;
        if (user.CustomerAccountId is { } customer && user.Can(AgentPolicies.Buyer))
        {
            var quotes = (await pricing.CompareAsync(new Dictionary<long, int> { [product.Id] = 1 }, customer, user.SupplierId, ct))[product.Id];
            best = quotes.FirstOrDefault(q => q.IsCheapest) is { } q ? new { q.SupplierCode, netUnitPrice = q.NetUnitPrice, q.Currency, q.Explanation } : null;
        }
        return new
        {
            product.Sku, product.Name, product.Brand, mpn = product.ManufacturerPartNumber, product.Gtin, status = product.Status.ToString(),
            listPrice = await db.ProductPrices.Where(x => x.ProductId == product.Id && x.ValidToUtc == null).Select(x => (decimal?)x.Amount).FirstOrDefaultAsync(ct),
            specs = specs.Select(s => new { s.name, value = s.TextValue ?? s.NumberValue?.ToString("0.##") ?? s.BoolValue?.ToString(), s.unit }),
            offers, bestPriceForYou = best
        };
    }

    [Description("List purchase orders the user can see. status: Submitted (waiting for the seller to accept), Accepted (to ship), Shipped, Rejected, Cancelled, or empty for all. side: leave empty unless the user says 'my purchases' (buying) or 'my sales' (selling).")]
    public async Task<object> ListOrders(string? status = null, string? side = null, CancellationToken ct = default)
    {
        var q = ScopedOrders();
        if (!string.IsNullOrWhiteSpace(status)) { var st = OrderStatus(status); q = q.Where(o => o.Status == st); }
        if (Normalize(side) is "buying" or "buy" or "purchases" or "bought") q = q.Where(o => o.BuyerOrganizationId == user.OrganizationId);
        else if (Normalize(side) is "selling" or "sell" or "sales" or "sold") q = q.Where(o => o.SupplierId == user.SupplierId);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(o => o.CreatedAtUtc).Take(10).Select(o => new
        {
            o.Number, status = o.Status.ToString(), payment = o.PaymentStatus.ToString(), buyer = o.BuyerOrganization!.Name, seller = o.Supplier!.Code,
            value = o.Subtotal, o.Currency, placed = o.CreatedAtUtc
        }).ToListAsync(ct);
        return new { total, showing = items.Count, items };
    }

    [Description("Details of one purchase order by its number (PO-...): lines, status, payment and history note.")]
    public async Task<object> OrderDetails([Description("Order number, e.g. PO-261004-77C80E")] string orderNumber, CancellationToken ct = default)
    {
        var number = (orderNumber ?? "").Trim().ToUpperInvariant();
        var o = await ScopedOrders().Where(x => x.Number == number).Select(x => new
        {
            x.Number, status = x.Status.ToString(), payment = x.PaymentStatus.ToString(), buyer = x.BuyerOrganization!.Name, seller = x.Supplier!.Name,
            x.Subtotal, x.Currency, x.BuyerReference, x.StatusNote, placed = x.CreatedAtUtc, updated = x.UpdatedAtUtc,
            lines = x.Lines.Select(l => new { l.SupplierSku, l.Description, l.Quantity, l.UnitPrice, l.LineTotal }).Take(10).ToList()
        }).FirstOrDefaultAsync(ct);
        return o ?? throw new KeyNotFoundException($"No order {number} that you can see.");
    }

    [Description("Recent supplier feed imports (CSV/EDI) with row counts. status: failed, errors (completed with row errors), running, completed, or empty.")]
    public async Task<object> ListImports(string? status = null, CancellationToken ct = default)
    {
        var q = db.ImportFiles.AsNoTracking();
        switch (Normalize(status))
        {
            case "" or null: break;
            case "failed" or "failure" or "rejected" or "error" or "broken": q = q.Where(i => i.Status == ImportStatus.Failed || i.Status == ImportStatus.Rejected); break;
            case "errors" or "witherrors" or "completedwitherrors" or "partial": q = q.Where(i => i.Status == ImportStatus.CompletedWithErrors); break;
            case "running" or "processing" or "inprogress" or "queued" or "received" or "retrying":
                q = q.Where(i => i.Status == ImportStatus.Processing || i.Status == ImportStatus.Received || i.Status == ImportStatus.Retrying); break;
            case "completed" or "done" or "ok" or "succeeded": q = q.Where(i => i.Status == ImportStatus.Completed); break;
            default: throw new ArgumentException("status must be one of: failed, errors, running, completed (or empty).");
        }
        var items = await q.OrderByDescending(i => i.Id).Take(8).Select(i => new
        {
            importId = i.Id, i.FileName, supplier = i.Supplier!.Code, status = i.Status.ToString(), received = i.ReceivedAtUtc,
            rows = i.TotalRows, i.ProcessedRows, rejectedRows = i.ErrorRows, reason = i.FailureReason
        }).ToListAsync(ct);
        var ids = items.Select(i => i.importId).ToList();
        var topErrors = await db.ImportItemErrors.AsNoTracking().Where(e => ids.Contains(e.ImportId))
            .GroupBy(e => new { e.ImportId, e.Field }).Select(g => new { g.Key.ImportId, g.Key.Field, N = g.Count() })
            .OrderByDescending(x => x.N).Take(6).ToListAsync(ct);
        return new { items, topErrorFields = topErrors };
    }

    [Description("Catalogue changes waiting for approval (new products and supplier price changes), largest price changes first.")]
    public async Task<object> PendingApprovals(CancellationToken ct = default)
    {
        var pending = db.ProductChangeRequests.AsNoTracking().Where(c => c.Status == ChangeStatus.Pending);
        var bySupplier = await pending.GroupBy(c => new { c.Kind, c.Supplier!.Code }).Select(g => new { kind = g.Key.Kind.ToString(), supplier = g.Key.Code, count = g.Count() })
            .OrderByDescending(x => x.count).Take(8).ToListAsync(ct);
        var priceChanges = await pending.Where(c => c.Kind == ChangeKind.PriceChange && c.OldPrice > 0)
            .Select(c => new { changeId = c.Id, sku = c.Product!.Sku, supplier = c.Supplier!.Code, c.OldPrice, c.NewPrice, raised = c.CreatedAtUtc })
            .OrderByDescending(c => c.raised).Take(30).ToListAsync(ct);
        var largest = priceChanges.Select(c => new { c.changeId, c.sku, c.supplier, c.OldPrice, c.NewPrice, changePercent = Math.Round((c.NewPrice!.Value - c.OldPrice!.Value) / c.OldPrice.Value * 100, 1) })
            .OrderByDescending(c => Math.Abs(c.changePercent)).Take(6);
        return new { total = await pending.CountAsync(ct), bySupplier, largestPriceChanges = largest };
    }

    [Description("Business metrics for a period: orders, order value, average order, payments, refunds, products added, rows ingested. range: today, 7d, 15d, 1m, 3m, 6m or 1y.")]
    public async Task<object> BusinessMetrics(string range = "7d", CancellationToken ct = default)
    {
        var r = Range(range);
        var report = await analytics.GetAsync(r, new AnalyticsScope(user.IsPlatformStaff, user.OrganizationId, user.SupplierId), ct);
        return new
        {
            range = r, from = report.FromUtc, to = report.ToUtc,
            metrics = report.Kpis.Select(k => new { k.Label, k.Value, previousPeriod = k.Previous, k.Unit,
                changePercent = k.Previous is > 0 ? Math.Round((k.Value - k.Previous.Value) / k.Previous.Value * 100, 1) : (decimal?)null }),
            topSuppliers = report.Rankings.GetValueOrDefault("topSuppliers")?.Take(3)
        };
    }

    [Description("Propose an action for a person to approve; nothing happens until they do. action: reprocess_import (target = import id), approve_change or reject_change (target = change id), accept_order, reject_order, ship_order or cancel_order (target = order number).")]
    public async Task<object> ProposeAction(string action, string target, [Description("Why, in one sentence")] string reason, CancellationToken ct = default)
    {
        var kind = Normalize(action) switch
        {
            "reprocessimport" or "reprocess" or "retryimport" or "rerunimport" => AgentActionKind.ReprocessImport,
            "approvechange" or "approve" or "approveprice" or "approvepricechange" => AgentActionKind.ApproveChange,
            "rejectchange" or "rejectprice" or "rejectpricechange" => AgentActionKind.RejectChange,
            "acceptorder" or "accept" => AgentActionKind.AcceptOrder,
            "rejectorder" or "reject" or "declineorder" => AgentActionKind.RejectOrder,
            "shiporder" or "ship" or "markshipped" or "markordershipped" => AgentActionKind.ShipOrder,
            "cancelorder" or "cancel" => AgentActionKind.CancelOrder,
            _ => throw new ArgumentException("action must be one of: reprocess_import, approve_change, reject_change, accept_order, reject_order, ship_order, cancel_order.")
        };
        var (targetId, label) = await ProposalTargets.ValidateAsync(db, user, kind, target, ct);
        var existing = await db.AgentActionProposals.FirstOrDefaultAsync(p => p.Kind == kind && p.TargetId == targetId && p.Status == ProposalStatus.Pending, ct);
        if (existing is not null) return new { proposalId = existing.Id, status = "already proposed, waiting for approval", existing.TargetLabel };
        var proposal = new AgentActionProposal(kind, targetId, label, reason, "assistant", user.UserName, conversationId);
        db.AgentActionProposals.Add(proposal);
        await db.SaveChangesAsync(ct);
        return new { proposalId = proposal.Id, status = "waiting for a person to approve it", proposal.TargetLabel };
    }

    // ----- helpers -----

    private IQueryable<PurchaseOrder> ScopedOrders()
    {
        var q = db.PurchaseOrders.AsNoTracking();
        return user.IsPlatformStaff ? q : q.Where(o => o.BuyerOrganizationId == user.OrganizationId || o.SupplierId == user.SupplierId);
    }

    private async Task<Product> FindProductAsync(string skuOrGtin, CancellationToken ct)
    {
        var term = (skuOrGtin ?? "").Trim();
        var digits = new string(term.Where(char.IsAsciiDigit).ToArray());
        var upper = term.ToUpperInvariant();
        var product = digits.Length >= 8 && digits.Length == term.Length && Gtin.IsValid(digits)
            ? await db.Products.AsNoTracking().Where(p => p.Gtin == Gtin.Normalize(digits)).OrderBy(p => p.Id).FirstOrDefaultAsync(ct)
            : await db.Products.AsNoTracking().Where(p => p.Sku == upper || p.ManufacturerPartNumber == upper).OrderBy(p => p.Id).FirstOrDefaultAsync(ct);
        return product ?? throw new KeyNotFoundException($"No product with SKU or GTIN '{term}'. Use find_products first.");
    }

    internal static PurchaseOrderStatus OrderStatus(string status) => Normalize(status) switch
    {
        "submitted" or "pending" or "waiting" or "new" or "open" or "awaiting" or "awaitingacceptance" or "pendingacceptance" or "placed" => PurchaseOrderStatus.Submitted,
        "accepted" or "confirmed" or "approved" or "toship" or "readytoship" => PurchaseOrderStatus.Accepted,
        "shipped" or "sent" or "dispatched" or "delivered" or "fulfilled" => PurchaseOrderStatus.Shipped,
        "rejected" or "declined" or "refused" => PurchaseOrderStatus.Rejected,
        "cancelled" or "canceled" or "cancel" => PurchaseOrderStatus.Cancelled,
        _ => throw new ArgumentException("status must be one of: Submitted (waiting for the seller), Accepted, Shipped, Rejected, Cancelled.")
    };

    internal static string Range(string? range) => Normalize(range) switch
    {
        "" or null or "7d" or "7days" or "week" or "lastweek" or "thisweek" or "7" => "7d",
        "today" or "day" or "24h" or "1d" => "today",
        "15d" or "15days" or "fortnight" or "2weeks" or "15" => "15d",
        "1m" or "month" or "30d" or "30days" or "lastmonth" or "thismonth" => "1m",
        "3m" or "quarter" or "90d" or "3months" => "3m",
        "6m" or "halfyear" or "6months" => "6m",
        "1y" or "year" or "12m" or "12months" or "365d" or "thisyear" => "1y",
        _ => throw new ArgumentException("range must be one of: today, 7d, 15d, 1m, 3m, 6m, 1y.")
    };

    internal static string? Normalize(string? s) =>
        s is null ? null : new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}

/// <summary>Validates the target of a proposed action and that the person asking may perform it.</summary>
public static class ProposalTargets
{
    public static async Task<(string TargetId, string Label)> ValidateAsync(ICatalogueDbContext db, AgentPrincipal user, AgentActionKind kind, string target, CancellationToken ct)
    {
        var t = (target ?? "").Trim();
        switch (kind)
        {
            case AgentActionKind.ReprocessImport:
            {
                if (!user.Can(AgentPolicies.ImportOps)) throw new InvalidOperationException("You are not allowed to reprocess imports.");
                if (!long.TryParse(t.TrimStart('#'), out var id)) throw new ArgumentException("target must be the numeric import id.");
                var import = await db.ImportFiles.AsNoTracking().Where(i => i.Id == id).Select(i => new { i.Id, i.FileName, i.Status }).FirstOrDefaultAsync(ct)
                    ?? throw new KeyNotFoundException($"No import {id}.");
                if (import.Status is ImportStatus.Processing or ImportStatus.Received) throw new InvalidOperationException($"Import {id} is still {import.Status}.");
                return (id.ToString(), $"Reprocess import #{id} ({import.FileName})");
            }
            case AgentActionKind.ApproveChange or AgentActionKind.RejectChange:
            {
                if (!user.Can(AgentPolicies.CatalogueAdmin)) throw new InvalidOperationException("You are not allowed to decide catalogue changes.");
                if (!long.TryParse(t.TrimStart('#'), out var id)) throw new ArgumentException("target must be the numeric change id.");
                var change = await db.ProductChangeRequests.AsNoTracking().Where(c => c.Id == id)
                    .Select(c => new { c.Status, c.Kind, sku = c.Product!.Sku, c.OldPrice, c.NewPrice }).FirstOrDefaultAsync(ct) ?? throw new KeyNotFoundException($"No change {id}.");
                if (change.Status != ChangeStatus.Pending) throw new InvalidOperationException($"Change {id} is already {change.Status}.");
                var what = change.Kind == ChangeKind.PriceChange ? $"price {change.OldPrice:0.00} → {change.NewPrice:0.00}" : "new product";
                return (id.ToString(), $"{(kind == AgentActionKind.ApproveChange ? "Approve" : "Reject")} change #{id}: {change.sku} {what}");
            }
            default:
            {
                var number = t.ToUpperInvariant();
                var order = await db.PurchaseOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Number == number, ct)
                    ?? throw new KeyNotFoundException($"No order {number}.");
                var isSeller = user.SupplierId == order.SupplierId;
                var isBuyer = user.OrganizationId == order.BuyerOrganizationId;
                var allowed = kind == AgentActionKind.CancelOrder ? isBuyer || user.IsAdmin : isSeller || user.IsAdmin;
                if (!allowed) throw new InvalidOperationException(kind == AgentActionKind.CancelOrder
                    ? "Only the buying company can cancel this order." : "Only the selling company can accept, reject or ship this order.");
                var next = kind switch
                {
                    AgentActionKind.AcceptOrder => PurchaseOrderStatus.Accepted,
                    AgentActionKind.RejectOrder => PurchaseOrderStatus.Rejected,
                    AgentActionKind.ShipOrder => PurchaseOrderStatus.Shipped,
                    _ => PurchaseOrderStatus.Cancelled
                };
                if (!CanTransition(order.Status, next)) throw new InvalidOperationException($"Order {number} is {order.Status}; it cannot become {next}.");
                return (number, $"{kind switch { AgentActionKind.AcceptOrder => "Accept", AgentActionKind.RejectOrder => "Reject", AgentActionKind.ShipOrder => "Mark shipped", _ => "Cancel" }} order {number} ({order.Subtotal:0.00} {order.Currency})");
            }
        }
    }

    private static bool CanTransition(PurchaseOrderStatus from, PurchaseOrderStatus to) => (from, to) switch
    {
        (PurchaseOrderStatus.Submitted, PurchaseOrderStatus.Accepted or PurchaseOrderStatus.Rejected or PurchaseOrderStatus.Cancelled) => true,
        (PurchaseOrderStatus.Accepted, PurchaseOrderStatus.Shipped or PurchaseOrderStatus.Cancelled) => true,
        _ => false
    };
}

/// <summary>
/// Chooses the tools for one question. Every tool definition is re-read by the model on every call (about 20
/// tokens/s on a laptop GPU), so a question about orders should not pay for the import and catalogue tools.
/// </summary>
public static class ToolRouter
{
    private static readonly (string Group, string[] Words)[] Groups =
    [
        ("catalogue", ["product", "sku", "gtin", "ean", "barcode", "mpn", "boiler", "heat pump", "pump", "valve", "radiator", "thermostat", "cylinder", "price", "offer", "stock", "brand", "item", "catalog", "cheap", "spec", "supplier"]),
        ("orders", ["order", "po-", "purchase", "ship", "accept", "cancel", "deliver", "buyer", "seller", "sale", "sold", "bought", "unpaid", "paid", "payment", "refund"]),
        ("imports", ["import", "feed", "edi", "csv", "file", "row", "ingest", "upload", "fail", "error", "reprocess"]),
        ("approvals", ["approv", "change", "pending", "review", "price change", "new product", "reject"]),
        ("analytics", ["revenue", "sales", "gmv", "trend", "week", "month", "today", "year", "quarter", "metric", "kpi", "analytic", "perform", "growth", "how many", "total", "compare", "increase", "decrease"]),
    ];

    public static IReadOnlyList<string> GroupsFor(string question)
    {
        var q = question.ToLowerInvariant();
        var groups = Groups.Where(g => g.Words.Any(w => q.Contains(w, StringComparison.Ordinal))).Select(g => g.Group).ToList();
        return groups.Count > 0 ? groups : ["catalogue", "orders"];
    }

    /// <summary>All tools this user may use, in their groups (the router then picks groups per question).</summary>
    public static IReadOnlyList<(string Group, AIFunction Function)> Available(CatalogueAgentTools t, AgentPrincipal user)
    {
        var list = new List<(string, AIFunction)>
        {
            ("always", AIFunctionFactory.Create(t.OperationsOverview, CatalogueAgentTools.Overview)),
            ("catalogue", AIFunctionFactory.Create(t.FindProducts, "find_products")),
            ("catalogue", AIFunctionFactory.Create(t.ProductDetails, "product_details")),
            ("analytics", AIFunctionFactory.Create(t.BusinessMetrics, "business_metrics")),
        };
        if (user.Can(AgentPolicies.Marketplace) || user.IsPlatformStaff)
        {
            list.Add(("orders", AIFunctionFactory.Create(t.ListOrders, "list_orders")));
            list.Add(("orders", AIFunctionFactory.Create(t.OrderDetails, "order_details")));
        }
        if (user.Can(AgentPolicies.ImportOps))
        {
            list.Add(("imports", AIFunctionFactory.Create(t.ListImports, "list_imports")));
            list.Add(("approvals", AIFunctionFactory.Create(t.PendingApprovals, "pending_approvals")));
        }
        var canAct = user.Can(AgentPolicies.ImportOps) || user.Can(AgentPolicies.CatalogueAdmin) || user.SupplierId is not null || user.OrganizationId is not null || user.IsAdmin;
        if (canAct) list.Add(("actions", AIFunctionFactory.Create(t.ProposeAction, "propose_action")));
        return list;
    }

    public static IReadOnlyList<AIFunction> Select(string question, IReadOnlyList<(string Group, AIFunction Function)> available)
    {
        var groups = GroupsFor(question);
        var wantsAction = groups.Contains("orders") || groups.Contains("imports") || groups.Contains("approvals")
            || question.Contains("approve", StringComparison.OrdinalIgnoreCase) || question.Contains("fix", StringComparison.OrdinalIgnoreCase)
            || question.Contains("do ", StringComparison.OrdinalIgnoreCase);
        return available.Where(a => a.Group == "always" || groups.Contains(a.Group) || (a.Group == "actions" && wantsAction))
            .Select(a => a.Function).ToList();
    }
}
