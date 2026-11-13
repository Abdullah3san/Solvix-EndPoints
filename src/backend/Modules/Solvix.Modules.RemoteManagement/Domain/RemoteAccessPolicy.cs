namespace Solvix.Modules.RemoteManagement.Domain;

using Solvix.Agent.Contracts.RemoteManagement;
using Solvix.SharedKernel.Entities;

/// <summary>
/// Tenant-wide remote access rules. One row per tenant, created with defaults on first use.
/// Permissions say <em>what</em> a technician may do; this policy says <em>when</em> and
/// <em>under which conditions</em> anyone may do it.
/// </summary>
public sealed class RemoteAccessPolicy : TenantEntity
{
    public const int MinCommandTtlMinutes = 5;
    public const int MaxCommandTtlMinutes = 7 * 24 * 60;

    private RemoteAccessPolicy()
    {
    }

    private RemoteAccessPolicy(Guid tenantId)
        : base(tenantId)
    {
    }

    /// <summary>Operations that need a second person's approval before they are sent to the device.</summary>
    public List<string> ApprovalRequiredCommands { get; private set; } = [CommandTypes.Restart, CommandTypes.Shutdown, CommandTypes.Logoff];

    /// <summary>How long a command may wait for an offline device before it expires.</summary>
    public int CommandTtlMinutes { get; private set; } = 60;

    /// <summary>How long an approval request stays open.</summary>
    public int ApprovalTtlMinutes { get; private set; } = 240;

    /// <summary>IANA time zone the allowed hours refer to.</summary>
    public string TimeZoneId { get; private set; } = "UTC";

    /// <summary>Start of the allowed window in minutes after local midnight; <c>null</c> = always allowed.</summary>
    public int? AllowedFromMinute { get; private set; }

    /// <summary>End of the allowed window (exclusive) in minutes after local midnight.</summary>
    public int? AllowedToMinute { get; private set; }

    /// <summary>Allowed week days as a bit mask (bit 0 = Sunday … bit 6 = Saturday).</summary>
    public int AllowedDaysMask { get; private set; } = 0b111_1111;

    public static RemoteAccessPolicy CreateDefault(Guid tenantId) => new(tenantId);

    public void Update(
        IReadOnlyCollection<string> approvalRequiredCommands,
        int commandTtlMinutes,
        int approvalTtlMinutes,
        string timeZoneId,
        int? allowedFromMinute,
        int? allowedToMinute,
        int allowedDaysMask)
    {
        ArgumentNullException.ThrowIfNull(approvalRequiredCommands);
        ApprovalRequiredCommands = approvalRequiredCommands.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        CommandTtlMinutes = commandTtlMinutes;
        ApprovalTtlMinutes = approvalTtlMinutes;
        TimeZoneId = timeZoneId;
        AllowedFromMinute = allowedFromMinute;
        AllowedToMinute = allowedToMinute;
        AllowedDaysMask = allowedDaysMask;
    }

    public bool RequiresApproval(string commandType) => ApprovalRequiredCommands.Contains(commandType, StringComparer.Ordinal);

    /// <summary>Whether remote work may start at <paramref name="utcNow"/>. Windows may wrap midnight (e.g. 22:00–06:00).</summary>
    public bool IsWithinAllowedHours(DateTimeOffset utcNow)
    {
        var zone = TimeZoneInfo.TryFindSystemTimeZoneById(TimeZoneId, out var found) ? found : TimeZoneInfo.Utc;
        var local = TimeZoneInfo.ConvertTime(utcNow, zone);
        var minute = (local.Hour * 60) + local.Minute;
        var day = (int)local.DayOfWeek;

        if (AllowedFromMinute is not { } from || AllowedToMinute is not { } to)
        {
            return (AllowedDaysMask & (1 << day)) != 0;
        }

        if (from <= to)
        {
            return (AllowedDaysMask & (1 << day)) != 0 && minute >= from && minute < to;
        }

        // Overnight window: the part after midnight belongs to the day the window started.
        return minute >= from
            ? (AllowedDaysMask & (1 << day)) != 0
            : minute < to && (AllowedDaysMask & (1 << ((day + 6) % 7))) != 0;
    }
}

/// <summary>
/// Technician-level scope: which devices one member may reach remotely. A dimension that is
/// <c>null</c> is unrestricted; an empty list allows nothing in that dimension. A member
/// without a row is unrestricted (permissions still apply).
/// </summary>
public sealed class RemoteAccessScope : TenantEntity
{
    private RemoteAccessScope()
    {
    }

    private RemoteAccessScope(Guid tenantId, Guid userId)
        : base(tenantId)
    {
        UserId = userId;
    }

    public Guid UserId { get; private set; }

    public List<Guid>? DeviceGroupIds { get; private set; }

    public List<Guid>? BranchIds { get; private set; }

    public static RemoteAccessScope Create(Guid tenantId, Guid userId) => new(tenantId, userId);

    public void Update(IReadOnlyCollection<Guid>? deviceGroupIds, IReadOnlyCollection<Guid>? branchIds)
    {
        DeviceGroupIds = deviceGroupIds?.Distinct().ToList();
        BranchIds = branchIds?.Distinct().ToList();
    }

    /// <summary>Whether a device in <paramref name="branchId"/> and <paramref name="groupIds"/> is inside this scope.</summary>
    public bool Allows(Guid? branchId, IReadOnlyCollection<Guid> groupIds)
    {
        ArgumentNullException.ThrowIfNull(groupIds);
        var groupOk = DeviceGroupIds is null || groupIds.Any(DeviceGroupIds.Contains);
        var branchOk = BranchIds is null || (branchId is { } b && BranchIds.Contains(b));
        return groupOk && branchOk;
    }
}
