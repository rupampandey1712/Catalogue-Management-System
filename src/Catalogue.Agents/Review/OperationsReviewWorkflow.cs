using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Catalogue.Agents.FoundryLocal;
using Catalogue.Agents.Runs;
using Catalogue.Agents.Tools;
using Catalogue.Application;
using Catalogue.Domain;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Catalogue.Agents.Review;

public sealed record ReviewRequest(long ReviewId, string Trigger, string RequestedBy);

public sealed record FailedImport(long Id, string FileName, string Supplier, string Status, string? Reason, DateTime ReceivedAtUtc);
public sealed record StaleOrder(string Number, string Supplier, string Buyer, decimal Value, string Currency, double AgeHours);
public sealed record SmallPriceChange(long ChangeId, string Sku, string Supplier, decimal OldPrice, decimal NewPrice, decimal ChangePercent);

/// <summary>Facts gathered from every part of the system (deterministic, no model involved).</summary>
public sealed record OperationsSnapshot(
    long ReviewId, DateTime TakenAtUtc,
    IReadOnlyList<FailedImport> FailedImports, int StuckImports, int ImportsLast24h, long RowsRejectedLast24h,
    int PendingNewProducts, int PendingPriceChanges, double OldestPendingChangeDays, IReadOnlyList<SmallPriceChange> SmallPriceChanges, int LargePriceChanges,
    IReadOnlyList<StaleOrder> AwaitingSeller, IReadOnlyList<StaleOrder> AwaitingShipment, int UnpaidOpenOrders, int FailedPaymentsLast7d, int PendingPaymentsOver1h,
    int CompaniesPendingOver2d, int CompaniesPending,
    decimal Orders7d, decimal OrdersPrev7d, decimal Gmv7d, decimal GmvPrev7d,
    long Products, long ProductsWithImage, int MediaFetchFailures);

public sealed record SuggestedAction(AgentActionKind Kind, string TargetId, string Reason);
public sealed record Finding(string Severity, string Area, string Title, string Detail, SuggestedAction? Action = null);
public sealed record TriageResult(OperationsSnapshot Snapshot, IReadOnlyList<Finding> Findings);
public sealed record ReviewDraft(TriageResult Triage, string Briefing, bool ByModel, string? ModelId, string? ModelError);
public sealed record ReviewOutcome(long ReviewId, int Findings, int Proposals, bool ByModel);

/// <summary>Step 1: collect signals from imports, approvals, orders, payments, companies, catalogue and media.</summary>
public sealed class CollectSignalsExecutor(ICatalogueDbContext db, AnalyticsService analytics, TimeProvider clock)
    : Executor<ReviewRequest, OperationsSnapshot>("collect-signals")
{
    public override async ValueTask<OperationsSnapshot> HandleAsync(ReviewRequest message, IWorkflowContext context, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var failed = await db.ImportFiles.AsNoTracking()
            .Where(i => i.ReceivedAtUtc >= now.AddDays(-2) && (i.Status == ImportStatus.Failed || i.Status == ImportStatus.Rejected))
            .OrderByDescending(i => i.Id).Take(5)
            .Select(i => new FailedImport(i.Id, i.FileName, i.Supplier!.Code, i.Status.ToString(), i.FailureReason, i.ReceivedAtUtc)).ToListAsync(ct);
        var stuck = await db.ImportFiles.CountAsync(i => i.Status == ImportStatus.Processing && i.ReceivedAtUtc < now.AddHours(-2), ct);
        var last24 = db.ImportFiles.AsNoTracking().Where(i => i.ReceivedAtUtc >= now.AddDays(-1));
        var pending = db.ProductChangeRequests.AsNoTracking().Where(c => c.Status == ChangeStatus.Pending);
        var oldest = await pending.OrderBy(c => c.CreatedAtUtc).Select(c => (DateTime?)c.CreatedAtUtc).FirstOrDefaultAsync(ct);
        // Low-risk candidates for approval: price moves within ±2%. Only a handful, a person still approves each.
        var small = (await pending.Where(c => c.Kind == ChangeKind.PriceChange && c.OldPrice > 0 && c.NewPrice != null)
                .OrderBy(c => c.CreatedAtUtc).Take(400)
                .Select(c => new { c.Id, sku = c.Product!.Sku, sup = c.Supplier!.Code, Old = c.OldPrice!.Value, New = c.NewPrice!.Value }).ToListAsync(ct))
            .Select(c => new SmallPriceChange(c.Id, c.sku, c.sup, c.Old, c.New, Math.Round((c.New - c.Old) / c.Old * 100, 2))).ToList();
        StaleOrder Stale(PurchaseOrder o, string supplier, string buyer) => new(o.Number, supplier, buyer, o.Subtotal, o.Currency, Math.Round((now - o.CreatedAtUtc).TotalHours, 1));
        var awaitingSeller = (await db.PurchaseOrders.AsNoTracking().Where(o => o.Status == PurchaseOrderStatus.Submitted && o.CreatedAtUtc < now.AddHours(-48))
            .OrderBy(o => o.CreatedAtUtc).Take(5).Select(o => new { o, s = o.Supplier!.Code, b = o.BuyerOrganization!.Name }).ToListAsync(ct)).Select(x => Stale(x.o, x.s, x.b)).ToList();
        var awaitingShip = (await db.PurchaseOrders.AsNoTracking().Where(o => o.Status == PurchaseOrderStatus.Accepted && o.UpdatedAtUtc < now.AddDays(-5))
            .OrderBy(o => o.UpdatedAtUtc).Take(5).Select(o => new { o, s = o.Supplier!.Code, b = o.BuyerOrganization!.Name }).ToListAsync(ct)).Select(x => Stale(x.o, x.s, x.b)).ToList();
        var report = await analytics.GetAsync("7d", new AnalyticsScope(true, null, null), ct);
        decimal Kpi(string key, bool previous) => report.Kpis.FirstOrDefault(k => k.Key == key) is { } k ? (previous ? k.Previous ?? 0 : k.Value) : 0;
        var snapshot = await analytics.SnapshotAsync(ct);

        return new OperationsSnapshot(message.ReviewId, now,
            failed, stuck, await last24.CountAsync(ct), await last24.SumAsync(i => (long)i.ErrorRows, ct),
            await pending.CountAsync(c => c.Kind == ChangeKind.NewProduct, ct), await pending.CountAsync(c => c.Kind == ChangeKind.PriceChange, ct),
            oldest is null ? 0 : Math.Round((now - oldest.Value).TotalDays, 1),
            small.Where(c => Math.Abs(c.ChangePercent) <= 2m).Take(5).ToList(), small.Count(c => Math.Abs(c.ChangePercent) >= 20m),
            awaitingSeller, awaitingShip,
            await db.PurchaseOrders.CountAsync(o => (o.Status == PurchaseOrderStatus.Submitted || o.Status == PurchaseOrderStatus.Accepted) && o.PaymentStatus == OrderPaymentStatus.Unpaid, ct),
            await db.Payments.CountAsync(p => p.Status == PaymentAttemptStatus.Failed && p.CreatedAtUtc >= now.AddDays(-7), ct),
            await db.Payments.CountAsync(p => p.Status == PaymentAttemptStatus.Pending && p.CreatedAtUtc < now.AddHours(-1), ct),
            await db.Organizations.CountAsync(o => o.Status == OrganizationStatus.PendingVerification && o.CreatedAtUtc < now.AddDays(-2), ct),
            await db.Organizations.CountAsync(o => o.Status == OrganizationStatus.PendingVerification, ct),
            Kpi("orders", false), Kpi("orders", true), Kpi("gmv", false), Kpi("gmv", true),
            snapshot.Products, snapshot.ProductsWithImage,
            await db.MediaFetchRequests.CountAsync(m => m.Status == MediaFetchStatus.Failed, ct));
    }
}

/// <summary>Step 2: deterministic rules turn signals into prioritized findings and suggested actions.</summary>
public sealed class TriageExecutor() : Executor<OperationsSnapshot, TriageResult>("triage")
{
    public override ValueTask<TriageResult> HandleAsync(OperationsSnapshot s, IWorkflowContext context, CancellationToken ct = default) =>
        ValueTask.FromResult(new TriageResult(s, Triage(s)));

    public static IReadOnlyList<Finding> Triage(OperationsSnapshot s)
    {
        var f = new List<Finding>();
        if (s.StuckImports > 0)
            f.Add(new("critical", "Imports", $"{s.StuckImports} import(s) stuck in processing for over 2 hours", "Check the worker and Event Hub consumers."));
        foreach (var i in s.FailedImports)
            f.Add(new("warning", "Imports", $"Import #{i.Id} {i.Status.ToLowerInvariant()} ({i.Supplier}, {i.FileName})", i.Reason ?? "No reason recorded.",
                i.Status == nameof(ImportStatus.Failed) ? new SuggestedAction(AgentActionKind.ReprocessImport, i.Id.ToString(), $"Import failed: {i.Reason ?? "unknown error"}") : null));
        if (s.ImportsLast24h > 0 && s.RowsRejectedLast24h > 0)
            f.Add(new("info", "Imports", $"{s.RowsRejectedLast24h:N0} rows rejected in the last 24 hours", $"{s.ImportsLast24h} files received. Review the error fields on the Imports page."));
        if (s.PendingPriceChanges + s.PendingNewProducts > 0)
            f.Add(new(s.OldestPendingChangeDays > 3 ? "warning" : "info", "Approvals",
                $"{s.PendingNewProducts:N0} new products and {s.PendingPriceChanges:N0} price changes awaiting approval",
                $"Oldest waiting {s.OldestPendingChangeDays:0.#} days.{(s.LargePriceChanges > 0 ? $" {s.LargePriceChanges} price moves of 20% or more need a careful look." : "")}"));
        foreach (var c in s.SmallPriceChanges)
            f.Add(new("info", "Approvals", $"Low-risk price change {c.Sku} ({c.Supplier}) {c.ChangePercent:+0.##;-0.##}%", $"{c.OldPrice:0.00} → {c.NewPrice:0.00}",
                new SuggestedAction(AgentActionKind.ApproveChange, c.ChangeId.ToString(), $"Price moves only {c.ChangePercent:+0.##;-0.##}%, within the ±2% low-risk band.")));
        foreach (var o in s.AwaitingSeller)
            f.Add(new("warning", "Orders", $"Order {o.Number} waiting {o.AgeHours:0}h for {o.Supplier} to accept", $"{o.Buyer}, {o.Value:0.00} {o.Currency}. Chase the seller."));
        foreach (var o in s.AwaitingShipment)
            f.Add(new("warning", "Orders", $"Order {o.Number} accepted but not shipped for {o.AgeHours / 24:0} days", $"{o.Supplier} → {o.Buyer}, {o.Value:0.00} {o.Currency}."));
        if (s.PendingPaymentsOver1h > 0)
            f.Add(new("warning", "Payments", $"{s.PendingPaymentsOver1h} payment(s) pending for over an hour", "Check the provider dashboard or webhook delivery."));
        if (s.FailedPaymentsLast7d > 0)
            f.Add(new("info", "Payments", $"{s.FailedPaymentsLast7d} failed payment attempt(s) in 7 days", "Buyers may need help or another method."));
        if (s.CompaniesPendingOver2d > 0)
            f.Add(new("warning", "Companies", $"{s.CompaniesPendingOver2d} company registration(s) waiting over 2 days", $"{s.CompaniesPending} in total awaiting verification (KYB)."));
        if (s.OrdersPrev7d >= 5 && s.Orders7d < s.OrdersPrev7d * 0.7m)
            f.Add(new("warning", "Sales", $"Orders down {(1 - s.Orders7d / s.OrdersPrev7d) * 100:0}% week on week", $"{s.Orders7d:0} vs {s.OrdersPrev7d:0}; value {s.Gmv7d:0} vs {s.GmvPrev7d:0} GBP."));
        else if (s.Orders7d > 0)
            f.Add(new("info", "Sales", $"{s.Orders7d:0} orders worth {s.Gmv7d:N0} GBP in the last 7 days", s.OrdersPrev7d > 0 ? $"Previous 7 days: {s.OrdersPrev7d:0} orders, {s.GmvPrev7d:N0} GBP." : "No orders the week before."));
        if (s.Products > 0 && s.ProductsWithImage * 100 / s.Products < 10)
            f.Add(new("info", "Catalogue", $"Only {(double)s.ProductsWithImage / s.Products:P1} of products have an image", "Ask suppliers for media packs or image links in their feeds."));
        if (s.MediaFetchFailures > 0)
            f.Add(new("info", "Catalogue", $"{s.MediaFetchFailures} media link(s) from feeds failed to download", "See Media library → links from supplier feeds."));
        var order = new Dictionary<string, int> { ["critical"] = 0, ["warning"] = 1, ["info"] = 2 };
        return f.OrderBy(x => order[x.Severity]).ToList();
    }
}

/// <summary>
/// Step 3: an agent turns the findings into a short briefing for the operations team. If the model is
/// unavailable, slow or produces nothing, a rule-based briefing is used, so the review always completes.
/// </summary>
public sealed class BriefingExecutor(IChatClient modelClient, FoundryLocalRuntime runtime, AgentOptions options, ILoggerFactory loggers, Action<string> progress)
    : Executor<TriageResult, ReviewDraft>("briefing")
{
    public override async ValueTask<ReviewDraft> HandleAsync(TriageResult triage, IWorkflowContext context, CancellationToken ct = default)
    {
        if (!options.ReviewUseModel || triage.Findings.Count == 0)
            return new ReviewDraft(triage, RuleBased(triage), false, null, triage.Findings.Count == 0 ? null : "Model briefing disabled.");
        try
        {
            // Starting Foundry Local and loading the model (about a minute) is not part of the generation budget.
            progress("Preparing the local model…");
            var connection = await runtime.EnsureReadyAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(options.ReviewModelTimeout);
            progress($"Writing the briefing with {connection.ModelId}…");

            // Structured output through a tool. Measured on Qwen 3.5 9B with Foundry Local: asked for free text, the model
            // reasons past 1,400 tokens (over 10 minutes) and never answers; asked to call a tool, it reasons for about
            // 200 tokens and calls it. The tool records the briefing and ends the run, so no further model call follows.
            string? briefing = null;
            var submit = AIFunctionFactory.Create(
                ([Description("One sentence on overall health")] string status,
                 [Description("Top 3 actions, most urgent first, each with a short reason")] JsonElement priorities,
                 [Description("One or two sentences")] string next_steps) =>
                {
                    var sb = new StringBuilder($"Status: {status.Trim()}\nPriorities:\n");
                    foreach (var (p, i) in FlattenPriorities(priorities).Take(5).Select((p, i) => (p, i)))
                        sb.AppendLine($"{i + 1}. {p}");
                    sb.Append($"Next steps: {next_steps.Trim()}");
                    briefing = sb.ToString();
                    if (FunctionInvokingChatClient.CurrentContext is { } fc) fc.Terminate = true;
                    return "Briefing recorded.";
                }, "submit_briefing", "Submit the operations briefing for the team.");

            var agent = new ChatClientAgent(modelClient, new ChatClientAgentOptions
            {
                Name = "operations-briefing",
                ChatOptions = new ChatOptions
                {
                    Instructions = "You are the operations lead of a B2B HVAC catalogue and marketplace. Read the findings and call submit_briefing once. Use only facts from the findings.",
                    Tools = [submit],
                    MaxOutputTokens = Math.Max(runtime.Options.MaxOutputTokens, 1400),
                    Temperature = 0.2f
                },
                UseProvidedChatClientAsIs = true
            }, loggers).AsBuilder().UseOpenTelemetry(CatalogueAssistant.SourceName).Build();
            var input = JsonSerializer.Serialize(triage.Findings.Take(12).Select(f => new { f.Severity, f.Area, f.Title, f.Detail }), ToolJson.Options);
            var response = await agent.RunAsync($"Findings:\n{input}", cancellationToken: cts.Token);
            // A model that answered in prose instead of calling the tool is still usable.
            var text = (briefing ?? response.Text).Trim();
            if (text.Length < 20) return new ReviewDraft(triage, RuleBased(triage), false, connection.ModelId, "The model used its whole output budget thinking and wrote no briefing.");
            return new ReviewDraft(triage, text, true, connection.ModelId, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var why = ex is OperationCanceledException ? $"Model did not answer within {options.ReviewModelTimeout.TotalMinutes:0} minutes." : ex.Message;
            return new ReviewDraft(triage, RuleBased(triage), false, null, why);
        }
    }

    /// <summary>
    /// Models differ in how they fill an array argument: Qwen sends strings, Ministral sends objects such as
    /// {"action": "...", "reason": "..."}; some send one string with line breaks. Accept all of them.
    /// </summary>
    internal static IEnumerable<string> FlattenPriorities(JsonElement e)
    {
        static string Line(JsonElement x) => x.ValueKind switch
        {
            JsonValueKind.String => x.GetString()!.Trim(),
            JsonValueKind.Object => string.Join(" - ", x.EnumerateObject().Select(p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()!.Trim() : p.Value.GetRawText())
                .Where(s => s.Length > 0)),
            _ => x.GetRawText()
        };
        var items = e.ValueKind switch
        {
            JsonValueKind.Array => e.EnumerateArray().Select(Line),
            JsonValueKind.String => e.GetString()!.Split('\n').Select(s => s.Trim().TrimStart('-', '*', ' ').Trim()),
            JsonValueKind.Object => [Line(e)],
            _ => []
        };
        // Drop the model's own numbering ("1. ...") - the briefing numbers the list itself.
        return items.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => System.Text.RegularExpressions.Regex.Replace(s, @"^\d+[.)]\s+", "")).ToList();
    }

    public static string RuleBased(TriageResult t)
    {
        var critical = t.Findings.Count(f => f.Severity == "critical");
        var warnings = t.Findings.Count(f => f.Severity == "warning");
        var sb = new StringBuilder();
        sb.AppendLine(critical > 0 ? $"Status: {critical} critical issue(s) and {warnings} warning(s) need attention."
            : warnings > 0 ? $"Status: running, with {warnings} warning(s) to follow up." : "Status: all clear, nothing urgent.");
        var top = t.Findings.Where(f => f.Severity != "info").Take(3).ToList();
        if (top.Count == 0) top = t.Findings.Take(3).ToList();
        if (top.Count > 0)
        {
            sb.AppendLine("Priorities:");
            for (var i = 0; i < top.Count; i++) sb.AppendLine($"{i + 1}. {top[i].Title}. {top[i].Detail}");
        }
        var actions = t.Findings.Count(f => f.Action is not null);
        sb.Append(actions > 0 ? $"Next steps: review the {actions} proposed action(s) and approve the ones you agree with." : "Next steps: none required.");
        return sb.ToString();
    }
}

/// <summary>Step 4: store the review and turn suggested actions into proposals awaiting human approval.</summary>
public sealed class PublishExecutor(ICatalogueDbContext db, TimeProvider clock) : Executor<ReviewDraft, ReviewOutcome>("publish")
{
    public override async ValueTask<ReviewOutcome> HandleAsync(ReviewDraft draft, IWorkflowContext context, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var review = await db.OperationsReviews.FirstAsync(r => r.Id == draft.Triage.Snapshot.ReviewId, ct);
        var created = 0;
        foreach (var f in draft.Triage.Findings.Where(f => f.Action is not null))
        {
            var a = f.Action!;
            if (await db.AgentActionProposals.AnyAsync(p => p.Kind == a.Kind && p.TargetId == a.TargetId && p.Status == ProposalStatus.Pending, ct)) continue;
            db.AgentActionProposals.Add(new AgentActionProposal(a.Kind, a.TargetId, f.Title, a.Reason, "operations-review", review.RequestedBy, reviewId: review.Id));
            created++;
        }
        var findings = draft.Triage.Findings;
        review.Complete(draft.Briefing, draft.ByModel, draft.ModelId, JsonSerializer.Serialize(findings, ToolJson.Options),
            JsonSerializer.Serialize(draft.Triage.Snapshot, ToolJson.Options),
            findings.Count(x => x.Severity == "critical"), findings.Count(x => x.Severity == "warning"), findings.Count(x => x.Severity == "info"),
            created, draft.ModelError, now);
        db.AuditEvents.Add(new AuditEvent(review.RequestedBy, "OperationsReviewCompleted", "OperationsReview", review.Id.ToString(),
            $"{findings.Count} findings, {created} proposals, briefing by {(draft.ByModel ? draft.ModelId : "rules")}", null));
        await db.SaveChangesAsync(ct);
        // Returned value becomes the workflow output (WithOutputFrom(publish)).
        return new ReviewOutcome(review.Id, findings.Count, created, draft.ByModel);
    }
}

/// <summary>Builds and runs the review workflow (collect → triage → briefing → publish) as a queued agent run.</summary>
public sealed class OperationsReviewRunner(IServiceScopeFactory scopes, AgentRunCoordinator coordinator, IChatClient modelClient,
    FoundryLocalRuntime runtime, AgentOptions options, ILoggerFactory loggers, TimeProvider clock)
{
    private static readonly ActivitySource Activity = new(CatalogueAssistant.SourceName);

    public async Task<(long ReviewId, AgentRun Run)> StartAsync(string trigger, string requestedBy, long userId, CancellationToken ct)
    {
        long reviewId;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ICatalogueDbContext>();
            if (await db.OperationsReviews.AnyAsync(r => r.Status == ReviewStatus.Running && r.StartedAtUtc > clock.GetUtcNow().UtcDateTime.AddHours(-1), ct))
                throw new AgentBusyException("A review is already running.");
            var review = new OperationsReview(trigger, requestedBy);
            db.OperationsReviews.Add(review);
            await db.SaveChangesAsync(ct);
            reviewId = review.Id;
        }
        try
        {
            var run = coordinator.Enqueue("review", userId, null, (r, token) => ExecuteAsync(r, new ReviewRequest(reviewId, trigger, requestedBy), token));
            return (reviewId, run);
        }
        catch (AgentBusyException ex)
        {
            await MarkFailedAsync(reviewId, ex.Message);
            throw;
        }
    }

    internal Workflow Build(IServiceProvider sp, Action<string> progress)
    {
        var collect = new CollectSignalsExecutor(sp.GetRequiredService<ICatalogueDbContext>(), sp.GetRequiredService<AnalyticsService>(), clock);
        var triage = new TriageExecutor();
        var briefing = new BriefingExecutor(modelClient, runtime, options, loggers, progress);
        var publish = new PublishExecutor(sp.GetRequiredService<ICatalogueDbContext>(), clock);
        return new WorkflowBuilder(collect)
            .AddEdge(collect, triage).AddEdge(triage, briefing).AddEdge(briefing, publish)
            .WithOutputFrom(publish)
            .WithName("operations-review")
            .WithDescription("Collects operational signals, triages them, writes a briefing and proposes actions for approval.")
            .WithOpenTelemetry(null, Activity)
            .Build();
    }

    private async Task ExecuteAsync(AgentRun run, ReviewRequest request, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var steps = new Dictionary<string, string>
        {
            ["collect-signals"] = "Collecting signals from imports, approvals, orders, payments and companies",
            ["triage"] = "Triaging findings",
            ["briefing"] = "Writing the briefing",
            ["publish"] = "Saving the review and proposals"
        };
        try
        {
            var workflow = Build(scope.ServiceProvider, message => run.Publish("status", new { message }));
            await using var stream = await InProcessExecution.RunStreamingAsync(workflow, request, cancellationToken: ct);
            await foreach (var evt in stream.WatchStreamAsync(ct))
            {
                switch (evt)
                {
                    case ExecutorInvokedEvent invoked when invoked.ExecutorId is { } id && steps.ContainsKey(id):
                        run.Publish("step", new { step = id, state = "running", label = steps[id] });
                        break;
                    case ExecutorCompletedEvent done when done.ExecutorId is { } id && steps.ContainsKey(id):
                        run.Publish("step", new { step = id, state = "done", label = steps[id], summary = Summarize(done.Data) });
                        break;
                    case ExecutorFailedEvent failed:
                        throw new InvalidOperationException($"Step {failed.ExecutorId} failed: {failed.Data?.Message}", failed.Data);
                    case WorkflowOutputEvent output when output.Is<ReviewOutcome>(out var outcome):
                        run.Publish("review", new { reviewId = outcome.ReviewId, findings = outcome.Findings, proposals = outcome.Proposals, byModel = outcome.ByModel });
                        return;
                }
            }
            throw new InvalidOperationException("The review workflow ended without a result.");
        }
        catch (Exception ex)
        {
            await MarkFailedAsync(request.ReviewId, ex is OperationCanceledException ? "Cancelled or timed out." : ex.Message);
            throw;
        }
    }

    private static object? Summarize(object? data) => data switch
    {
        OperationsSnapshot s => $"{s.FailedImports.Count} failed imports, {s.AwaitingSeller.Count + s.AwaitingShipment.Count} stale orders, {s.PendingNewProducts + s.PendingPriceChanges:N0} pending approvals",
        TriageResult t => $"{t.Findings.Count} findings ({t.Findings.Count(f => f.Severity == "critical")} critical, {t.Findings.Count(f => f.Severity == "warning")} warnings)",
        ReviewDraft d => d.ByModel ? $"Briefing written by {d.ModelId}" : $"Rule-based briefing ({d.ModelError ?? "model not used"})",
        ReviewOutcome o => $"{o.Proposals} proposal(s) created",
        _ => null
    };

    private async Task MarkFailedAsync(long reviewId, string error)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ICatalogueDbContext>();
        var review = await db.OperationsReviews.FirstOrDefaultAsync(r => r.Id == reviewId);
        if (review is null || review.Status != ReviewStatus.Running) return;
        review.Fail(error, clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(CancellationToken.None);
    }
}

/// <summary>Runs the review on a schedule and expires proposals nobody decided on.</summary>
public sealed class OperationsReviewScheduler(IServiceScopeFactory scopes, OperationsReviewRunner runner, AgentOptions options,
    ILogger<OperationsReviewScheduler> log, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        do
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Operations review scheduling failed"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ICatalogueDbContext>();
        foreach (var p in await db.AgentActionProposals.Where(p => p.Status == ProposalStatus.Pending && p.ExpiresAtUtc <= now).Take(500).ToListAsync(ct))
            p.Expire(now);
        // Reviews left "Running" by a restart.
        foreach (var r in await db.OperationsReviews.Where(r => r.Status == ReviewStatus.Running && r.StartedAtUtc < now.AddHours(-1)).ToListAsync(ct))
            r.Fail("Interrupted (server restart or timeout).", now);
        await db.SaveChangesAsync(ct);

        if (!options.ReviewEnabled || options.ReviewInterval <= TimeSpan.Zero) return;
        var last = await db.OperationsReviews.OrderByDescending(r => r.StartedAtUtc).Select(r => (DateTime?)r.StartedAtUtc).FirstOrDefaultAsync(ct);
        if (last is not null && now - last.Value < options.ReviewInterval) return;
        try
        {
            var (id, _) = await runner.StartAsync("schedule", "operations-review", 0, ct);
            log.LogInformation("Scheduled operations review {ReviewId} queued", id);
        }
        catch (AgentBusyException ex) { log.LogInformation("Scheduled review skipped: {Reason}", ex.Message); }
    }
}
