namespace Solvix.Modules.Billing.Domain;

using Solvix.SharedKernel.Billing;

/// <summary>
/// Pure, time-based evaluation of what a subscription allows. It deliberately never relies on a background
/// job having run: a subscription is judged by its timestamps at the moment of the request.
/// </summary>
public static class EntitlementEvaluator
{
    public static SubscriptionStatus EffectiveStatus(Subscription? subscription, DateTimeOffset now, TimeSpan grace)
    {
        if (subscription is null)
        {
            return SubscriptionStatus.Expired;
        }

        switch (subscription.Status)
        {
            case SubscriptionStatus.Suspended:
            case SubscriptionStatus.Cancelled:
            case SubscriptionStatus.Expired:
                return subscription.Status;
            case SubscriptionStatus.Trialing:
                return now < (subscription.TrialEndsAtUtc ?? subscription.CurrentPeriodEndUtc)
                    ? SubscriptionStatus.Trialing
                    : SubscriptionStatus.Expired;
            default:
                if (now < subscription.CurrentPeriodEndUtc)
                {
                    return SubscriptionStatus.Active;
                }

                if (subscription.CancelAtPeriodEnd)
                {
                    return SubscriptionStatus.Cancelled;
                }

                return now < subscription.CurrentPeriodEndUtc + grace ? SubscriptionStatus.PastDue : SubscriptionStatus.Expired;
        }
    }

    public static DeviceEntitlement Evaluate(Subscription? subscription, DateTimeOffset now, TimeSpan grace)
    {
        var status = EffectiveStatus(subscription, now, grace);
        return status switch
        {
            SubscriptionStatus.Trialing or SubscriptionStatus.Active or SubscriptionStatus.PastDue =>
                new DeviceEntitlement(true, subscription!.DeviceLimit),
            SubscriptionStatus.Suspended =>
                DeviceEntitlement.None("subscription_suspended", "This workspace's subscription is suspended. Contact support."),
            SubscriptionStatus.Cancelled =>
                DeviceEntitlement.None("subscription_cancelled", "The subscription has been cancelled. Choose a plan to enroll more devices."),
            _ => subscription is null
                ? DeviceEntitlement.None("subscription_missing", "This workspace has no subscription. Choose a plan to enroll devices.")
                : DeviceEntitlement.None("subscription_expired", "The subscription has expired. Renew or choose a plan to enroll more devices."),
        };
    }
}
