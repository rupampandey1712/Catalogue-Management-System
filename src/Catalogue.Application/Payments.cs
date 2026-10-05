using Catalogue.Domain;
using Microsoft.EntityFrameworkCore;

namespace Catalogue.Application;

public sealed record CheckoutRequest(long PaymentId, string OrderNumber, string Description, decimal Amount, string Currency,
    string SuccessUrl, string CancelUrl, string? CustomerEmail);

/// <summary>Where the buyer goes next: a hosted checkout page, or null for the in-app simulated checkout.</summary>
public sealed record CheckoutStart(string Reference, string? RedirectUrl);

public sealed record GatewayResult(PaymentAttemptStatus Status, string? PaymentReference, string? FailureReason);

/// <summary>A payment provider (Stripe, the local simulator; Razorpay etc. would slot in here).</summary>
public interface IPaymentGateway
{
    PaymentProvider Provider { get; }
    string DisplayName { get; }
    /// <summary>True when no real money can move (Stripe test keys, the simulator).</summary>
    bool IsTestMode { get; }
    Task<CheckoutStart> StartAsync(CheckoutRequest request, CancellationToken ct);
    /// <summary>Asks the provider for the current state of a checkout (null when the provider cannot tell).</summary>
    Task<GatewayResult?> GetStatusAsync(string checkoutReference, CancellationToken ct);
    Task<string> RefundAsync(string paymentReference, decimal amount, string currency, long paymentId, CancellationToken ct);
    Task CancelAsync(string checkoutReference, CancellationToken ct);
}

public sealed class PaymentException(string message, int statusCode = 409) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Pays purchase orders through a gateway. Amounts always come from the stored order, never the client;
/// gateway confirmations (webhook or status sync) are applied idempotently; paid orders that are later
/// rejected or cancelled are refunded through the same gateway.
/// </summary>
public sealed class PaymentService(ICatalogueDbContext db, IEnumerable<IPaymentGateway> gateways, TimeProvider clock)
{
    /// <summary>A pending attempt younger than this is reused (double clicks, back button) instead of starting another.</summary>
    public static readonly TimeSpan ReuseWindow = TimeSpan.FromMinutes(30);

    public IReadOnlyList<IPaymentGateway> Gateways { get; } = gateways.ToList();

    public IPaymentGateway Gateway(PaymentProvider provider) =>
        Gateways.FirstOrDefault(g => g.Provider == provider)
        ?? throw new PaymentException($"{provider} payments are not enabled on this server.", 400);

    public async Task<(Payment Payment, CheckoutStart Checkout)> StartAsync(long orderId, long buyerOrganizationId, PaymentProvider provider,
        string user, string? email, string returnBaseUrl, CancellationToken ct)
    {
        var gateway = Gateway(provider);
        var order = await db.PurchaseOrders.Include(o => o.Supplier).FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw new PaymentException("Order not found.", 404);
        if (order.BuyerOrganizationId != buyerOrganizationId) throw new PaymentException("Order not found.", 404);
        if (order.PaymentStatus == OrderPaymentStatus.Paid) throw new PaymentException($"Order {order.Number} is already paid.");
        if (!order.CanBePaid) throw new PaymentException($"Order {order.Number} cannot be paid while it is {order.Status.ToString().ToLowerInvariant()}.");

        var now = clock.GetUtcNow().UtcDateTime;
        var pending = await db.Payments.Where(p => p.PurchaseOrderId == orderId && p.Status == PaymentAttemptStatus.Pending).ToListAsync(ct);
        var reusable = pending.FirstOrDefault(p => p.Provider == provider && p.Amount == order.Subtotal
            && now - p.CreatedAtUtc < ReuseWindow && p.CheckoutReference is not null);
        if (reusable is not null) return (reusable, new CheckoutStart(reusable.CheckoutReference!, reusable.CheckoutUrl));
        foreach (var stale in pending)
        {
            if (stale.CheckoutReference is { } r) await TryCancelAsync(stale.Provider, r, ct);
            stale.Cancel("Superseded by a new payment attempt.", now);
        }

        var payment = new Payment(order, provider, gateway.IsTestMode, user);
        order.MarkPaymentPending();
        db.Payments.Add(payment);
        await db.SaveChangesAsync(ct); // need the id for the gateway's idempotency key and metadata

        var baseUrl = returnBaseUrl.TrimEnd('/');
        try
        {
            var checkout = await gateway.StartAsync(new CheckoutRequest(payment.Id, order.Number,
                $"Purchase order {order.Number} - {order.Supplier?.Name}", payment.Amount, payment.Currency,
                $"{baseUrl}/orders?payment={payment.Id}&result=success", $"{baseUrl}/orders?payment={payment.Id}&result=cancelled", email), ct);
            payment.AttachCheckout(checkout.Reference, checkout.RedirectUrl);
            db.AuditEvents.Add(new AuditEvent(user, "PaymentStarted", "PurchaseOrder", order.Number,
                $"{provider}{(gateway.IsTestMode ? " (test)" : "")} {payment.Amount:0.00} {payment.Currency}, payment {payment.Id}", null));
            await db.SaveChangesAsync(ct);
            return (payment, checkout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            payment.Fail($"Could not start checkout: {ex.Message}", now);
            order.MarkPaymentAbandoned();
            await db.SaveChangesAsync(CancellationToken.None);
            throw new PaymentException($"{gateway.DisplayName} is unavailable: {ex.Message}", 502);
        }
    }

    /// <summary>Applies a gateway outcome (webhook, status sync or the simulator). Safe to call repeatedly.</summary>
    public async Task<Payment> ApplyAsync(Payment payment, GatewayResult result, string actor, CancellationToken ct)
    {
        var order = payment.PurchaseOrder ?? await db.PurchaseOrders.FirstAsync(o => o.Id == payment.PurchaseOrderId, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var changed = result.Status switch
        {
            PaymentAttemptStatus.Succeeded => payment.Succeed(result.PaymentReference, now),
            PaymentAttemptStatus.Failed => payment.Fail(result.FailureReason ?? "Declined", now),
            PaymentAttemptStatus.Cancelled => payment.Cancel(result.FailureReason ?? "Checkout abandoned", now),
            _ => false
        };
        if (!changed) return payment;

        if (payment.Status == PaymentAttemptStatus.Succeeded)
        {
            order.MarkPaid(now);
            // The order was cancelled/rejected while the buyer was on the checkout page: give the money back.
            if (order.Status is PurchaseOrderStatus.Cancelled or PurchaseOrderStatus.Rejected)
                await RefundAsync(payment, order, "Order closed before payment completed", actor, ct);
        }
        else if (!await db.Payments.AnyAsync(p => p.PurchaseOrderId == order.Id && p.Id != payment.Id && p.Status == PaymentAttemptStatus.Pending, ct))
            order.MarkPaymentAbandoned();

        db.AuditEvents.Add(new AuditEvent(actor, $"Payment{payment.Status}", "PurchaseOrder", order.Number,
            $"{payment.Provider} payment {payment.Id} {payment.Amount:0.00} {payment.Currency}{(payment.FailureReason is { } r ? ": " + r : "")}", null));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) when (payment.Status == PaymentAttemptStatus.Succeeded)
        {
            // IX_Payments_OneSuccessPerOrder: another attempt for this order already succeeded. Refund this one.
            throw new PaymentException($"Order {order.Number} was already paid by another payment; refund payment {payment.Id} at the provider.", 409);
        }
        return payment;
    }

    /// <summary>Pulls the latest state from the provider (used when the buyer returns from checkout, so it works without webhooks).</summary>
    public async Task<Payment> SyncAsync(Payment payment, string actor, CancellationToken ct)
    {
        if (payment.Status != PaymentAttemptStatus.Pending || payment.CheckoutReference is null) return payment;
        var result = await Gateway(payment.Provider).GetStatusAsync(payment.CheckoutReference, ct);
        return result is null ? payment : await ApplyAsync(payment, result, actor, ct);
    }

    /// <summary>
    /// Called when an order is rejected or cancelled: refunds a successful payment and cancels pending ones.
    /// Throws (and the caller does not save the status change) if the provider refuses the refund.
    /// </summary>
    public async Task SettleClosedOrderAsync(PurchaseOrder order, string actor, CancellationToken ct)
    {
        var payments = await db.Payments.Where(p => p.PurchaseOrderId == order.Id
            && (p.Status == PaymentAttemptStatus.Pending || p.Status == PaymentAttemptStatus.Succeeded)).ToListAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var p in payments)
        {
            if (p.Status == PaymentAttemptStatus.Succeeded) await RefundAsync(p, order, $"Order {order.Status.ToString().ToLowerInvariant()}", actor, ct);
            else
            {
                if (p.CheckoutReference is { } r) await TryCancelAsync(p.Provider, r, ct);
                p.Cancel($"Order {order.Status.ToString().ToLowerInvariant()}", now);
            }
        }
        if (order.PaymentStatus == OrderPaymentStatus.Pending) order.MarkPaymentAbandoned();
    }

    private async Task RefundAsync(Payment payment, PurchaseOrder order, string reason, string actor, CancellationToken ct)
    {
        if (payment.PaymentReference is null) throw new PaymentException($"Payment {payment.Id} has no provider reference to refund.", 500);
        string refund;
        try { refund = await Gateway(payment.Provider).RefundAsync(payment.PaymentReference, payment.Amount, payment.Currency, payment.Id, ct); }
        catch (Exception ex) when (ex is not (OperationCanceledException or PaymentException))
        {
            throw new PaymentException($"The refund for order {order.Number} failed at {payment.Provider}: {ex.Message}. The order was not changed.", 502);
        }
        payment.Refunded(refund, clock.GetUtcNow().UtcDateTime);
        order.MarkRefunded();
        db.AuditEvents.Add(new AuditEvent(actor, "PaymentRefunded", "PurchaseOrder", order.Number,
            $"{payment.Provider} payment {payment.Id} refunded {payment.Amount:0.00} {payment.Currency} ({refund}): {reason}", null));
    }

    private async Task TryCancelAsync(PaymentProvider provider, string reference, CancellationToken ct)
    {
        try { await Gateway(provider).CancelAsync(reference, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best effort: an expired or completed checkout cannot be cancelled */ }
    }
}

/// <summary>
/// Moves a purchase order through its status machine (used by the order endpoints and by approved agent actions):
/// rejecting or cancelling refunds online payments first, and nothing is saved if the refund fails.
/// </summary>
public sealed class OrderStatusService(ICatalogueDbContext db, PaymentService payments)
{
    /// <exception cref="InvalidOperationException">The transition is not allowed from the current status.</exception>
    /// <exception cref="PaymentException">A refund was needed and the provider refused it.</exception>
    public async Task ChangeAsync(PurchaseOrder order, PurchaseOrderStatus next, string? note, string actorId, string? correlationId, CancellationToken ct)
    {
        order.TransitionTo(next, note);
        if (next is PurchaseOrderStatus.Rejected or PurchaseOrderStatus.Cancelled)
            await payments.SettleClosedOrderAsync(order, actorId, ct);
        db.AuditEvents.Add(new AuditEvent(actorId, $"PurchaseOrder{next}", "PurchaseOrder", order.Number, note, correlationId));
        await db.SaveChangesAsync(ct);
    }
}
