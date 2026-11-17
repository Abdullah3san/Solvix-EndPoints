namespace Solvix.Modules.Billing.Payments;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Modules.Billing.Configuration;
using Solvix.SharedKernel.Exceptions;

public sealed record CheckoutRequest(Guid TenantId, Guid InvoiceId, string InvoiceNumber, string Description, long TotalMinor, string Currency, string? CustomerEmail);

public sealed record CheckoutSessionResult(string SessionId, string Url);

public enum ProviderEventKind
{
    Ignored = 0,
    PaymentSucceeded = 1,
    PaymentFailed = 2,
}

/// <summary>A verified, normalised provider event. Amounts are carried so the receiver can cross-check them against the invoice.</summary>
public sealed record ProviderEvent(
    string EventId,
    string EventType,
    ProviderEventKind Kind,
    Guid? InvoiceId,
    Guid? TenantId,
    long AmountMinor,
    string? Currency,
    string? Reference,
    string? FailureReason);

public sealed class WebhookSignatureException : Exception
{
    public WebhookSignatureException(string message)
        : base(message)
    {
    }

    public WebhookSignatureException()
    {
    }

    public WebhookSignatureException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Payment integration seam. Card data never touches this platform: checkout is hosted by the provider and
/// only identifiers and amounts come back, authenticated by a signature.
/// </summary>
public interface IPaymentProvider
{
    string Name { get; }

    bool SupportsCheckout { get; }

    Task<CheckoutSessionResult?> CreateCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken);

    /// <summary>Verifies the signature over the raw body and normalises the event. Throws <see cref="WebhookSignatureException"/> when it cannot be authenticated.</summary>
    ProviderEvent ParseWebhook(string payload, string? signatureHeader, DateTimeOffset now);
}

/// <summary>Invoices only: an operator confirms the (bank transfer) payment. No online checkout, no webhooks.</summary>
internal sealed class ManualPaymentProvider : IPaymentProvider
{
    public const string ProviderName = "manual";

    public string Name => ProviderName;

    public bool SupportsCheckout => false;

    public Task<CheckoutSessionResult?> CreateCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<CheckoutSessionResult?>(null);

    public ProviderEvent ParseWebhook(string payload, string? signatureHeader, DateTimeOffset now) =>
        throw new WebhookSignatureException("The manual provider does not accept webhooks.");
}

/// <summary>
/// Stripe Checkout (hosted page, payment mode) driven through the REST API; no SDK, no card data. Webhooks are
/// authenticated with the <c>Stripe-Signature</c> HMAC and a timestamp tolerance (replay protection).
/// </summary>
internal sealed partial class StripePaymentProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<BillingOptions> options,
    ILogger<StripePaymentProvider> logger) : IPaymentProvider
{
    public const string ProviderName = "stripe";
    public const string HttpClientName = "stripe";

    public string Name => ProviderName;

    public bool SupportsCheckout => true;

    public async Task<CheckoutSessionResult?> CreateCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = options.Value;
        var app = settings.PublicAppUrl.TrimEnd('/');
        var form = new List<KeyValuePair<string, string>>
        {
            new("mode", "payment"),
            new("client_reference_id", request.InvoiceId.ToString("D")),
            new("success_url", $"{app}/settings/billing?checkout=success"),
            new("cancel_url", $"{app}/settings/billing?checkout=cancelled"),
            new("expires_at", DateTimeOffset.UtcNow.AddMinutes(60).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            new("line_items[0][quantity]", "1"),
            new("line_items[0][price_data][currency]", request.Currency.ToLowerInvariant()),
            new("line_items[0][price_data][unit_amount]", request.TotalMinor.ToString(CultureInfo.InvariantCulture)),
            new("line_items[0][price_data][product_data][name]", request.Description),
            new("metadata[tenant_id]", request.TenantId.ToString("D")),
            new("metadata[invoice_id]", request.InvoiceId.ToString("D")),
            new("metadata[invoice_number]", request.InvoiceNumber),
        };
        if (!string.IsNullOrWhiteSpace(request.CustomerEmail))
        {
            form.Add(new("customer_email", request.CustomerEmail));
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/checkout/sessions") { Content = new FormUrlEncodedContent(form) };
        message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + settings.Stripe.SecretKey);
        message.Headers.TryAddWithoutValidation("Idempotency-Key", $"checkout-{request.InvoiceId:N}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 900}");

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(message, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // Never log the body: it can echo request fields. The status is enough to diagnose.
                LogProviderError(logger, (int)response.StatusCode);
                throw new ExternalServiceException("stripe", "The payment provider rejected the request. Try again later.");
            }

            using var json = JsonDocument.Parse(body);
            var id = json.RootElement.GetProperty("id").GetString();
            var url = json.RootElement.GetProperty("url").GetString();
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(url))
            {
                throw new ExternalServiceException("stripe", "The payment provider returned an unexpected response.");
            }

            return new CheckoutSessionResult(id, url);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException && !cancellationToken.IsCancellationRequested)
        {
            LogProviderFailure(logger, ex.GetType().Name);
            throw new ExternalServiceException("stripe", "The payment provider is unavailable. Try again later.");
        }
    }

    public ProviderEvent ParseWebhook(string payload, string? signatureHeader, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var secret = options.Value.Stripe.WebhookSecret;
        if (string.IsNullOrEmpty(secret))
        {
            throw new WebhookSignatureException("Webhook secret is not configured.");
        }

        StripeSignature.Verify(payload, signatureHeader, secret, now, TimeSpan.FromSeconds(options.Value.Stripe.WebhookToleranceSeconds));
        return StripeEventParser.Parse(payload);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Stripe returned HTTP {Status} when creating a checkout session")]
    private static partial void LogProviderError(ILogger logger, int status);

    [LoggerMessage(Level = LogLevel.Error, Message = "Stripe request failed ({ExceptionType})")]
    private static partial void LogProviderFailure(ILogger logger, string exceptionType);
}

/// <summary>Stripe webhook signature scheme: <c>t=&lt;unix&gt;,v1=&lt;hex hmac-sha256(secret, "t.payload")&gt;</c>.</summary>
public static class StripeSignature
{
    public static string Compute(string secret, long timestamp, string payload)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var data = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{timestamp}.{payload}"));
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, data));
    }

    public static string Header(string secret, DateTimeOffset at, string payload)
    {
        var timestamp = at.ToUnixTimeSeconds();
        return string.Create(CultureInfo.InvariantCulture, $"t={timestamp},v1={Compute(secret, timestamp, payload)}");
    }

    public static void Verify(string payload, string? header, string secret, DateTimeOffset now, TimeSpan tolerance)
    {
        if (string.IsNullOrWhiteSpace(header) || header.Length > 1024)
        {
            throw new WebhookSignatureException("Missing signature header.");
        }

        long? timestamp = null;
        var signatures = new List<byte[]>();
        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var name = part[..eq];
            var value = part[(eq + 1)..];
            if (name == "t" && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var t))
            {
                timestamp = t;
            }
            else if (name == "v1" && value.Length == 64)
            {
                try
                {
                    signatures.Add(Convert.FromHexString(value));
                }
                catch (FormatException)
                {
                    // ignore malformed candidate
                }
            }
        }

        if (timestamp is null || signatures.Count == 0)
        {
            throw new WebhookSignatureException("Malformed signature header.");
        }

        var age = now - DateTimeOffset.FromUnixTimeSeconds(timestamp.Value);
        if (age.Duration() > tolerance)
        {
            throw new WebhookSignatureException("Signature timestamp is outside the tolerance window.");
        }

        var expected = Convert.FromHexString(Compute(secret, timestamp.Value, payload));
        if (!signatures.Any(candidate => CryptographicOperations.FixedTimeEquals(candidate, expected)))
        {
            throw new WebhookSignatureException("Signature mismatch.");
        }
    }
}

internal static class StripeEventParser
{
    public static ProviderEvent Parse(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var id = root.GetProperty("id").GetString() ?? throw new WebhookSignatureException("Event has no id.");
            var type = root.GetProperty("type").GetString() ?? string.Empty;
            var obj = root.GetProperty("data").GetProperty("object");

            var kind = type switch
            {
                "checkout.session.completed" or "checkout.session.async_payment_succeeded" when PaymentStatus(obj) == "paid" => ProviderEventKind.PaymentSucceeded,
                "checkout.session.async_payment_failed" => ProviderEventKind.PaymentFailed,
                _ => ProviderEventKind.Ignored,
            };

            Guid? invoiceId = TryGuid(obj, "client_reference_id");
            Guid? tenantId = null;
            if (obj.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
            {
                tenantId = TryGuid(metadata, "tenant_id");
                invoiceId ??= TryGuid(metadata, "invoice_id");
            }

            var amount = obj.TryGetProperty("amount_total", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt64() : 0;
            var currency = obj.TryGetProperty("currency", out var c) ? c.GetString()?.ToUpperInvariant() : null;
            var reference = obj.TryGetProperty("id", out var sid) ? sid.GetString() : null;
            return new ProviderEvent(id, type, kind, invoiceId, tenantId, amount, currency, reference, kind == ProviderEventKind.PaymentFailed ? "async payment failed" : null);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new WebhookSignatureException("Malformed event payload.", ex);
        }
    }

    private static string? PaymentStatus(JsonElement obj) =>
        obj.TryGetProperty("payment_status", out var s) ? s.GetString() : null;

    private static Guid? TryGuid(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var guid) ? guid : null;
}
