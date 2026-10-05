using Catalogue.Agents;
using Catalogue.Agents.FoundryLocal;
using Catalogue.Agents.Tools;
using Catalogue.Application;
using Catalogue.Domain;
using Catalogue.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Catalogue.Web.Agent;

/// <summary>Builds the agent's view of the signed-in user from the same authorization policies the API enforces.</summary>
public sealed class AgentPrincipalFactory(IHttpContextAccessor http, IAuthorizationService authz, CurrentUser me, ICatalogueDbContext db)
{
    public async Task<AgentPrincipal> CurrentAsync(CancellationToken ct)
    {
        var user = http.HttpContext?.User ?? throw new InvalidOperationException("No signed-in user.");
        var policies = new HashSet<string>();
        foreach (var p in AgentPolicies.All)
            if ((await authz.AuthorizeAsync(user, p)).Succeeded) policies.Add(p);
        var profile = await db.Users.AsNoTracking().Where(u => u.Id == me.UserId)
            .Select(u => new { u.DisplayName, Org = u.Organization != null ? u.Organization.Name : null }).FirstOrDefaultAsync(ct);
        return new AgentPrincipal(me.UserId ?? 0, me.Name, profile?.DisplayName ?? me.Name, policies, me.IsPlatformStaff, me.IsInRole(AppRoles.Admin),
            me.OrganizationId, profile?.Org, me.SupplierId, me.CustomerAccountId);
    }
}

public sealed record DecisionResult(int StatusCode, string Message, AgentActionProposal? Proposal);

/// <summary>
/// Executes a proposal once a person approves it. The approver is re-validated against the action (their own
/// permissions, the current state of the target), then the same services as the regular endpoints do the work.
/// </summary>
public sealed class AgentActionExecutor(ICatalogueDbContext db, ApprovalService approvals, OrderStatusService orders, ImportReprocessService imports,
    IActorContext actor, HybridCache cache, TimeProvider clock)
{
    public async Task<DecisionResult> DecideAsync(long proposalId, bool approve, string? note, AgentPrincipal approver, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var p = await db.AgentActionProposals.FirstOrDefaultAsync(x => x.Id == proposalId, ct);
        if (p is null) return new(404, "Proposal not found.", null);
        if (!p.IsOpen(now)) return new(409, $"Proposal {p.Id} is {(p.Status == ProposalStatus.Pending ? "expired" : p.Status.ToString().ToLowerInvariant())}.", p);

        string? permissionError = null;
        try { await ProposalTargets.ValidateAsync(db, approver, p.Kind, p.TargetId, ct); }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("You are not allowed", StringComparison.Ordinal) || ex.Message.StartsWith("Only the", StringComparison.Ordinal))
        { permissionError = ex.Message; }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            // The target changed since it was proposed (order already accepted, import running...).
            if (!approve) { p.Rejected(approver.UserName, note, now); await SaveAsync(p, "AgentProposalRejected", note, ct); return new(200, "Proposal rejected.", p); }
            p.Failed(approver.UserName, ex.Message, now);
            await SaveAsync(p, "AgentProposalFailed", ex.Message, ct);
            return new(409, ex.Message, p);
        }

        if (!approve)
        {
            if (permissionError is not null && p.RequestedBy != approver.UserName && !approver.IsAdmin) return new(403, permissionError, p);
            p.Rejected(approver.UserName, note, now);
            await SaveAsync(p, "AgentProposalRejected", note, ct);
            return new(200, "Proposal rejected.", p);
        }
        if (permissionError is not null) return new(403, permissionError, p);

        try
        {
            var result = await ExecuteAsync(p, note, ct);
            p.Executed(approver.UserName, result, now);
            await SaveAsync(p, "AgentProposalExecuted", result, ct);
            return new(200, result, p);
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or PaymentException)
        {
            (db as DbContext)?.ChangeTracker.Clear(); // drop half-applied changes from the failed action before recording the failure
            var fresh = await db.AgentActionProposals.FirstAsync(x => x.Id == proposalId, ct);
            fresh.Failed(approver.UserName, ex.Message, now);
            await SaveAsync(fresh, "AgentProposalFailed", ex.Message, ct);
            return new(ex is PaymentException pe ? pe.StatusCode : 409, ex.Message, fresh);
        }
    }

    private async Task<string> ExecuteAsync(AgentActionProposal p, string? note, CancellationToken ct)
    {
        var why = string.IsNullOrWhiteSpace(note) ? $"Approved assistant proposal #{p.Id}" : note.Trim();
        switch (p.Kind)
        {
            case AgentActionKind.ReprocessImport:
                if (!await imports.ReprocessAsync(long.Parse(p.TargetId), actor.ActorId, actor.CorrelationId, ct)) throw new KeyNotFoundException($"Import {p.TargetId} no longer exists.");
                return $"Import #{p.TargetId} queued for reprocessing.";
            case AgentActionKind.ApproveChange or AgentActionKind.RejectChange:
                var approve = p.Kind == AgentActionKind.ApproveChange;
                var decided = await approvals.DecideAsync([long.Parse(p.TargetId)], approve, why, ct);
                await cache.RemoveByTagAsync(CacheTags.Products, ct);
                if (decided == 0) throw new InvalidOperationException($"Change #{p.TargetId} was already decided.");
                return $"Change #{p.TargetId} {(approve ? "approved" : "rejected")}.";
            default:
                var order = await db.PurchaseOrders.FirstOrDefaultAsync(o => o.Number == p.TargetId, ct) ?? throw new KeyNotFoundException($"Order {p.TargetId} not found.");
                var next = p.Kind switch
                {
                    AgentActionKind.AcceptOrder => PurchaseOrderStatus.Accepted,
                    AgentActionKind.RejectOrder => PurchaseOrderStatus.Rejected,
                    AgentActionKind.ShipOrder => PurchaseOrderStatus.Shipped,
                    _ => PurchaseOrderStatus.Cancelled
                };
                await orders.ChangeAsync(order, next, why, actor.ActorId, actor.CorrelationId, ct);
                return $"Order {order.Number} is now {next.ToString().ToLowerInvariant()}{(order.PaymentStatus == OrderPaymentStatus.Refunded ? " (payment refunded)" : "")}.";
        }
    }

    private async Task SaveAsync(AgentActionProposal p, string action, string? detail, CancellationToken ct)
    {
        db.AuditEvents.Add(new AuditEvent(actor.ActorId, action, "AgentActionProposal", p.Id.ToString(), $"{p.Kind} {p.TargetId}: {detail}", actor.CorrelationId));
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Reports the local model as Degraded (never Unhealthy): the catalogue works without it.</summary>
public sealed class FoundryLocalHealthCheck(FoundryLocalRuntime runtime) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var s = await runtime.GetStatusAsync(false, ct);
        var data = new Dictionary<string, object> { ["model"] = s.Model, ["endpoint"] = s.Endpoint ?? "", ["loaded"] = s.ModelLoaded };
        if (!s.Enabled) return HealthCheckResult.Healthy("Assistant disabled.", data);
        if (s.Ready) return HealthCheckResult.Healthy($"{s.ModelId} loaded at {s.Endpoint}.", data);
        if (s.ServiceRunning && s.ModelCached) return HealthCheckResult.Healthy($"{s.ModelId} available; loads on first use.", data);
        return HealthCheckResult.Degraded(s.Error ?? "Foundry Local unavailable.", data: data);
    }
}
