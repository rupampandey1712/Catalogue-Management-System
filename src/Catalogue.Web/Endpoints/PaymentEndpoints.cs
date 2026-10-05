using System.Text.Json;
using Catalogue.Application;
using Catalogue.Domain;
using Catalogue.Infrastructure.Payments;
using Catalogue.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Catalogue.Web.Endpoints;

/// <summary>
/// Online payment of purchase orders. Buyers either settle on their trade account (default, no action) or
/// pay online through a gateway: Stripe Checkout (hosted page, test mode with sk_test_ keys) or the local
/// simulator, which only updates the database.
/// </summary>
public static class PaymentEndpoints
{
    public sealed record PayRequest(string Provider);
    public sealed record SimulateRequest(string Outcome);
    public sealed record PaymentMethodDto(string Provider, string Name, bool TestMode);
    public sealed record PaymentDto(long Id, long OrderId, string OrderNumber, string Provider, string Status, bool IsTest, decimal Amount,
        string Currency, string? CheckoutUrl, string? PaymentReference, string? RefundReference, string? FailureReason, string CreatedBy,
        DateTime CreatedAtUtc, DateTime? CompletedAtUtc, DateTime? RefundedAtUtc, string BuyerName, string SupplierCode, string SupplierName);

    public static void Map(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/payments").WithTags("Payments");

        group.MapGet("/methods", (PaymentService payments) =>
            Results.Ok(payments.Gateways.Select(g => new PaymentMethodDto(g.Provider.ToString(), g.DisplayName, g.IsTestMode))))
            .RequireAuthorization("Marketplace");

        api.MapPost("/marketplace/orders/{id:long}/pay", async (long id, PayRequest req, CurrentUser me, ICatalogueDbContext db,
            PaymentService payments, PaymentOptions options, CancellationToken ct) =>
        {
            if (me.OrganizationId is not { } orgId) return Results.Problem(statusCode: 403, title: "Only company users can pay orders.");
            if (!Enum.TryParse<PaymentProvider>(req.Provider, true, out var provider))
                return Results.BadRequest(new { error = $"Unknown payment method '{req.Provider}'." });
            var email = await db.Users.Where(u => u.Id == me.UserId).Select(u => u.Email).FirstOrDefaultAsync(ct);
            try
            {
                var (payment, checkout) = await payments.StartAsync(id, orgId, provider, me.Name, email, options.ReturnBaseUrl, ct);
                return Results.Ok(new
                {
                    paymentId = payment.Id, provider = payment.Provider.ToString(), status = payment.Status.ToString(),
                    payment.Amount, payment.Currency, payment.IsTest, redirectUrl = checkout.RedirectUrl
                });
            }
            catch (PaymentException ex) { return Results.Problem(statusCode: ex.StatusCode, title: "Payment not started", detail: ex.Message); }
        }).RequireAuthorization("Buyer").WithTags("Payments");

        // sync=true asks the provider for the latest state (the buyer just came back from checkout). This
        // makes Stripe work locally even when no webhook can reach this machine.
        group.MapGet("/{id:long}", async (long id, bool? sync, CurrentUser me, ICatalogueDbContext db, PaymentService payments, CancellationToken ct) =>
        {
            var payment = await db.Payments.Include(p => p.PurchaseOrder).FirstOrDefaultAsync(p => p.Id == id, ct);
            if (payment is null) return Results.NotFound();
            var access = await MarketplaceEndpoints.AccessAsync(payment.PurchaseOrder!, me, db, ct);
            if (access == MarketplaceEndpoints.Access.None) return Results.NotFound();
            if (sync == true && access is MarketplaceEndpoints.Access.Buyer or MarketplaceEndpoints.Access.Staff)
            {
                try { await payments.SyncAsync(payment, me.Name, ct); }
                catch (PaymentException ex) { return Results.Problem(statusCode: ex.StatusCode, title: "Payment check failed", detail: ex.Message); }
                catch (HttpRequestException ex) { return Results.Problem(statusCode: 502, title: "Payment provider unavailable", detail: ex.Message); }
            }
            return Results.Ok(await Project(db.Payments.Where(p => p.Id == id)).FirstAsync(ct));
        }).RequireAuthorization("Marketplace");

        // The local test checkout: approve or decline without any provider. Only for simulated payments.
        group.MapPost("/{id:long}/simulate", async (long id, SimulateRequest req, CurrentUser me, ICatalogueDbContext db,
            PaymentService payments, CancellationToken ct) =>
        {
            var payment = await db.Payments.Include(p => p.PurchaseOrder).FirstOrDefaultAsync(p => p.Id == id, ct);
            if (payment is null || payment.PurchaseOrder!.BuyerOrganizationId != me.OrganizationId) return Results.NotFound();
            if (payment.Provider != PaymentProvider.Simulated)
                return Results.Problem(statusCode: 400, title: "Only simulated payments can be completed here.");
            if (payment.Status != PaymentAttemptStatus.Pending)
                return Results.Conflict(new { error = $"Payment {payment.Id} is already {payment.Status.ToString().ToLowerInvariant()}." });
            var result = req.Outcome.ToLowerInvariant() switch
            {
                "approve" => new GatewayResult(PaymentAttemptStatus.Succeeded, $"sim_pi_{Guid.NewGuid():N}", null),
                "decline" => new GatewayResult(PaymentAttemptStatus.Failed, null, "Declined by the test checkout (simulated)"),
                "cancel" => new GatewayResult(PaymentAttemptStatus.Cancelled, null, "Buyer left the test checkout"),
                _ => null
            };
            if (result is null) return Results.BadRequest(new { error = "Outcome must be approve, decline or cancel." });
            try { await payments.ApplyAsync(payment, result, me.Name, ct); }
            catch (PaymentException ex) { return Results.Problem(statusCode: ex.StatusCode, title: "Payment not applied", detail: ex.Message); }
            return Results.Ok(await Project(db.Payments.Where(p => p.Id == id)).FirstAsync(ct));
        }).RequireAuthorization("Buyer");

        // role=buyer: payments my company made. role=seller: payments for orders to my company. Staff: all.
        group.MapGet("/", async (string? role, string? status, int? page, int? pageSize, CurrentUser me, ICatalogueDbContext db, CancellationToken ct) =>
        {
            var pg = Math.Max(1, page ?? 1);
            var ps = Math.Clamp(pageSize ?? 25, 1, 100);
            var q = db.Payments.AsNoTracking();
            if (role == "seller")
                q = me.SupplierId is { } sid && !me.IsInRole(AppRoles.Admin) ? q.Where(p => p.PurchaseOrder!.SupplierId == sid)
                    : me.IsPlatformStaff ? q : q.Where(_ => false);
            else if (role == "all" && me.IsPlatformStaff) { }
            else q = me.OrganizationId is { } oid ? q.Where(p => p.PurchaseOrder!.BuyerOrganizationId == oid) : q.Where(_ => false);
            if (Enum.TryParse<PaymentAttemptStatus>(status, true, out var st)) q = q.Where(p => p.Status == st);

            var total = await q.CountAsync(ct);
            var collected = await q.Where(p => p.Status == PaymentAttemptStatus.Succeeded).GroupBy(p => p.Currency)
                .Select(g => new { currency = g.Key, amount = g.Sum(p => p.Amount), count = g.Count() }).ToListAsync(ct);
            var refunded = await q.Where(p => p.Status == PaymentAttemptStatus.Refunded).GroupBy(p => p.Currency)
                .Select(g => new { currency = g.Key, amount = g.Sum(p => p.Amount), count = g.Count() }).ToListAsync(ct);
            var items = await Project(q.OrderByDescending(p => p.CreatedAtUtc).Skip((pg - 1) * ps).Take(ps)).ToListAsync(ct);
            return Results.Ok(new { items, total, page = pg, pageSize = ps, collected, refunded });
        }).RequireAuthorization("Marketplace");

        // Stripe → us. Authenticated by the signature, not a bearer token. Configure the endpoint in the Stripe
        // dashboard, or locally: stripe listen --forward-to localhost:5080/api/payments/webhooks/stripe
        group.MapPost("/webhooks/stripe", async (HttpRequest request, PaymentOptions options, ICatalogueDbContext db,
            PaymentService payments, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var log = loggers.CreateLogger("Catalogue.Payments.Stripe");
            if (string.IsNullOrEmpty(options.StripeWebhookSecret)) return Results.NotFound();
            using var reader = new StreamReader(request.Body);
            var payload = await reader.ReadToEndAsync(ct);
            if (payload.Length > 512 * 1024) return Results.BadRequest();
            if (!StripeWebhook.Verify(payload, request.Headers["Stripe-Signature"], options.StripeWebhookSecret, DateTimeOffset.UtcNow))
            {
                log.LogWarning("Rejected Stripe webhook with an invalid signature");
                return Results.BadRequest(new { error = "Invalid signature." });
            }
            using var doc = JsonDocument.Parse(payload);
            var type = doc.RootElement.GetProperty("type").GetString();
            if (type is not ("checkout.session.completed" or "checkout.session.async_payment_succeeded"
                or "checkout.session.async_payment_failed" or "checkout.session.expired"))
                return Results.Ok(); // acknowledged, not interesting

            var session = doc.RootElement.GetProperty("data").GetProperty("object");
            var sessionId = session.GetProperty("id").GetString();
            var payment = await db.Payments.Include(p => p.PurchaseOrder)
                .FirstOrDefaultAsync(p => p.Provider == PaymentProvider.Stripe && p.CheckoutReference == sessionId, ct);
            if (payment is null)
            {
                log.LogWarning("Stripe webhook {Type} for unknown checkout session {Session}", type, sessionId);
                return Results.Ok(); // not ours (shared test account) - don't make Stripe retry
            }
            var result = type == "checkout.session.async_payment_failed"
                ? new GatewayResult(PaymentAttemptStatus.Failed, null, "Payment failed at Stripe")
                : StripePaymentGateway.FromSession(session);
            try { await payments.ApplyAsync(payment, result, "stripe-webhook", ct); }
            catch (PaymentException ex)
            {
                log.LogError("Stripe webhook {Type} for payment {Payment} could not be applied: {Error}", type, payment.Id, ex.Message);
                return Results.Ok(); // retrying would not help; it is logged and audited for follow-up
            }
            log.LogInformation("Stripe webhook {Type}: payment {Payment} is {Status}", type, payment.Id, payment.Status);
            return Results.Ok();
        }).AllowAnonymous().ExcludeFromDescription();
    }

    internal static IQueryable<PaymentDto> Project(IQueryable<Payment> q) =>
        q.Select(p => new PaymentDto(p.Id, p.PurchaseOrderId, p.PurchaseOrder!.Number, p.Provider.ToString(), p.Status.ToString(), p.IsTest,
            p.Amount, p.Currency, p.Status == PaymentAttemptStatus.Pending ? p.CheckoutUrl : null, p.PaymentReference, p.RefundReference,
            p.FailureReason, p.CreatedBy, p.CreatedAtUtc, p.CompletedAtUtc, p.RefundedAtUtc,
            p.PurchaseOrder.BuyerOrganization!.Name, p.PurchaseOrder.Supplier!.Code, p.PurchaseOrder.Supplier.Name));
}
