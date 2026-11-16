namespace Solvix.UnitTests.Phase12;

using Solvix.Modules.Helpdesk.Domain;

public sealed class SlaCalendarTests
{
    private static readonly SlaCalendar Business = SlaCalendar.Business("UTC", 9 * 60, 17 * 60, 0b0111110); // Mon-Fri 09-17

    private static DateTimeOffset Utc(int y, int m, int d, int h = 0, int min = 0) => new(y, m, d, h, min, 0, TimeSpan.Zero);

    [Fact]
    public void Always_on_adds_wall_clock_minutes()
    {
        Assert.Equal(Utc(2026, 11, 16, 12, 30), SlaCalendar.AlwaysOn.Add(Utc(2026, 11, 16, 10, 0), 150));
        Assert.Equal(150, SlaCalendar.AlwaysOn.Between(Utc(2026, 11, 16, 10, 0), Utc(2026, 11, 16, 12, 30)));
    }

    [Fact]
    public void Business_hours_stay_inside_the_working_day()
    {
        // Monday 2026-11-16 10:00 + 2h = 12:00 same day.
        Assert.Equal(Utc(2026, 11, 16, 12, 0), Business.Add(Utc(2026, 11, 16, 10, 0), 120));
    }

    [Fact]
    public void Business_hours_roll_over_to_the_next_working_day()
    {
        // Monday 16:00 + 2h: 1h left today, 1h tomorrow starting 09:00 -> Tuesday 10:00.
        Assert.Equal(Utc(2026, 11, 17, 10, 0), Business.Add(Utc(2026, 11, 16, 16, 0), 120));
    }

    [Fact]
    public void Business_hours_skip_the_weekend()
    {
        // Friday 2026-11-20 16:00 + 2h -> Monday 10:00.
        Assert.Equal(Utc(2026, 11, 23, 10, 0), Business.Add(Utc(2026, 11, 20, 16, 0), 120));
    }

    [Fact]
    public void Starting_outside_working_hours_waits_for_opening()
    {
        // Saturday 11:00 + 30 min -> Monday 09:30.
        Assert.Equal(Utc(2026, 11, 23, 9, 30), Business.Add(Utc(2026, 11, 21, 11, 0), 30));
        // Monday 06:00 + 30 min -> Monday 09:30.
        Assert.Equal(Utc(2026, 11, 16, 9, 30), Business.Add(Utc(2026, 11, 16, 6, 0), 30));
    }

    [Fact]
    public void Between_counts_only_working_minutes()
    {
        // Friday 16:00 -> Monday 10:00 = 60 (Fri) + 60 (Mon).
        Assert.Equal(120, Business.Between(Utc(2026, 11, 20, 16, 0), Utc(2026, 11, 23, 10, 0)));
        Assert.Equal(0, Business.Between(Utc(2026, 11, 21, 8, 0), Utc(2026, 11, 22, 20, 0)));
        Assert.Equal(0, Business.Between(Utc(2026, 11, 16, 12, 0), Utc(2026, 11, 16, 10, 0)));
    }

    [Fact]
    public void Add_and_Between_are_inverse_for_working_time()
    {
        var start = Utc(2026, 11, 18, 14, 17);
        foreach (var minutes in new[] { 1, 59, 480, 481, 1000, 4000 })
        {
            Assert.Equal(minutes, Business.Between(start, Business.Add(start, minutes)));
        }
    }

    [Fact]
    public void Time_zones_are_respected()
    {
        var tokyo = SlaCalendar.Business("Asia/Tokyo", 9 * 60, 17 * 60, 0b0111110);
        // 2026-11-16 00:00Z is 09:00 JST Monday: +60 min -> 10:00 JST = 01:00Z.
        Assert.Equal(Utc(2026, 11, 16, 1, 0), tokyo.Add(Utc(2026, 11, 16, 0, 0), 60));
    }

    [Theory]
    [InlineData(600, 600, 0b0111110)]
    [InlineData(600, 500, 0b0111110)]
    [InlineData(0, 1441, 0b0111110)]
    [InlineData(540, 1020, 0)]
    public void Invalid_calendars_are_rejected(int start, int end, int mask) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SlaCalendar.Business("UTC", start, end, (byte)mask));
}
