using System.Net;
using System.Text;
using System.Text.Json;
using Catalogue.Application;
using Catalogue.Domain;
using Catalogue.Infrastructure.Payments;
using Catalogue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Catalogue.UnitTests;

public class PaymentTests
{
    private static async Task<(CatalogueDbContext Db, PurchaseOrder Order)> OrderAsync(decimal amount = 556m)
    {
        var db = new CatalogueDbContext(new DbContextOptionsBuilder<CatalogueDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var supplier = new Supplier("NWD", "Northwest Distribution", IntegrationType.Edi);
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();
        var po = new PurchaseOrder("PO-1", 7, 1, supplier.Id, "GBP", "buyer", null, null);
        po.AddLine(new PurchaseOrderLine(1, 1, "S-1", "Boiler", 1, amount, amount, "GBP", ""));
        db.PurchaseOrders.Add(po);
        await db.SaveChangesAsync();
        return (db, po);
    }

    private static PaymentService Service(CatalogueDbContext db, params IPaymentGateway[] gateways) =>
        new(db, gateways.Length == 0 ? [new SimulatedPaymentGateway()] : gateways, TimeProvider.System);

    [Fact]
    public async Task Amount_comes_from_the_order_and_a_double_click_reuses_the_pending_attempt()
    {
        var (db, po) = await OrderAsync();
        var svc = Service(db);
        var (first, _) = await svc.StartAsync(po.Id, 7, PaymentProvider.Simulated, "buyer", null, "http://localhost:5173", default);
        var (second, _) = await svc.StartAsync(po.Id, 7, PaymentProvider.Simulated, "buyer", null, "http://localhost:5173", default);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(556m, first.Amount);
        Assert.True(first.IsTest);
        Assert.Equal(OrderPaymentStatus.Pending, po.PaymentStatus);
    }

    [Fact]
    public async Task Another_company_cannot_pay_or_see_the_order()
    {
        var (db, po) = await OrderAsync();
        var ex = await Assert.ThrowsAsync<PaymentException>(() =>
            Service(db).StartAsync(po.Id, buyerOrganizationId: 99, PaymentProvider.Simulated, "x", null, "http://localhost", default));
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task Decline_leaves_the_order_payable_and_success_is_idempotent()
    {
        var (db, po) = await OrderAsync();
        var svc = Service(db);
        var (p1, _) = await svc.StartAsync(po.Id, 7, PaymentProvider.Simulated, "buyer", null, "http://x", default);
        await svc.ApplyAsync(p1, new GatewayResult(PaymentAttemptStatus.Failed, null, "Declined"), "buyer", default);
        Assert.Equal(OrderPaymentStatus.Unpaid, po.PaymentStatus);
        Assert.True(po.CanBePaid);

        var (p2, _) = await svc.StartAsync(po.Id, 7, PaymentProvider.Simulated, "buyer", null, "http://x", default);
        Assert.NotEqual(p1.Id, p2.Id);
        var ok = new GatewayResult(PaymentAttemptStatus.Succeeded, "sim_pi_1", null);
        await svc.ApplyAsync(p2, ok, "buyer", default);
        await svc.ApplyAsync(p2, ok, "webhook", default); // repeated confirmation: no change, no error
        Assert.Equal(OrderPaymentStatus.Paid, po.PaymentStatus);
        Assert.Single(db.AuditEvents.Where(a => a.Action == "PaymentSucceeded"));
        var again = await Assert.ThrowsAsync<PaymentException>(() => svc.StartAsync(po.Id, 7, PaymentProvider.Simulated, "buyer", null, "http://x", default));
        Assert.Contains("already paid", again.Message);
    }

    [Fact]
    public async Task Rejecting_a_paid_order_refunds_it_and_closed_orders_cannot_be_paid()
    {
        var (db, po) = await OrderAsync();
        var svc = Service(db);
        var (p, _) = await svc.StartAsync(po.Id, 7, PaymentProvider.Simulated, "buyer", null, "http://x", default);
        await svc.ApplyAsync(p, new GatewayResult(PaymentAttemptStatus.Succeeded, "sim_pi_1", null), "buyer", default);

        po.TransitionTo(PurchaseOrderStatus.Rejected, "no stock");
        await svc.SettleClosedOrderAsync(po, "seller", default);
        await db.SaveChangesAsync();
        Assert.Equal(PaymentAttemptStatus.Refunded, p.Status);
        Assert.StartsWith("sim_re_", p.RefundReference);
        Assert.Equal(OrderPaymentStatus.Refunded, po.PaymentStatus);
        Assert.False(po.CanBePaid);
    }

    private sealed class FailingRefunds : IPaymentGateway
    {
        public PaymentProvider Provider => PaymentProvider.Stripe;
        public string DisplayName => "Broken";
        public bool IsTestMode => true;
        public Task<CheckoutStart> StartAsync(CheckoutRequest request, CancellationToken ct) => Task.FromResult(new CheckoutStart("cs_1", "https://checkout"));
        public Task<GatewayResult?> GetStatusAsync(string checkoutReference, CancellationToken ct) =>
            Task.FromResult<GatewayResult?>(new GatewayResult(PaymentAttemptStatus.Succeeded, "pi_1", null));
        public Task<string> RefundAsync(string paymentReference, decimal amount, string currency, long paymentId, CancellationToken ct) =>
            throw new HttpRequestException("Stripe 500");
        public Task CancelAsync(string checkoutReference, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task Hosted_checkout_syncs_from_the_provider_and_a_failed_refund_blocks_the_cancellation()
    {
        var (db, po) = await OrderAsync();
        var svc = Service(db, new FailingRefunds());
        var (p, checkout) = await svc.StartAsync(po.Id, 7, PaymentProvider.Stripe, "buyer", null, "http://x", default);
        Assert.Equal("https://checkout", checkout.RedirectUrl);
        await svc.SyncAsync(p, "buyer", default);
        Assert.Equal(PaymentAttemptStatus.Succeeded, p.Status);
        Assert.Equal("pi_1", p.PaymentReference);

        po.TransitionTo(PurchaseOrderStatus.Cancelled, null);
        var ex = await Assert.ThrowsAsync<PaymentException>(() => svc.SettleClosedOrderAsync(po, "buyer", default));
        Assert.Equal(502, ex.StatusCode);
        Assert.Equal(PaymentAttemptStatus.Succeeded, p.Status); // nothing pretended to be refunded
    }

    [Fact]
    public async Task Disabled_provider_is_refused()
    {
        var (db, po) = await OrderAsync();
        var ex = await Assert.ThrowsAsync<PaymentException>(() =>
            Service(db).StartAsync(po.Id, 7, PaymentProvider.Stripe, "buyer", null, "http://x", default));
        Assert.Equal(400, ex.StatusCode);
    }

    // ----- Stripe -----

    [Fact]
    public void Webhook_signature_is_verified_with_replay_window()
    {
        const string secret = "whsec_test_secret";
        var payload = """{"type":"checkout.session.completed"}""";
        var now = DateTimeOffset.UtcNow;
        var header = StripeWebhook.Sign(payload, secret, now);
        Assert.True(StripeWebhook.Verify(payload, header, secret, now));
        Assert.False(StripeWebhook.Verify(payload + " ", header, secret, now));            // body tampered
        Assert.False(StripeWebhook.Verify(payload, header, "whsec_other", now));            // wrong secret
        Assert.False(StripeWebhook.Verify(payload, header, secret, now.AddMinutes(10)));    // replayed later
        Assert.False(StripeWebhook.Verify(payload, null, secret, now));
    }

    [Theory]
    [InlineData("complete", "paid", PaymentAttemptStatus.Succeeded)]
    [InlineData("complete", "unpaid", PaymentAttemptStatus.Pending)]   // async payment method still processing
    [InlineData("open", "unpaid", PaymentAttemptStatus.Pending)]
    [InlineData("expired", "unpaid", PaymentAttemptStatus.Cancelled)]
    public void Checkout_session_state_maps_to_payment_status(string status, string paid, PaymentAttemptStatus expected)
    {
        using var doc = JsonDocument.Parse($$"""{"id":"cs_1","status":"{{status}}","payment_status":"{{paid}}","payment_intent":"pi_9"}""");
        Assert.Equal(expected, StripePaymentGateway.FromSession(doc.RootElement).Status);
    }

    [Theory]
    [InlineData(556.00, "GBP", 55600)]
    [InlineData(19.995, "GBP", 2000)]
    [InlineData(1500, "JPY", 1500)]
    public void Amounts_are_sent_in_minor_units(decimal amount, string currency, long minor)
        => Assert.Equal(minor, StripePaymentGateway.MinorUnits(amount, currency));

    private sealed class Capture(string response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Stripe_checkout_is_created_server_side_with_idempotency_and_metadata()
    {
        var handler = new Capture("""{"id":"cs_test_1","url":"https://checkout.stripe.com/c/pay/cs_test_1"}""");
        var gateway = new StripePaymentGateway(new HttpClient(handler) { BaseAddress = new Uri("https://api.stripe.com/") },
            new PaymentOptions { StripeSecretKey = "sk_test_123" });
        var start = await gateway.StartAsync(new CheckoutRequest(42, "PO-1", "Purchase order PO-1", 556m, "GBP",
            "http://localhost:5173/orders?payment=42&result=success", "http://localhost:5173/orders?payment=42&result=cancelled", null), default);

        Assert.Equal("cs_test_1", start.Reference);
        Assert.True(gateway.IsTestMode);
        Assert.Equal("Bearer sk_test_123", handler.Request!.Headers.Authorization!.ToString());
        Assert.Equal("catalogue-checkout-42", handler.Request.Headers.GetValues("Idempotency-Key").Single());
        var form = System.Web.HttpUtility.ParseQueryString(handler.Body!);
        Assert.Equal("55600", form["line_items[0][price_data][unit_amount]"]);
        Assert.Equal("gbp", form["line_items[0][price_data][currency]"]);
        Assert.Equal("42", form["metadata[payment_id]"]);
    }

    [Theory]
    [InlineData("sk_live_abc", false, true)]
    [InlineData("sk_live_abc", true, false)]
    [InlineData("pk_test_abc", false, true)]
    [InlineData("sk_test_abc", false, false)]
    public void Live_and_publishable_keys_are_refused_by_default(string key, bool allowLive, bool throws)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Payments:Stripe:SecretKey"] = key, ["Payments:Stripe:AllowLiveKeys"] = allowLive.ToString()
        }).Build();
        if (throws) Assert.Throws<InvalidOperationException>(() => PaymentOptions.From(config));
        else Assert.True(PaymentOptions.From(config).StripeEnabled);
    }
}
