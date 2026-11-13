namespace Solvix.Modules.Devices.Monitoring;

using Solvix.Agent.Contracts;

public sealed record MonitoringSettingsDto(
    int HeartbeatIntervalSeconds,
    int WarningAfterSeconds,
    int OfflineAfterSeconds,
    bool OfflineAlertsEnabled,
    bool RecoveryNotificationsEnabled,
    bool IsDefault);

public sealed record UpdateMonitoringSettingsRequest(
    int HeartbeatIntervalSeconds,
    int WarningAfterSeconds,
    int OfflineAfterSeconds,
    bool OfflineAlertsEnabled,
    bool RecoveryNotificationsEnabled);

public sealed record StateCountsDto(int Total, int Online, int Warning, int Offline)
{
    public static StateCountsDto From(IEnumerable<ConnectivityState> states)
    {
        int online = 0, warning = 0, offline = 0;
        foreach (var state in states)
        {
            switch (state)
            {
                case ConnectivityState.Online: online++; break;
                case ConnectivityState.Warning: warning++; break;
                default: offline++; break;
            }
        }

        return new StateCountsDto(online + warning + offline, online, warning, offline);
    }
}

public sealed record BranchMonitoringDto(Guid? BranchId, string Name, string? Code, StateCountsDto Devices, double? Availability7dPercent);

public sealed record OperatingSystemMonitoringDto(string OperatingSystem, StateCountsDto Devices);

public sealed record SignalCountsDto(IReadOnlyDictionary<string, int> Network, IReadOnlyDictionary<string, int> OperatingSystem);

public sealed record MonitoringSummaryDto
{
    public required DateTimeOffset GeneratedAtUtc { get; init; }

    /// <summary>Active (non-retired) devices by agent connectivity.</summary>
    public required StateCountsDto Devices { get; init; }

    public int RetiredDevices { get; init; }

    public double? Availability7dPercent { get; init; }

    public double? Availability30dPercent { get; init; }

    public required IReadOnlyList<BranchMonitoringDto> Branches { get; init; }

    public required IReadOnlyList<OperatingSystemMonitoringDto> OperatingSystems { get; init; }

    /// <summary>Network and OS signals of devices whose agent is connected; disconnected devices count as Unknown.</summary>
    public required SignalCountsDto Signals { get; init; }

    public required IReadOnlyList<ConnectionEventDto> RecentEvents { get; init; }

    public int OpenAlerts { get; init; }

    public required MonitoringSettingsDto Settings { get; init; }
}

public sealed record ConnectionEventDto(
    Guid Id,
    Guid DeviceId,
    string DeviceName,
    ConnectionEventType Type,
    MonitoringSignal Signal,
    DateTimeOffset OccurredAtUtc,
    string? FromStatus,
    string? ToStatus,
    string? Detail,
    DateTimeOffset? LastSeenAtUtc,
    long? DurationSeconds,
    int? FailedAttempts);

public sealed record ConnectionEventQuery(
    Guid? DeviceId,
    Guid? BranchId,
    ConnectionEventType? Type,
    MonitoringSignal? Signal,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int Page = 1,
    int PageSize = 50);

public sealed record MonitoringAlertDto(
    Guid Id,
    Guid DeviceId,
    string DeviceName,
    MonitoringAlertType Type,
    MonitoringAlertStatus Status,
    string Message,
    DateTimeOffset OpenedAtUtc,
    DateTimeOffset LastSeenAtUtc,
    DateTimeOffset? ResolvedAtUtc,
    long? DowntimeSeconds,
    DateTimeOffset? AcknowledgedAtUtc)
{
    public static MonitoringAlertDto From(MonitoringAlert a) =>
        new(a.Id, a.DeviceId, a.DeviceName, a.Type, a.Status, a.Message, a.OpenedAtUtc, a.LastSeenAtUtc, a.ResolvedAtUtc, a.DowntimeSeconds, a.AcknowledgedAtUtc);
}

public sealed record AlertQuery(MonitoringAlertStatus? Status, Guid? DeviceId, int Page = 1, int PageSize = 50);

/// <summary>One of the three independent signals of a device.</summary>
public sealed record SignalDto<TStatus>(TStatus Status, DateTimeOffset? CheckedAtUtc, bool Stale, IReadOnlyList<string> Issues);

public sealed record DeviceMonitoringDto
{
    public required Guid DeviceId { get; init; }

    public required string DeviceName { get; init; }

    public required bool Monitored { get; init; }

    /// <summary>A. Agent connectivity (heartbeats only).</summary>
    public required ConnectivityState Connectivity { get; init; }

    public required DateTimeOffset StateChangedAtUtc { get; init; }

    public required DateTimeOffset LastSeenAtUtc { get; init; }

    public DateTimeOffset? LastCommunicationAtUtc { get; init; }

    public DateTimeOffset? LastHeartbeatAtUtc { get; init; }

    /// <summary>Continuous agent connectivity since (null while offline).</summary>
    public DateTimeOffset? OnlineSinceUtc { get; init; }

    public long? CurrentUptimeSeconds { get; init; }

    /// <summary>Last evidence of life before the current outage (null unless offline).</summary>
    public DateTimeOffset? OfflineSinceUtc { get; init; }

    public long? CurrentDowntimeSeconds { get; init; }

    public DateTimeOffset? AgentStartedAtUtc { get; init; }

    public DateTimeOffset? SystemBootedAtUtc { get; init; }

    /// <summary>Operating-system uptime as last reported by the agent.</summary>
    public long? SystemUptimeSeconds { get; init; }

    /// <summary>B. Network reachability (agent probes).</summary>
    public required SignalDto<NetworkStatus> Network { get; init; }

    public NetworkReachabilitySnapshot? NetworkSnapshot { get; init; }

    /// <summary>C. Operating-system health.</summary>
    public required SignalDto<OsHealthStatus> OperatingSystem { get; init; }

    public SystemHealthSnapshot? OperatingSystemSnapshot { get; init; }

    public required AvailabilityTotalsDto Today { get; init; }

    public required AvailabilityTotalsDto Last7Days { get; init; }

    public required AvailabilityTotalsDto Last30Days { get; init; }

    public MonitoringAlertDto? OpenAlert { get; init; }

    public required IReadOnlyList<ConnectionEventDto> RecentEvents { get; init; }

    public required MonitoringSettingsDto Thresholds { get; init; }
}

public sealed record AvailabilityTotalsDto(long OnlineSeconds, long WarningSeconds, long OfflineSeconds, int OfflineCount, double? AvailabilityPercent)
{
    /// <summary>Time the device was declared offline (Warning counts as available).</summary>
    public long DowntimeSeconds => OfflineSeconds;

    public long UptimeSeconds => OnlineSeconds + WarningSeconds;
}

public enum AvailabilityGrouping
{
    Device = 1,
    Branch = 2,
}

public sealed record AvailabilityReportQuery(DateOnly? From, DateOnly? To, Guid? BranchId, AvailabilityGrouping GroupBy = AvailabilityGrouping.Device);

public sealed record AvailabilityRowDto(
    Guid Id,
    string Name,
    string? BranchName,
    string? OperatingSystem,
    AvailabilityTotalsDto Totals);

public sealed record AvailabilityDayDto(DateOnly Day, AvailabilityTotalsDto Totals);

public sealed record AvailabilityReportDto(
    DateOnly From,
    DateOnly To,
    DateTimeOffset GeneratedAtUtc,
    AvailabilityGrouping GroupBy,
    AvailabilityTotalsDto Totals,
    IReadOnlyList<AvailabilityRowDto> Rows,
    IReadOnlyList<AvailabilityDayDto> Days);

/// <summary>Pushed over SignalR (method <c>monitoringUpdate</c>) once per unit of work and tenant.</summary>
public sealed record MonitoringUpdateDto(
    IReadOnlyList<DeviceStatusChangeDto> StatusChanges,
    IReadOnlyList<ConnectionEventDto> Events,
    IReadOnlyList<MonitoringAlertDto> Alerts);

public sealed record DeviceStatusChangeDto(
    Guid DeviceId,
    string DeviceName,
    Guid? BranchId,
    ConnectivityState From,
    ConnectivityState To,
    DateTimeOffset ChangedAtUtc,
    DateTimeOffset LastSeenAtUtc);
