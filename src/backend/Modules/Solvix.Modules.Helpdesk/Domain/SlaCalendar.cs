namespace Solvix.Modules.Helpdesk.Domain;

/// <summary>
/// Calendar an SLA clock runs on: round the clock (24x7) or business hours in a time zone. Pure and deterministic
/// so every deadline can be unit tested.
/// </summary>
public sealed class SlaCalendar
{
    private readonly TimeZoneInfo _zone;
    private readonly int _startMinute;
    private readonly int _endMinute;
    private readonly byte _workDays;

    private SlaCalendar(bool businessHours, TimeZoneInfo zone, int startMinute, int endMinute, byte workDays)
    {
        IsBusinessHours = businessHours;
        _zone = zone;
        _startMinute = startMinute;
        _endMinute = endMinute;
        _workDays = workDays;
    }

    public bool IsBusinessHours { get; }

    public static SlaCalendar AlwaysOn { get; } = new(false, TimeZoneInfo.Utc, 0, 1440, 0x7F);

    /// <param name="workDaysMask">Bit 0 = Sunday ... bit 6 = Saturday.</param>
    public static SlaCalendar Business(string timeZoneId, int startMinute, int endMinute, byte workDaysMask)
    {
        if (startMinute < 0 || endMinute > 1440 || endMinute <= startMinute)
        {
            throw new ArgumentOutOfRangeException(nameof(endMinute), "Working hours must be a non-empty range within one day.");
        }

        if ((workDaysMask & 0x7F) == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workDaysMask), "At least one working day is required.");
        }

        return new SlaCalendar(true, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId), startMinute, endMinute, (byte)(workDaysMask & 0x7F));
    }

    /// <summary>The instant after <paramref name="minutes"/> working minutes counted from <paramref name="start"/>.</summary>
    public DateTimeOffset Add(DateTimeOffset start, int minutes) => Add(start, TimeSpan.FromMinutes(minutes));

    /// <summary>The instant after <paramref name="duration"/> of working time counted from <paramref name="start"/> (exact, no rounding).</summary>
    public DateTimeOffset Add(DateTimeOffset start, TimeSpan duration)
    {
        if (!IsBusinessHours)
        {
            return start + duration;
        }

        var local = TimeZoneInfo.ConvertTime(start, _zone).DateTime;
        double remaining = duration.TotalMinutes;
        // Bounded: at most one pass per day; 366*20 days covers any sane target.
        for (var guard = 0; guard < 8000; guard++)
        {
            if (IsWorkDay(local.DayOfWeek))
            {
                var minuteOfDay = local.TimeOfDay.TotalMinutes;
                var open = Math.Max(minuteOfDay, _startMinute);
                if (open < _endMinute)
                {
                    var available = _endMinute - open;
                    if (remaining <= available)
                    {
                        return ToUtc(local.Date.AddMinutes(open + remaining));
                    }

                    remaining -= available;
                }
            }

            local = local.Date.AddDays(1);
        }

        throw new InvalidOperationException("SLA target is too long for the configured calendar.");
    }

    /// <summary>Whole working minutes between two instants (zero when <paramref name="to"/> is not after <paramref name="from"/>).</summary>
    public int Between(DateTimeOffset from, DateTimeOffset to) => (int)Math.Floor(BetweenExact(from, to).TotalMinutes);

    /// <summary>Exact working time between two instants.</summary>
    public TimeSpan BetweenExact(DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from)
        {
            return TimeSpan.Zero;
        }

        if (!IsBusinessHours)
        {
            return to - from;
        }

        var a = TimeZoneInfo.ConvertTime(from, _zone).DateTime;
        var b = TimeZoneInfo.ConvertTime(to, _zone).DateTime;
        double total = 0;
        for (var day = a.Date; day <= b.Date; day = day.AddDays(1))
        {
            if (!IsWorkDay(day.DayOfWeek))
            {
                continue;
            }

            var open = day.AddMinutes(_startMinute);
            var close = day.AddMinutes(_endMinute);
            var s = a > open ? a : open;
            var e = b < close ? b : close;
            if (e > s)
            {
                total += (e - s).TotalMinutes;
            }
        }

        return TimeSpan.FromMinutes(total);
    }

    private bool IsWorkDay(DayOfWeek day) => (_workDays & (1 << (int)day)) != 0;

    private DateTimeOffset ToUtc(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (_zone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddHours(1);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, _zone), TimeSpan.Zero);
    }
}
