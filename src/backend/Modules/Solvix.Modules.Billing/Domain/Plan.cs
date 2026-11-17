namespace Solvix.Modules.Billing.Domain;

using Solvix.SharedKernel.Entities;

/// <summary>
/// A purchasable plan. Platform-level (not tenant-owned) and read-only for the application: plans are
/// maintained by operators through migrations/SQL, never through a tenant-facing API.
/// <see cref="DeviceLimit"/> null means "custom" (Enterprise): the limit is negotiated per subscription.
/// </summary>
public sealed class Plan : Entity
{
    private Plan()
    {
    }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public int? DeviceLimit { get; private set; }

    public long? MonthlyPriceMinor { get; private set; }

    public long? AnnualPriceMinor { get; private set; }

    public string Currency { get; private set; } = "USD";

    public int SortOrder { get; private set; }

    /// <summary>Shown in the public catalog and purchasable by self-service. Enterprise is catalog-only.</summary>
    public bool IsPublic { get; private set; }

    public bool IsActive { get; private set; } = true;

    public bool IsCustom => DeviceLimit is null;

    public long? PriceFor(BillingInterval interval) => interval == BillingInterval.Monthly ? MonthlyPriceMinor : AnnualPriceMinor;

    public static Plan Create(string code, string name, int? deviceLimit, long? monthlyPriceMinor, long? annualPriceMinor, string currency, int sortOrder, bool isPublic) =>
        new()
        {
            Code = code,
            Name = name,
            DeviceLimit = deviceLimit,
            MonthlyPriceMinor = monthlyPriceMinor,
            AnnualPriceMinor = annualPriceMinor,
            Currency = currency,
            SortOrder = sortOrder,
            IsPublic = isPublic,
        };
}
