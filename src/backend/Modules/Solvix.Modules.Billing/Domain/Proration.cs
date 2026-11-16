namespace Solvix.Modules.Billing.Domain;

/// <summary>
/// Upgrade pricing. An upgrade starts a new period at payment time, so the customer is credited for the
/// unused part of what they already paid for and charged the full price of the new terms.
/// </summary>
public static class Proration
{
    /// <summary>Value of the unused part of the current period, in minor units (rounded down: never over-credit).</summary>
    public static long UnusedCredit(long currentPriceMinor, DateTimeOffset periodStart, DateTimeOffset periodEnd, DateTimeOffset now)
    {
        if (currentPriceMinor <= 0 || now >= periodEnd)
        {
            return 0;
        }

        var total = (periodEnd - periodStart).TotalSeconds;
        if (total <= 0)
        {
            return 0;
        }

        var remaining = Math.Clamp((periodEnd - now).TotalSeconds, 0, total);
        return (long)Math.Floor(currentPriceMinor * (decimal)(remaining / total));
    }

    /// <summary>Amount to charge for an immediate upgrade (never negative).</summary>
    public static long UpgradeCharge(long newPriceMinor, long credit) => Math.Max(0, newPriceMinor - credit);
}
