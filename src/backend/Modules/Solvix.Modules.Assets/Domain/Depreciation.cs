namespace Solvix.Modules.Assets.Domain;

public sealed record DepreciationYear(int Year, decimal OpeningValue, decimal Depreciation, decimal ClosingValue);

public sealed record DepreciationResult(
    DepreciationMethod Method,
    decimal Cost,
    decimal SalvageValue,
    int UsefulLifeMonths,
    DateOnly StartDate,
    DateOnly AsOf,
    int MonthsElapsed,
    decimal AccumulatedDepreciation,
    decimal BookValue,
    decimal CurrentMonthlyCharge,
    DateOnly FullyDepreciatedOn,
    IReadOnlyList<DepreciationYear> Schedule);

/// <summary>
/// Monthly depreciation, charged at the end of each full month after the start
/// date (the purchase date). Amounts are rounded to cents per month; the final
/// month absorbs rounding so the book value lands exactly on the salvage value.
/// </summary>
public static class DepreciationCalculator
{
    public static DepreciationResult? Calculate(Asset asset, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.DepreciationMethod == DepreciationMethod.None || asset.PurchaseCost is not { } cost || asset.PurchaseDate is not { } start || asset.UsefulLifeMonths is not { } life)
        {
            return null;
        }

        // A disposed asset stops depreciating at disposal.
        var effectiveAsOf = asset.DisposedAtUtc is { } disposed && DateOnly.FromDateTime(disposed.UtcDateTime) < asOf
            ? DateOnly.FromDateTime(disposed.UtcDateTime)
            : asOf;
        return Calculate(asset.DepreciationMethod, cost, asset.SalvageValue ?? 0m, life, start, effectiveAsOf);
    }

    public static DepreciationResult Calculate(DepreciationMethod method, decimal cost, decimal salvage, int lifeMonths, DateOnly start, DateOnly asOf)
    {
        if (method == DepreciationMethod.None)
        {
            throw new ArgumentOutOfRangeException(nameof(method));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(lifeMonths, 1);
        salvage = Math.Clamp(salvage, 0m, cost);
        var monthly = Charges(method, cost, salvage, lifeMonths);

        var elapsed = 0;
        while (elapsed < lifeMonths && start.AddMonths(elapsed + 1) <= asOf)
        {
            elapsed++;
        }

        var accumulated = monthly.Take(elapsed).Sum();
        var schedule = monthly
            .Select((amount, index) => (Year: start.AddMonths(index + 1).Year, Amount: amount))
            .GroupBy(m => m.Year)
            .Select(g => (g.Key, Amount: g.Sum(x => x.Amount)))
            .Aggregate(
                new List<DepreciationYear>(),
                (rows, y) =>
                {
                    var opening = rows.Count == 0 ? cost : rows[^1].ClosingValue;
                    rows.Add(new DepreciationYear(y.Key, opening, y.Amount, opening - y.Amount));
                    return rows;
                });

        return new DepreciationResult(
            method,
            cost,
            salvage,
            lifeMonths,
            start,
            asOf,
            elapsed,
            accumulated,
            cost - accumulated,
            elapsed < lifeMonths ? monthly[elapsed] : 0m,
            start.AddMonths(lifeMonths),
            schedule);
    }

    private static List<decimal> Charges(DepreciationMethod method, decimal cost, decimal salvage, int life)
    {
        var charges = new List<decimal>(life);
        var depreciable = cost - salvage;
        var book = cost;
        var rate = 2m / life;
        for (var month = 1; month <= life; month++)
        {
            decimal charge;
            if (month == life)
            {
                charge = book - salvage;
            }
            else if (method == DepreciationMethod.StraightLine)
            {
                charge = decimal.Round(depreciable / life, 2, MidpointRounding.AwayFromZero);
            }
            else
            {
                // Double-declining balance, switching to straight-line over the remaining months when that is larger.
                var declining = book * rate;
                var straight = (book - salvage) / (life - month + 1);
                charge = decimal.Round(Math.Max(declining, straight), 2, MidpointRounding.AwayFromZero);
            }

            charge = Math.Clamp(charge, 0m, book - salvage);
            charges.Add(charge);
            book -= charge;
        }

        return charges;
    }
}
