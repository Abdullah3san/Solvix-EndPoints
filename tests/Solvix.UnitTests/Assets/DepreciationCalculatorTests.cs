namespace Solvix.UnitTests.Assets;

using Solvix.Modules.Assets.Domain;

public sealed class DepreciationCalculatorTests
{
    private static readonly DateOnly Start = new(2024, 1, 15);

    [Fact]
    public void Straight_line_charges_equal_monthly_amounts_down_to_salvage()
    {
        var mid = DepreciationCalculator.Calculate(DepreciationMethod.StraightLine, 1200m, 120m, 36, Start, new DateOnly(2025, 1, 15));
        Assert.Equal(12, mid.MonthsElapsed);
        Assert.Equal(360m, mid.AccumulatedDepreciation);
        Assert.Equal(840m, mid.BookValue);
        Assert.Equal(30m, mid.CurrentMonthlyCharge);

        var end = DepreciationCalculator.Calculate(DepreciationMethod.StraightLine, 1200m, 120m, 36, Start, new DateOnly(2040, 1, 1));
        Assert.Equal(36, end.MonthsElapsed);
        Assert.Equal(120m, end.BookValue);
        Assert.Equal(0m, end.CurrentMonthlyCharge);
        Assert.Equal(new DateOnly(2027, 1, 15), end.FullyDepreciatedOn);
    }

    [Fact]
    public void No_charge_before_the_first_full_month()
    {
        var result = DepreciationCalculator.Calculate(DepreciationMethod.StraightLine, 1000m, 0m, 10, Start, new DateOnly(2024, 2, 14));
        Assert.Equal(0, result.MonthsElapsed);
        Assert.Equal(1000m, result.BookValue);
    }

    [Fact]
    public void Rounding_is_absorbed_by_the_final_month()
    {
        var result = DepreciationCalculator.Calculate(DepreciationMethod.StraightLine, 1000m, 0m, 3, Start, new DateOnly(2030, 1, 1));
        Assert.Equal(0m, result.BookValue);
        Assert.Equal(1000m, result.Schedule.Sum(y => y.Depreciation));
    }

    [Fact]
    public void Declining_balance_front_loads_and_never_goes_below_salvage()
    {
        var oneYear = DepreciationCalculator.Calculate(DepreciationMethod.DecliningBalance, 6000m, 500m, 60, Start, new DateOnly(2025, 1, 15));
        var straight = DepreciationCalculator.Calculate(DepreciationMethod.StraightLine, 6000m, 500m, 60, Start, new DateOnly(2025, 1, 15));
        Assert.True(oneYear.AccumulatedDepreciation > straight.AccumulatedDepreciation);

        var end = DepreciationCalculator.Calculate(DepreciationMethod.DecliningBalance, 6000m, 500m, 60, Start, new DateOnly(2040, 1, 1));
        Assert.Equal(500m, end.BookValue);
        Assert.Equal(5500m, end.AccumulatedDepreciation);
    }

    [Fact]
    public void Yearly_schedule_chains_opening_and_closing_values()
    {
        var result = DepreciationCalculator.Calculate(DepreciationMethod.StraightLine, 1200m, 0m, 24, Start, Start);
        Assert.Equal([2024, 2025, 2026], result.Schedule.Select(y => y.Year).ToArray());
        Assert.Equal(1200m, result.Schedule[0].OpeningValue);
        for (var i = 1; i < result.Schedule.Count; i++)
        {
            Assert.Equal(result.Schedule[i - 1].ClosingValue, result.Schedule[i].OpeningValue);
        }

        Assert.Equal(0m, result.Schedule[^1].ClosingValue);
    }
}
