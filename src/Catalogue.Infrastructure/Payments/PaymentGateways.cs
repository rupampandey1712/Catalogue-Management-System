using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Catalogue.Application;
using Catalogue.Domain;
using Microsoft.Extensions.Configuration;

namespace Catalogue.Infrastructure.Payments;

public sealed class PaymentOptions
{
    /// <summary>Base URL of the SPA that hosted checkouts return to (never taken from request headers).</summary>
    public string ReturnBaseUrl { get; init; } = "http://localhost:5173";
    public bool SimulatedEnabled { get; init; }
    public string? StripeSecretKey { get; init; }
    public string? StripeWebhookSecret { get; init; }
    public bool StripeAllowLiveKeys { get; init; }

    public bool StripeEnabled => !string.IsNullOrWhiteSpace(StripeSecretKey);

    public static PaymentOptions From(IConfiguration config)
    {
        var o = new PaymentOptions
        {
            ReturnBaseUrl = config["Payments:ReturnBaseUrl"] ?? "http://localhost:5173",
            SimulatedEnabled = config.GetValue("Payments:Simulated:Enabled", false),
            StripeSecretKey = config["Payments:Stripe:SecretKey"],
            StripeWebhookSecret = config["Payments:Stripe:WebhookSecret"],
            StripeAllowLiveKeys = config.GetValue("Payments:Stripe:AllowLiveKeys", false)
        };
        if (o.StripeEnabled)
        {
            var key = o.StripeSecretKey!;
            if (key.StartsWith("pk_", StringComparison.Ordinal))
                throw new InvalidOperationException("Payments:Stripe:SecretKey holds a publishable key (pk_...). Use the secret key (sk_test_...).");
            if (key.StartsWith("sk_live_", StringComparison.Ordinal) && !o.StripeAllowLiveKeys)
                throw new InvalidOperationException("A live Stripe key is configured. Use a test key (sk_test_...) or set Payments:Stripe:AllowLiveKeys=true deliberately.");
        }
        return o;
    }
}

/// <summary>
/// Local test gateway: no provider, no money. The buyer approves or declines in the app and only the
/// database changes. Enabled by configuration (on in Development only).
/// </summary>
public sealed class SimulatedPaymentGateway : IPaymentGateway
{
    public PaymentProvider Provider => PaymentProvider.Simulated;
    public string DisplayName => "Test payment (simulated)";
    public bool IsTestMode => true;

    public Task<CheckoutStart> StartAsync(CheckoutRequest request, CancellationToken ct) =>
        Task.FromResult(new CheckoutStart($"sim_cs_{Guid.NewGuid():N}", null));

    // The simulator has no state of its own: outcomes arrive through the simulate endpoint.
    public Task<GatewayResult?> GetStatusAsync(string checkoutReference, CancellationToken ct) => Task.FromResult<GatewayResult?>(null);

    public Task<string> RefundAsync(string paymentReference, decimal amount, string currency, long paymentId, CancellationToken ct) =>
        Task.FromResult($"sim_re_{Guid.NewGuid():N}");

    public Task CancelAsync(string checkoutReference, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Stripe Checkout over the REST API (form-encoded, no SDK). The buyer pays on Stripe's hosted page, so
/// card details never touch this application. Test keys (sk_test_...) use Stripe's free test mode.
/// </summary>
public sealed class StripePaymentGateway(HttpClient http, PaymentOptions options) : IPaymentGateway
{
    public const string HttpClientName = "stripe";

    public PaymentProvider Provider => PaymentProvider.Stripe;
    public string DisplayName => IsTestMode ? "Card via Stripe (test mode)" : "Card via Stripe";
    public bool IsTestMode => options.StripeSecretKey?.StartsWith("sk_test_", StringComparison.Ordinal) == true;

    public async Task<CheckoutStart> StartAsync(CheckoutRequest r, CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("mode", "payment"),
            new("line_items[0][quantity]", "1"),
            new("line_items[0][price_data][currency]", r.Currency.ToLowerInvariant()),
            new("line_items[0][price_data][unit_amount]", MinorUnits(r.Amount, r.Currency).ToString(CultureInfo.InvariantCulture)),
            new("line_items[0][price_data][product_data][name]", r.Description.Length > 250 ? r.Description[..250] : r.Description),
            new("success_url", r.SuccessUrl),
            new("cancel_url", r.CancelUrl),
            new("client_reference_id", r.PaymentId.ToString(CultureInfo.InvariantCulture)),
            new("metadata[payment_id]", r.PaymentId.ToString(CultureInfo.InvariantCulture)),
            new("metadata[order_number]", r.OrderNumber),
            new("payment_intent_data[metadata][payment_id]", r.PaymentId.ToString(CultureInfo.InvariantCulture)),
            new("payment_intent_data[metadata][order_number]", r.OrderNumber),
            new("expires_at", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
        };
        if (!string.IsNullOrWhiteSpace(r.CustomerEmail)) form.Add(new("customer_email", r.CustomerEmail));
        using var doc = await SendAsync(HttpMethod.Post, "v1/checkout/sessions", form, $"catalogue-checkout-{r.PaymentId}", ct);
        return new CheckoutStart(doc.RootElement.GetProperty("id").GetString()!, doc.RootElement.GetProperty("url").GetString());
    }

    public async Task<GatewayResult?> GetStatusAsync(string checkoutReference, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"v1/checkout/sessions/{Uri.EscapeDataString(checkoutReference)}", null, null, ct);
        return FromSession(doc.RootElement);
    }

    public async Task<string> RefundAsync(string paymentReference, decimal amount, string currency, long paymentId, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Post, "v1/refunds",
            [new("payment_intent", paymentReference), new("metadata[payment_id]", paymentId.ToString(CultureInfo.InvariantCulture))],
            $"catalogue-refund-{paymentId}", ct);
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    public async Task CancelAsync(string checkoutReference, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Post, $"v1/checkout/sessions/{Uri.EscapeDataString(checkoutReference)}/expire", [], null, ct);
    }

    /// <summary>Maps a Checkout Session object (from the API or a webhook) to a payment outcome.</summary>
    public static GatewayResult FromSession(JsonElement session)
    {
        var status = session.TryGetProperty("status", out var s) ? s.GetString() : null;
        var paid = session.TryGetProperty("payment_status", out var ps) ? ps.GetString() : null;
        var intent = session.TryGetProperty("payment_intent", out var pi) && pi.ValueKind == JsonValueKind.String ? pi.GetString() : null;
        return (status, paid) switch
        {
            ("complete", "paid") => new GatewayResult(PaymentAttemptStatus.Succeeded, intent, null),
            ("expired", _) => new GatewayResult(PaymentAttemptStatus.Cancelled, intent, "Checkout expired"),
            _ => new GatewayResult(PaymentAttemptStatus.Pending, intent, null) // open, or complete but async payment still processing
        };
    }

    /// <summary>Stripe amounts are integers in the currency's minor unit (pence for GBP).</summary>
    public static long MinorUnits(decimal amount, string currency) =>
        ZeroDecimal.Contains(currency.ToUpperInvariant()) ? (long)Math.Round(amount, 0, MidpointRounding.AwayFromZero)
            : (long)Math.Round(amount * 100, 0, MidpointRounding.AwayFromZero);

    private static readonly HashSet<string> ZeroDecimal = ["JPY", "KRW", "VND", "CLP", "PYG", "UGX", "XAF", "XOF", "BIF", "DJF", "GNF", "KMF", "MGA", "RWF", "VUV", "XPF"];

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, List<KeyValuePair<string, string>>? form, string? idempotencyKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.StripeSecretKey);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (form is not null) request.Content = new FormUrlEncodedContent(form);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var message = body;
            try { message = JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("message").GetString() ?? body; }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { /* not a Stripe error body */ }
            throw new HttpRequestException($"Stripe {(int)response.StatusCode}: {message}", null, response.StatusCode);
        }
        return JsonDocument.Parse(body);
    }
}

/// <summary>Verifies the Stripe-Signature header (HMAC-SHA256 of "timestamp.payload" with the endpoint secret).</summary>
public static class StripeWebhook
{
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    public static bool Verify(string payload, string? header, string secret, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(header) || string.IsNullOrEmpty(secret)) return false;
        long? timestamp = null;
        var signatures = new List<string>();
        foreach (var part in header.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0] == "t" && long.TryParse(kv[1], NumberStyles.None, CultureInfo.InvariantCulture, out var t)) timestamp = t;
            else if (kv[0] == "v1") signatures.Add(kv[1]);
        }
        if (timestamp is null || signatures.Count == 0) return false;
        if ((now - DateTimeOffset.FromUnixTimeSeconds(timestamp.Value)).Duration() > Tolerance) return false; // replay protection
        var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"{timestamp.Value.ToString(CultureInfo.InvariantCulture)}.{payload}"))).ToLowerInvariant();
        return signatures.Any(s => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(s), Encoding.ASCII.GetBytes(expected)));
    }

    /// <summary>Builds a header the way Stripe does (tests and local tooling).</summary>
    public static string Sign(string payload, string secret, DateTimeOffset at)
    {
        var t = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var sig = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{payload}"))).ToLowerInvariant();
        return $"t={t},v1={sig}";
    }
}
