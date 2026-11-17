namespace Solvix.Modules.EndpointManagement.Domain.MaintenanceWindows;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Guards;

[Flags]
public enum WeekDays
{
    None = 0,
    Sunday = 1 << DayOfWeek.Sunday,
    Monday = 1 << DayOfWeek.Monday,
    Tuesday = 1 << DayOfWeek.Tuesday,
    Wednesday = 1 << DayOfWeek.Wednesday,
    Thursday = 1 << DayOfWeek.Thursday,
    Friday = 1 << DayOfWeek.Friday,
    Saturday = 1 << DayOfWeek.Saturday,
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    Weekend = Saturday | Sunday,
    Everyday = Weekdays | Weekend,
}

public enum MaintenanceWindowRecurrence
{
    /// <summary>Opens on the selected weekdays at <see cref="MaintenanceWindow.StartTime"/> local time.</summary>
    Weekly = 1,

    /// <summary>A single, absolute period.</summary>
    Once = 2,
}

/// <summary>A concrete opening of a window.</summary>
public readonly record struct WindowOccurrence(DateTimeOffset StartUtc, DateTimeOffset EndUtc)
{
    public bool Contains(DateTimeOffset utc) => utc >= StartUtc && utc < EndUtc;
}

/// <summary>
/// A period during which disruptive work (installing updates, restarting,
/// applying configuration) is allowed. Weekly windows are evaluated in the
/// window's own IANA time zone, so "Saturday 22:00 for 4 h" follows daylight
/// saving changes and may run past midnight.
/// </summary>
public sealed class MaintenanceWindow : TenantEntity
{
    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 1000;
    public const int TimeZoneMaxLength = 64;
    public const int MinDurationMinutes = 15;
    public const int MaxDurationMinutes = 24 * 60;

    private MaintenanceWindow()
    {
    }

    private MaintenanceWindow(Guid tenantId, string name)
        : base(tenantId)
    {
        Name = name;
        IsEnabled = true;
    }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public string TimeZoneId { get; private set; } = "UTC";

    public MaintenanceWindowRecurrence Recurrence { get; private set; }

    public WeekDays DaysOfWeek { get; private set; }

    public TimeOnly StartTime { get; private set; }

    public int DurationMinutes { get; private set; }

    public DateTimeOffset? OnceStartUtc { get; private set; }

    public DateTimeOffset? OnceEndUtc { get; private set; }

    public bool IsEnabled { get; private set; }

    public static MaintenanceWindow CreateWeekly(Guid tenantId, string name, string timeZoneId, WeekDays days, TimeOnly start, int durationMinutes)
    {
        var window = new MaintenanceWindow(tenantId, Guard.NotNullOrWhiteSpace(name, "name", NameMaxLength));
        window.SetWeekly(timeZoneId, days, start, durationMinutes);
        return window;
    }

    public static MaintenanceWindow CreateOnce(Guid tenantId, string name, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        var window = new MaintenanceWindow(tenantId, Guard.NotNullOrWhiteSpace(name, "name", NameMaxLength));
        window.SetOnce(startUtc, endUtc);
        return window;
    }

    public void Rename(string name, string? description)
    {
        Name = Guard.NotNullOrWhiteSpace(name, "name", NameMaxLength);
        Description = Guard.Optional(description, "description", DescriptionMaxLength);
    }

    public void SetWeekly(string timeZoneId, WeekDays days, TimeOnly start, int durationMinutes)
    {
        var tz = Guard.NotNullOrWhiteSpace(timeZoneId, "timeZoneId", TimeZoneMaxLength);
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(tz, out _))
        {
            throw new ValidationException("timeZoneId", "Unknown time zone id.");
        }

        if ((days & WeekDays.Everyday) == WeekDays.None)
        {
            throw new ValidationException("daysOfWeek", "Select at least one day.");
        }

        if (durationMinutes is < MinDurationMinutes or > MaxDurationMinutes)
        {
            throw new ValidationException("durationMinutes", $"Duration must be between {MinDurationMinutes} and {MaxDurationMinutes} minutes.");
        }

        Recurrence = MaintenanceWindowRecurrence.Weekly;
        TimeZoneId = tz;
        DaysOfWeek = days & WeekDays.Everyday;
        StartTime = start;
        DurationMinutes = durationMinutes;
        OnceStartUtc = null;
        OnceEndUtc = null;
    }

    public void SetOnce(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        if (endUtc <= startUtc)
        {
            throw new ValidationException("endUtc", "The window must end after it starts.");
        }

        Recurrence = MaintenanceWindowRecurrence.Once;
        OnceStartUtc = startUtc.ToUniversalTime();
        OnceEndUtc = endUtc.ToUniversalTime();
        DaysOfWeek = WeekDays.None;
        DurationMinutes = (int)Math.Min(int.MaxValue, (endUtc - startUtc).TotalMinutes);
        TimeZoneId = "UTC";
    }

    public void Enable() => IsEnabled = true;

    public void Disable() => IsEnabled = false;

    /// <summary>The occurrence containing <paramref name="utc"/>, or null when the window is closed.</summary>
    public WindowOccurrence? OccurrenceAt(DateTimeOffset utc)
    {
        if (!IsEnabled)
        {
            return null;
        }

        if (Recurrence == MaintenanceWindowRecurrence.Once)
        {
            var once = new WindowOccurrence(OnceStartUtc!.Value, OnceEndUtc!.Value);
            return once.Contains(utc) ? once : null;
        }

        var tz = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, tz).DateTime);

        // Duration is at most 24 h, so only today's and yesterday's openings can contain utc.
        for (var offset = 0; offset >= -1; offset--)
        {
            if (Occurrence(tz, localDate.AddDays(offset)) is { } occurrence && occurrence.Contains(utc))
            {
                return occurrence;
            }
        }

        return null;
    }

    public bool IsOpenAt(DateTimeOffset utc) => OccurrenceAt(utc) is not null;

    /// <summary>The first occurrence that starts after <paramref name="utc"/> (null when there is none).</summary>
    public WindowOccurrence? NextOccurrenceAfter(DateTimeOffset utc)
    {
        if (!IsEnabled)
        {
            return null;
        }

        if (Recurrence == MaintenanceWindowRecurrence.Once)
        {
            return OnceStartUtc > utc ? new WindowOccurrence(OnceStartUtc.Value, OnceEndUtc!.Value) : null;
        }

        var tz = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, tz).DateTime);
        for (var offset = 0; offset <= 8; offset++)
        {
            if (Occurrence(tz, localDate.AddDays(offset)) is { } occurrence && occurrence.StartUtc > utc)
            {
                return occurrence;
            }
        }

        return null;
    }

    private WindowOccurrence? Occurrence(TimeZoneInfo tz, DateOnly localDate)
    {
        var day = (WeekDays)(1 << (int)localDate.DayOfWeek);
        if ((DaysOfWeek & day) == WeekDays.None)
        {
            return null;
        }

        var start = ToUtc(localDate.ToDateTime(StartTime), tz);
        return new WindowOccurrence(start, start.AddMinutes(DurationMinutes));
    }

    /// <summary>
    /// Local wall-clock time to UTC. A start inside a spring-forward gap moves to
    /// the end of the gap; an ambiguous fall-back time uses its first occurrence.
    /// </summary>
    internal static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo tz)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (tz.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddMinutes(15);
        }

        var offset = tz.IsAmbiguousTime(unspecified)
            ? tz.GetAmbiguousTimeOffsets(unspecified).Max()
            : tz.GetUtcOffset(unspecified);
        return new DateTimeOffset(unspecified, offset).ToUniversalTime();
    }
}
