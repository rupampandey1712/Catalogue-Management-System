using System.Text.Json;
using Catalogue.Agents;
using Catalogue.Agents.FoundryLocal;
using Catalogue.Agents.Review;
using Catalogue.Agents.Runs;
using Catalogue.Application;
using Catalogue.Domain;
using Catalogue.Web.Agent;
using Catalogue.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Catalogue.Web.Endpoints;

/// <summary>
/// The operations assistant (chat), the operations review workflow and the approval queue for actions the
/// agent proposes. Model work runs in a background queue; clients follow it over Server-Sent Events.
/// </summary>
public static class AgentEndpoints
{
    public sealed record AskRequest(string Text);
    public sealed record DecideRequest(string? Note);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Map(IEndpointRouteBuilder api)
    {
        var agent = api.MapGroup("/agent").WithTags("Assistant").RequireAuthorization();

        agent.MapGet("/status", async (FoundryLocalRuntime runtime, AgentRunCoordinator runs, AgentOptions options, ICatalogueDbContext db,
            CurrentUser me, bool? refresh, CancellationToken ct) =>
        {
            var s = await runtime.GetStatusAsync(refresh == true, ct);
            var lastReview = await db.OperationsReviews.AsNoTracking().OrderByDescending(r => r.StartedAtUtc)
                .Select(r => new { r.Id, status = r.Status.ToString(), r.StartedAtUtc, r.CompletedAtUtc }).FirstOrDefaultAsync(ct);
            var mine = runs.Active().FirstOrDefault(r => r.UserId == me.UserId);
            return Results.Ok(new
            {
                enabled = options.Enabled && s.Enabled,
                model = new { s.Model, s.ModelId, s.ServiceRunning, s.ModelCached, s.ModelLoaded, s.Ready, s.SupportsToolCalling, s.Device, s.Error,
                    endpoint = me.IsPlatformStaff ? s.Endpoint : null, s.CheckedAtUtc },
                queue = new { waiting = runs.Waiting, running = runs.Active().Count(r => r.Status == AgentRunStatus.Running), myRun = mine?.Id },
                limits = new { runTimeoutMinutes = options.RunTimeout.TotalMinutes, options.MaxToolCallsPerRun, options.HistoryMessages },
                review = new { enabled = options.ReviewEnabled, intervalHours = options.ReviewInterval.TotalHours, useModel = options.ReviewUseModel, last = lastReview }
            });
        });

        // Loading a 7 GB model takes about a minute; do it in the background and let the UI poll /status.
        agent.MapPost("/model/load", (FoundryLocalRuntime runtime, ILogger<FoundryLocalRuntime> log) =>
        {
            _ = Task.Run(async () =>
            {
                try { await runtime.EnsureReadyAsync(CancellationToken.None); }
                catch (Exception ex) { log.LogWarning("Model load requested from the UI failed: {Error}", ex.Message); }
            });
            return Results.Accepted("/api/agent/status");
        }).RequireAuthorization("PlatformAdmin");

        // ----- conversations -----
        agent.MapGet("/conversations", async (CurrentUser me, ICatalogueDbContext db, CancellationToken ct) =>
            Results.Ok(await db.AgentConversations.AsNoTracking().Where(c => c.UserId == me.UserId).OrderByDescending(c => c.UpdatedAtUtc).Take(50)
                .Select(c => new { c.Id, c.Title, c.MessageCount, c.CreatedAtUtc, c.UpdatedAtUtc }).ToListAsync(ct)));

        agent.MapPost("/conversations", async (CurrentUser me, ICatalogueDbContext db, CancellationToken ct) =>
        {
            if (me.UserId is not { } uid) return Results.Problem(statusCode: 403, title: "Sign in again.");
            var c = new AgentConversation(uid, me.Name);
            db.AgentConversations.Add(c);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/agent/conversations/{c.Id}", new { c.Id, c.Title });
        });

        agent.MapGet("/conversations/{id:guid}", async (Guid id, CurrentUser me, ICatalogueDbContext db, AgentRunCoordinator runs, CancellationToken ct) =>
        {
            var c = await db.AgentConversations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.UserId == me.UserId, ct);
            if (c is null) return Results.NotFound();
            var messages = await db.AgentMessages.AsNoTracking().Where(m => m.ConversationId == id).OrderBy(m => m.Id).Take(200).ToListAsync(ct);
            var proposals = await db.AgentActionProposals.AsNoTracking().Where(p => p.ConversationId == id).OrderBy(p => p.Id).ToListAsync(ct);
            var active = runs.Active().FirstOrDefault(r => r.ConversationId == id);
            return Results.Ok(new
            {
                c.Id, c.Title, c.CreatedAtUtc, c.UpdatedAtUtc, activeRunId = active?.Id,
                messages = messages.Select(m => new
                {
                    m.Id, m.Role, m.Content, m.Reasoning, m.Error, m.DurationMs, m.InputTokens, m.OutputTokens, m.CreatedAtUtc,
                    tools = m.ToolTrace is null ? (object?)null : JsonSerializer.Deserialize<JsonElement>(m.ToolTrace)
                }),
                proposals = proposals.Select(ToDto)
            });
        });

        agent.MapDelete("/conversations/{id:guid}", async (Guid id, CurrentUser me, ICatalogueDbContext db, AgentRunCoordinator runs, CancellationToken ct) =>
        {
            var c = await db.AgentConversations.FirstOrDefaultAsync(x => x.Id == id && x.UserId == me.UserId, ct);
            if (c is null) return Results.NotFound();
            if (runs.Active().Any(r => r.ConversationId == id)) return Results.Conflict(new { error = "Wait for the current answer or cancel it first." });
            db.AgentConversations.Remove(c);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        agent.MapPost("/conversations/{id:guid}/messages", async (Guid id, AskRequest req, AgentPrincipalFactory principals,
            CatalogueAssistant assistant, CancellationToken ct) =>
        {
            var text = req.Text?.Trim() ?? "";
            if (text.Length is 0 or > 2000)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["text"] = ["Ask a question of 1 to 2,000 characters."] });
            try
            {
                var run = await assistant.AskAsync(await principals.CurrentAsync(ct), id, text, ct);
                return Results.Accepted($"/api/agent/runs/{run.Id}/events", new { runId = run.Id });
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (AgentBusyException ex) { return Results.Problem(statusCode: 429, title: "Assistant busy", detail: ex.Message); }
        }).RequireRateLimiting(Policies.Agent);

        // ----- runs (chat turns and reviews) -----
        agent.MapGet("/runs/{id:guid}/events", async (Guid id, int? after, HttpContext http, CurrentUser me, AgentRunCoordinator runs, CancellationToken ct) =>
        {
            var run = runs.Get(id);
            if (run is null || !CanSee(run, me)) { http.Response.StatusCode = 404; return; }
            var lastEventId = int.TryParse(http.Request.Headers["Last-Event-ID"], out var l) ? l : after ?? 0;
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
            await foreach (var e in run.ReadAsync(lastEventId, ct))
            {
                if (e.Type == "heartbeat") await http.Response.WriteAsync(": keep-alive\n\n", ct);
                else await http.Response.WriteAsync($"id: {e.Seq}\nevent: {e.Type}\ndata: {JsonSerializer.Serialize(e.Data, Json)}\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
            }
        }).ExcludeFromDescription().DisableRequestTimeout();

        agent.MapPost("/runs/{id:guid}/cancel", (Guid id, CurrentUser me, AgentRunCoordinator runs) =>
        {
            var run = runs.Get(id);
            if (run is null || !CanSee(run, me)) return Results.NotFound();
            run.Cancel();
            return Results.Accepted();
        });

        // ----- proposals awaiting approval -----
        agent.MapGet("/proposals", async (string? status, CurrentUser me, ICatalogueDbContext db, CancellationToken ct) =>
        {
            var q = db.AgentActionProposals.AsNoTracking();
            if (!me.IsPlatformStaff) q = q.Where(p => p.RequestedBy == me.Name);
            if (Enum.TryParse<ProposalStatus>(status, true, out var st)) q = q.Where(p => p.Status == st);
            var items = await q.OrderByDescending(p => p.Id).Take(100).ToListAsync(ct);
            return Results.Ok(items.Select(ToDto));
        });

        agent.MapPost("/proposals/{id:long}/approve", (long id, DecideRequest req, AgentPrincipalFactory principals, AgentActionExecutor executor, CancellationToken ct) =>
            Decide(id, true, req.Note, principals, executor, ct));
        agent.MapPost("/proposals/{id:long}/reject", (long id, DecideRequest req, AgentPrincipalFactory principals, AgentActionExecutor executor, CancellationToken ct) =>
            Decide(id, false, req.Note, principals, executor, ct));

        // ----- operations review -----
        var reviews = agent.MapGroup("/reviews").RequireAuthorization("ImportOps");

        reviews.MapGet("/", async (ICatalogueDbContext db, CancellationToken ct) =>
            Results.Ok(await db.OperationsReviews.AsNoTracking().OrderByDescending(r => r.Id).Take(30)
                .Select(r => new { r.Id, r.Trigger, r.RequestedBy, status = r.Status.ToString(), r.StartedAtUtc, r.CompletedAtUtc, r.DurationMs,
                    r.Critical, r.Warnings, r.Infos, r.ProposalCount, r.GeneratedByModel, r.ModelId, r.Error })
                .ToListAsync(ct)));

        reviews.MapGet("/{id:long}", async (long id, ICatalogueDbContext db, AgentRunCoordinator runs, CancellationToken ct) =>
        {
            var r = await db.OperationsReviews.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (r is null) return Results.NotFound();
            var proposals = await db.AgentActionProposals.AsNoTracking().Where(p => p.ReviewId == id).OrderBy(p => p.Id).ToListAsync(ct);
            return Results.Ok(new
            {
                r.Id, r.Trigger, r.RequestedBy, status = r.Status.ToString(), r.StartedAtUtc, r.CompletedAtUtc, r.DurationMs, r.Briefing,
                r.GeneratedByModel, r.ModelId, r.Error, r.Critical, r.Warnings, r.Infos,
                findings = JsonSerializer.Deserialize<JsonElement>(r.FindingsJson),
                snapshot = r.SnapshotJson is null ? (object?)null : JsonSerializer.Deserialize<JsonElement>(r.SnapshotJson),
                proposals = proposals.Select(ToDto)
            });
        });

        reviews.MapPost("/", async (CurrentUser me, OperationsReviewRunner runner, CancellationToken ct) =>
        {
            try
            {
                var (reviewId, run) = await runner.StartAsync("manual", me.Name, me.UserId ?? 0, ct);
                return Results.Accepted($"/api/agent/reviews/{reviewId}", new { reviewId, runId = run.Id });
            }
            catch (AgentBusyException ex) { return Results.Problem(statusCode: 429, title: "Review not started", detail: ex.Message); }
        }).RequireRateLimiting(Policies.Agent);
    }

    private static async Task<IResult> Decide(long id, bool approve, string? note, AgentPrincipalFactory principals, AgentActionExecutor executor, CancellationToken ct)
    {
        if (note is { Length: > 500 }) return Results.ValidationProblem(new Dictionary<string, string[]> { ["note"] = ["At most 500 characters."] });
        var result = await executor.DecideAsync(id, approve, note, await principals.CurrentAsync(ct), ct);
        return result.StatusCode == 200
            ? Results.Ok(new { message = result.Message, proposal = result.Proposal is null ? null : ToDto(result.Proposal) })
            : Results.Problem(statusCode: result.StatusCode, title: approve ? "Not executed" : "Not rejected", detail: result.Message);
    }

    private static bool CanSee(AgentRun run, CurrentUser me) => run.UserId == me.UserId || (run.UserId == 0 && me.IsPlatformStaff) || me.IsInRole(AppRoles.Admin);

    private static object ToDto(AgentActionProposal p) => new
    {
        p.Id, kind = p.Kind.ToString(), p.TargetId, p.TargetLabel, p.Reason, p.Source, p.ConversationId, p.ReviewId, p.RequestedBy,
        status = p.Status == ProposalStatus.Pending && p.ExpiresAtUtc <= DateTime.UtcNow ? "Expired" : p.Status.ToString(),
        p.DecidedBy, p.DecidedAtUtc, p.Result, p.CreatedAtUtc, p.ExpiresAtUtc
    };
}

public static class AnalyticsEndpoints
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/analytics", async (string? range, CurrentUser me, AnalyticsService analytics, CancellationToken ct) =>
        {
            if (range is not null && !AnalyticsWindow.Ranges.Contains(range.ToLowerInvariant()))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["range"] = [$"Use one of: {string.Join(", ", AnalyticsWindow.Ranges)}."] });
            var scope = new AnalyticsScope(me.IsPlatformStaff, me.OrganizationId, me.SupplierId);
            if (!scope.Platform && me.OrganizationId is null) return Results.Problem(statusCode: 403, title: "No company on this account.");
            return Results.Ok(await analytics.GetAsync(range, scope, ct));
        }).RequireAuthorization().WithTags("Analytics").WithRequestTimeout(Policies.LongRunning);
}
