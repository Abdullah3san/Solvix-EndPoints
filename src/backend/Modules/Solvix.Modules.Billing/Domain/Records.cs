namespace Solvix.Modules.Billing.Domain;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Guards;

/// <summary>A payment attempt result reported by the payment provider (or recorded by an operator).</summary>
public sealed class Payment : TenantEntity
{
    private Payment()
    {
    }

    public Guid InvoiceId { get; private set; }

    public string Provider { get; private set; } = string.Empty;

    /// <summary>Provider-side identifier (e.g. checkout session id). Globally unique per provider: replays cannot double-record.</summary>
    public string ProviderReference { get; private set; } = string.Empty;

    public long AmountMinor { get; private set; }

    public string Currency { get; private set; } = "USD";

    public PaymentStatus Status { get; private set; }

    public string? FailureReason { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public static Payment Record(Guid tenantId, Guid invoiceId, string provider, string providerReference, long amountMinor, string currency, PaymentStatus status, string? failureReason, DateTimeOffset now)
    {
        var payment = new Payment
        {
            InvoiceId = invoiceId,
            Provider = provider,
            ProviderReference = providerReference,
            AmountMinor = amountMinor,
            Currency = currency,
            Status = status,
            FailureReason = failureReason is null ? null : (failureReason.Length <= 300 ? failureReason : failureReason[..300]),
            OccurredAtUtc = now,
        };
        payment.AssignTenant(tenantId);
        return payment;
    }
}

/// <summary>
/// Company details printed on invoices. Billing data: only owners can read or change it.
/// </summary>
public sealed class BillingProfile : TenantEntity
{
    public const int TextMaxLength = 200;

    private BillingProfile()
    {
    }

    public string LegalName { get; private set; } = string.Empty;

    public string BillingEmail { get; private set; } = string.Empty;

    public string? AddressLine1 { get; private set; }

    public string? AddressLine2 { get; private set; }

    public string? City { get; private set; }

    public string? Region { get; private set; }

    public string? PostalCode { get; private set; }

    public string? CountryCode { get; private set; }

    public string? TaxId { get; private set; }

    public static BillingProfile Create(Guid tenantId)
    {
        var profile = new BillingProfile();
        profile.AssignTenant(tenantId);
        return profile;
    }

    public void Update(string legalName, string billingEmail, string? line1, string? line2, string? city, string? region, string? postalCode, string? countryCode, string? taxId)
    {
        LegalName = Guard.NotNullOrWhiteSpace(legalName, "legalName", TextMaxLength);
        BillingEmail = Guard.NotNullOrWhiteSpace(billingEmail, "billingEmail", 254);
        AddressLine1 = Guard.Optional(line1, "addressLine1", TextMaxLength);
        AddressLine2 = Guard.Optional(line2, "addressLine2", TextMaxLength);
        City = Guard.Optional(city, "city", 100);
        Region = Guard.Optional(region, "region", 100);
        PostalCode = Guard.Optional(postalCode, "postalCode", 20);
        CountryCode = Guard.Optional(countryCode, "countryCode", 2)?.ToUpperInvariant();
        TaxId = Guard.Optional(taxId, "taxId", 50);
    }

    public string? FormatAddress()
    {
        var parts = new[] { AddressLine1, AddressLine2, string.Join(' ', new[] { PostalCode, City }.Where(p => !string.IsNullOrWhiteSpace(p))), Region, CountryCode }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        var joined = string.Join('\n', parts);
        return joined.Length == 0 ? null : joined;
    }
}

/// <summary>Immutable audit trail of subscription changes; part of the billing history.</summary>
public sealed class SubscriptionEvent : TenantEntity
{
    private SubscriptionEvent()
    {
    }

    public SubscriptionEventType Type { get; private set; }

    public ActorKind Actor { get; private set; }

    public Guid? ActorUserId { get; private set; }

    public string? Detail { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public static SubscriptionEvent Create(Guid tenantId, SubscriptionEventType type, ActorKind actor, Guid? actorUserId, string? detail, DateTimeOffset now)
    {
        var e = new SubscriptionEvent
        {
            Type = type,
            Actor = actor,
            ActorUserId = actorUserId,
            Detail = detail is null ? null : (detail.Length <= 1000 ? detail : detail[..1000]),
            OccurredAtUtc = now,
        };
        e.AssignTenant(tenantId);
        return e;
    }
}

/// <summary>Per-day usage record: peak billable devices vs. the limit in force.</summary>
public sealed class UsageSnapshot : TenantEntity
{
    private UsageSnapshot()
    {
    }

    public DateOnly Day { get; private set; }

    public int PeakDevices { get; private set; }

    public int DeviceLimit { get; private set; }

    public DateTimeOffset RecordedAtUtc { get; private set; }

    public static UsageSnapshot Create(Guid tenantId, DateOnly day, int devices, int limit, DateTimeOffset now)
    {
        var s = new UsageSnapshot { Day = day, PeakDevices = devices, DeviceLimit = limit, RecordedAtUtc = now };
        s.AssignTenant(tenantId);
        return s;
    }

    public void Observe(int devices, int limit, DateTimeOffset now)
    {
        PeakDevices = Math.Max(PeakDevices, devices);
        DeviceLimit = limit;
        RecordedAtUtc = now;
    }
}

/// <summary>Per-tenant gap-free invoice number sequence (incremented atomically with raw SQL).</summary>
public sealed class InvoiceCounter : TenantEntity
{
    private InvoiceCounter()
    {
    }

    public long LastNumber { get; private set; }
}

/// <summary>
/// Processed payment-provider events, for idempotency. Platform table: an event arrives before any tenant
/// is known. Holds identifiers only, never payloads or customer data.
/// </summary>
public sealed class WebhookEvent : Entity
{
    private WebhookEvent()
    {
    }

    public string Provider { get; private set; } = string.Empty;

    public string EventId { get; private set; } = string.Empty;

    public string EventType { get; private set; } = string.Empty;

    public string Outcome { get; private set; } = string.Empty;

    public DateTimeOffset ReceivedAtUtc { get; private set; }

    public static WebhookEvent Create(string provider, string eventId, string eventType, string outcome, DateTimeOffset now) =>
        new() { Provider = provider, EventId = eventId, EventType = eventType, Outcome = outcome, ReceivedAtUtc = now };
}
