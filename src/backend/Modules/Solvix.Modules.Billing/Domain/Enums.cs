namespace Solvix.Modules.Billing.Domain;

public enum BillingInterval
{
    Monthly = 1,
    Annual = 2,
}

/// <summary>
/// Persisted states are Trialing, Active, Cancelled, Expired and Suspended. <see cref="PastDue"/> is only ever
/// produced by <see cref="EntitlementEvaluator"/> (period elapsed, still inside the grace window).
/// </summary>
public enum SubscriptionStatus
{
    Trialing = 1,
    Active = 2,
    PastDue = 3,
    Cancelled = 4,
    Expired = 5,
    Suspended = 6,
}

public enum InvoiceStatus
{
    Open = 1,
    Paid = 2,
    Void = 3,
}

public enum InvoicePurpose
{
    /// <summary>First paid period, or re-subscribing after a lapse.</summary>
    NewSubscription = 1,

    /// <summary>The next period of a running subscription.</summary>
    Renewal = 2,

    /// <summary>Immediate upgrade to a plan with a higher device limit.</summary>
    PlanChange = 3,
}

public enum PaymentStatus
{
    Succeeded = 1,
    Failed = 2,
}

public enum SubscriptionEventType
{
    TrialStarted = 1,
    Activated = 2,
    Renewed = 3,
    PlanChanged = 4,
    PlanChangeScheduled = 5,
    PlanChangeDropped = 6,
    CancelRequested = 7,
    CancelReverted = 8,
    Cancelled = 9,
    Expired = 10,
    Suspended = 11,
    Reactivated = 12,
    EnterpriseConfigured = 13,
    InvoiceIssued = 14,
    PaymentFailed = 15,
}

public enum ActorKind
{
    User = 1,
    System = 2,
    Operator = 3,
    PaymentProvider = 4,
}
