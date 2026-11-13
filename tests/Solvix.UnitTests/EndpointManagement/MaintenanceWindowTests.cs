namespace Solvix.UnitTests.EndpointManagement;

using Solvix.Modules.EndpointManagement.Domain.MaintenanceWindows;
using Solvix.SharedKernel.Exceptions;

public sealed class MaintenanceWindowTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    private static DateTimeOffset Utc(int y, int m, int d, int h, int min = 0) => new(y, m, d, h, min, 0, TimeSpan.Zero);

    [Fact]
    public void Weekly_window_is_open_only_on_selected_days_and_hours()
    {
        // Saturdays 22:00-02:00 Berlin time (UTC+1 in January).
        var window = MaintenanceWindow.CreateWeekly(Tenant, "Weekend", "Europe/Berlin", WeekDays.Saturday, new TimeOnly(22, 0), 240);

        Assert.False(window.IsOpenAt(Utc(2026, 1, 10, 20, 59))); // Sat 21:59 local
        Assert.True(window.IsOpenAt(Utc(2026, 1, 10, 21, 0)));   // Sat 22:00 local
        Assert.True(window.IsOpenAt(Utc(2026, 1, 11, 0, 30)));   // Sun 01:30 local – runs past midnight
        Assert.False(window.IsOpenAt(Utc(2026, 1, 11, 1, 0)));   // Sun 02:00 local – closed
        Assert.False(window.IsOpenAt(Utc(2026, 1, 11, 21, 30))); // Sunday evening – not selected

        var occurrence = window.OccurrenceAt(Utc(2026, 1, 11, 0, 30))!.Value;
        Assert.Equal(Utc(2026, 1, 10, 21, 0), occurrence.StartUtc);
        Assert.Equal(Utc(2026, 1, 11, 1, 0), occurrence.EndUtc);
    }

    [Fact]
    public void Weekly_window_follows_daylight_saving_time()
    {
        var window = MaintenanceWindow.CreateWeekly(Tenant, "Nightly", "America/New_York", WeekDays.Everyday, new TimeOnly(1, 0), 60);

        // 01:00 EST = 06:00 UTC in winter, 01:00 EDT = 05:00 UTC in summer.
        Assert.True(window.IsOpenAt(Utc(2026, 1, 15, 6, 10)));
        Assert.False(window.IsOpenAt(Utc(2026, 1, 15, 5, 10)));
        Assert.True(window.IsOpenAt(Utc(2026, 7, 15, 5, 10)));
        Assert.False(window.IsOpenAt(Utc(2026, 7, 15, 6, 10)));
    }

    [Fact]
    public void Start_inside_a_spring_forward_gap_moves_to_the_end_of_the_gap()
    {
        // 2026-03-08 02:30 does not exist in New York (clocks jump 02:00 -> 03:00 EDT).
        var window = MaintenanceWindow.CreateWeekly(Tenant, "Gap", "America/New_York", WeekDays.Sunday, new TimeOnly(2, 30), 60);
        var occurrence = window.NextOccurrenceAfter(Utc(2026, 3, 7, 12))!.Value;
        Assert.Equal(Utc(2026, 3, 8, 7, 0), occurrence.StartUtc); // 03:00 EDT
    }

    [Fact]
    public void Next_occurrence_and_disabled_windows()
    {
        var window = MaintenanceWindow.CreateWeekly(Tenant, "Tue", "UTC", WeekDays.Tuesday, new TimeOnly(3, 0), 120);
        var next = window.NextOccurrenceAfter(Utc(2026, 1, 1, 0))!.Value; // Thursday
        Assert.Equal(Utc(2026, 1, 6, 3, 0), next.StartUtc);

        window.Disable();
        Assert.False(window.IsOpenAt(Utc(2026, 1, 6, 3, 30)));
        Assert.Null(window.NextOccurrenceAfter(Utc(2026, 1, 1, 0)));
    }

    [Fact]
    public void One_time_window()
    {
        var window = MaintenanceWindow.CreateOnce(Tenant, "Change 42", Utc(2026, 2, 1, 20), Utc(2026, 2, 1, 23));
        Assert.True(window.IsOpenAt(Utc(2026, 2, 1, 22)));
        Assert.False(window.IsOpenAt(Utc(2026, 2, 1, 23)));
        Assert.Null(window.NextOccurrenceAfter(Utc(2026, 2, 2, 0)));
    }

    [Fact]
    public void Invalid_definitions_are_rejected()
    {
        Assert.Throws<ValidationException>(() => MaintenanceWindow.CreateWeekly(Tenant, "x", "Not/AZone", WeekDays.Monday, new TimeOnly(1, 0), 60));
        Assert.Throws<ValidationException>(() => MaintenanceWindow.CreateWeekly(Tenant, "x", "UTC", WeekDays.None, new TimeOnly(1, 0), 60));
        Assert.Throws<ValidationException>(() => MaintenanceWindow.CreateWeekly(Tenant, "x", "UTC", WeekDays.Monday, new TimeOnly(1, 0), 5));
        Assert.Throws<ValidationException>(() => MaintenanceWindow.CreateOnce(Tenant, "x", Utc(2026, 1, 2, 0), Utc(2026, 1, 1, 0)));
    }
}
