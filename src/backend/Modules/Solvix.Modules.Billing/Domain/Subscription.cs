namespace Solvix.Modules.Billing.Domain;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Exceptions;

/// <summary>
/// The single subscription of a tenant (unique on tenant). Limits and price are snapshots taken when the
/// plan was applied, so editing the catalog never silently changes what an existing customer has bought.
/// </summary>
public sealed class Subscription : TenantEntity
{
    private Subscription()
    {
    }

    private Subscription(Guid tenantId)
        : base(tenantId)
    {
    }

    public Guid PlanId { get; private set; }

    public string PlanCode { get; private set; } = string.Empty;

    public SubscriptionStatus Status { get; private set; }

    public BillingInterval Interval { get; private set; }

    public int DeviceLimit { get; private set; }

    public long UnitPriceMinor { get; private set; }

    public string Currency { get; private set; } = "USD";

    public DateTimeOffset? TrialEndsAtUtc { get; private set; }

    public DateTimeOffset CurrentPeriodStartUtc { get; private set; }

    public DateTimeOffset CurrentPeriodEndUtc { get; private set; }

    public bool CancelAtPeriodEnd { get; private set; }

    public DateTimeOffset? CancelledAtUtc { get; private set; }

    public Guid? PendingPlanId { get; private set; }

    public string? PendingPlanCode { get; private set; }

    public BillingInterval? PendingInterval { get; private set; }

    public string? SuspensionReason { get; private set; }

    /// <summary>
    /// Operator-assigned tax rate (basis points); null means the platform default. Deliberately not editable
    /// by the tenant, otherwise a customer could zero its own tax.
    /// </summary>
    public int? TaxRateBasisPoints { get; private set; }

    public void SetTaxRate(int? basisPoints) => TaxRateBasisPoints = basisPoints;

    public void Reactivate()
    {
        Status = SubscriptionStatus.Active;
        SuspensionReason = null;
    }

    public static Subscription StartTrial(Guid tenantId, Plan plan, DateTimeOffset now, int trialDays)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.DeviceLimit is not { } limit)
        {
            throw new ValidationException("planCode", "A trial needs a plan with a fixed device limit.");
        }

        var ends = now.AddDays(trialDays);
        return new Subscription(tenantId)
        {
            PlanId = plan.Id,
            PlanCode = plan.Code,
            Status = SubscriptionStatus.Trialing,
            Interval = BillingInterval.Monthly,
            DeviceLimit = limit,
            UnitPriceMinor = 0,
            Currency = plan.Currency,
            TrialEndsAtUtc = ends,
            CurrentPeriodStartUtc = now,
            CurrentPeriodEndUtc = ends,
        };
    }

    public static DateTimeOffset AddInterval(DateTimeOffset start, BillingInterval interval) =>
        interval == BillingInterval.Monthly ? start.AddMonths(1) : start.AddYears(1);

    /// <summary>Starts a fresh paid period at <paramref name="start"/> with the given purchased terms.</summary>
    public void StartPaidPeriod(Guid planId, string planCode, BillingInterval interval, int deviceLimit, long unitPriceMinor, string currency, DateTimeOffset start)
    {
        PlanId = planId;
        PlanCode = planCode;
        Interval = interval;
        DeviceLimit = deviceLimit;
        UnitPriceMinor = unitPriceMinor;
        Currency = currency;
        Status = SubscriptionStatus.Active;
        TrialEndsAtUtc = null;
        CurrentPeriodStartUtc = start;
        CurrentPeriodEndUtc = AddInterval(start, interval);
        CancelAtPeriodEnd = false;
        CancelledAtUtc = null;
        SuspensionReason = null;
        ClearPending();
    }

    /// <summary>Continues the running subscription with the next period (contiguous, no gap).</summary>
    public void ExtendPeriod(Guid planId, string planCode, BillingInterval interval, int deviceLimit, long unitPriceMinor, string currency)
    {
        var start = CurrentPeriodEndUtc;
        PlanId = planId;
        PlanCode = planCode;
        Interval = interval;
        DeviceLimit = deviceLimit;
        UnitPriceMinor = unitPriceMinor;
        Currency = currency;
        Status = SubscriptionStatus.Active;
        CurrentPeriodStartUtc = start;
        CurrentPeriodEndUtc = AddInterval(start, interval);
        CancelAtPeriodEnd = false;
        ClearPending();
    }

    public void SchedulePlanChange(Guid planId, string planCode, BillingInterval interval)
    {
        PendingPlanId = planId;
        PendingPlanCode = planCode;
        PendingInterval = interval;
    }

    public void ClearPending()
    {
        PendingPlanId = null;
        PendingPlanCode = null;
        PendingInterval = null;
    }

    public void RequestCancellation()
    {
        if (Status is not (SubscriptionStatus.Active or SubscriptionStatus.Trialing))
        {
            throw new ConflictException("Only a running subscription can be cancelled.", "subscription_not_active");
        }

        CancelAtPeriodEnd = true;
        ClearPending();
    }

    public void RevertCancellation()
    {
        if (!CancelAtPeriodEnd)
        {
            throw new ConflictException("The subscription is not scheduled for cancellation.", "subscription_not_cancelling");
        }

        CancelAtPeriodEnd = false;
    }

    public void MarkCancelled(DateTimeOffset now)
    {
        Status = SubscriptionStatus.Cancelled;
        CancelledAtUtc = now;
        CancelAtPeriodEnd = false;
        ClearPending();
    }

    public void MarkExpired()
    {
        Status = SubscriptionStatus.Expired;
        ClearPending();
    }

    public void Suspend(string reason)
    {
        Status = SubscriptionStatus.Suspended;
        SuspensionReason = reason.Length <= 500 ? reason : reason[..500];
    }

    /// <summary>Operator-managed (Enterprise / negotiated) terms. Period end is set explicitly.</summary>
    public void ConfigureNegotiated(Plan plan, BillingInterval interval, int deviceLimit, long unitPriceMinor, DateTimeOffset periodStart, DateTimeOffset periodEnd)
    {
        ArgumentNullException.ThrowIfNull(plan);
        PlanId = plan.Id;
        PlanCode = plan.Code;
        Interval = interval;
        DeviceLimit = deviceLimit;
        UnitPriceMinor = unitPriceMinor;
        Currency = plan.Currency;
        Status = SubscriptionStatus.Active;
        TrialEndsAtUtc = null;
        CurrentPeriodStartUtc = periodStart;
        CurrentPeriodEndUtc = periodEnd;
        CancelAtPeriodEnd = false;
        CancelledAtUtc = null;
        SuspensionReason = null;
        ClearPending();
    }
}
