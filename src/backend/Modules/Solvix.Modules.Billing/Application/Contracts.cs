namespace Solvix.Modules.Billing.Application;

using Solvix.Modules.Billing.Domain;

public sealed record PlanDto(Guid Id, string Code, string Name, string? Description, int? DeviceLimit, long? MonthlyPriceMinor, long? AnnualPriceMinor, string Currency, bool IsCustom);

public sealed record SubscriptionDto(
    string PlanCode,
    string PlanName,
    SubscriptionStatus Status,
    BillingInterval Interval,
    int DeviceLimit,
    long UnitPriceMinor,
    string Currency,
    DateTimeOffset? TrialEndsAtUtc,
    DateTimeOffset CurrentPeriodStartUtc,
    DateTimeOffset CurrentPeriodEndUtc,
    DateTimeOffset? GraceEndsAtUtc,
    bool CancelAtPeriodEnd,
    string? PendingPlanCode,
    BillingInterval? PendingInterval,
    bool CanEnrollDevices,
    string? RestrictionCode,
    string? RestrictionMessage);

public sealed record UsagePointDto(DateOnly Day, int PeakDevices, int DeviceLimit);

public sealed record UsageDto(int DevicesUsed, int DeviceLimit, int PercentUsed, string Level, bool CanEnrollDevices, IReadOnlyList<UsagePointDto> History);

public sealed record ChangePlanRequest(string? PlanCode, BillingInterval Interval);

public sealed record InvoiceLineDto(string Description, int Quantity, long UnitAmountMinor, long AmountMinor);

public sealed record InvoiceDto(
    Guid Id,
    string Number,
    InvoiceStatus Status,
    InvoicePurpose Purpose,
    string Currency,
    long SubtotalMinor,
    long TaxMinor,
    long TotalMinor,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset DueAtUtc,
    DateTimeOffset? PaidAtUtc,
    DateTimeOffset PeriodStartUtc,
    DateTimeOffset PeriodEndUtc,
    string PlanCode,
    BillingInterval Interval);

public sealed record InvoiceDetailsDto(InvoiceDto Invoice, IReadOnlyList<InvoiceLineDto> Lines, string? BillToName, string? BillToEmail, string? BillToAddress, string? BillToTaxId, int TaxRateBasisPoints);

/// <param name="Outcome"><c>invoice_issued</c> (pay it), <c>scheduled</c> (applies at period end) or <c>unchanged</c>.</param>
public sealed record ChangePlanResult(string Outcome, InvoiceDto? Invoice, string? CheckoutUrl, SubscriptionDto Subscription);

public sealed record CheckoutDto(InvoiceDto Invoice, string? CheckoutUrl, string Provider);

public sealed record BillingProfileDto(
    string LegalName,
    string BillingEmail,
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? Region,
    string? PostalCode,
    string? CountryCode,
    string? TaxId);

public sealed record UpdateBillingProfileRequest(
    string? LegalName,
    string? BillingEmail,
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? Region,
    string? PostalCode,
    string? CountryCode,
    string? TaxId);

public sealed record BillingHistoryItemDto(string Kind, DateTimeOffset AtUtc, string Title, long? AmountMinor, string? Currency, string? Status, Guid? InvoiceId);

public sealed record InvoiceListQuery(int Page = 1, int PageSize = 25, InvoiceStatus? Status = null);

// Platform operator ---------------------------------------------------------------------------------------------

public sealed record OperatorConfigureSubscriptionRequest(string? PlanCode, BillingInterval Interval, int? DeviceLimit, long? UnitPriceMinor, DateTimeOffset? PeriodEndUtc);

public sealed record OperatorMarkPaidRequest(string? Reference);

public sealed record OperatorSuspendRequest(string? Reason);

public sealed record OperatorTaxRateRequest(int? TaxRateBasisPoints);
