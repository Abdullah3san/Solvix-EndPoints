namespace Solvix.Modules.Devices.Monitoring;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Exceptions;

/// <summary>
/// Agent connectivity: can the Solvix agent on the device talk to the platform?
/// Derived ONLY from authenticated agent contact; it says nothing about the
/// device's network services or operating-system health (see <see cref="NetworkStatus"/>
/// and <see cref="OsHealthStatus"/>).
/// </summary>
public enum ConnectivityState
{
    Online = 1,

    /// <summary>Heartbeats are overdue but the offline timeout has not been exceeded.</summary>
    Warning = 2,

    Offline = 3,
}

/// <summary>Network reachability as observed by the agent's built-in probes.</summary>
public enum NetworkStatus
{
    Unknown = 0,
    Reachable = 1,
    Degraded = 2,
    Unreachable = 3,
}

/// <summary>Operating-system health as measured on the device.</summary>
public enum OsHealthStatus
{
    Unknown = 0,
    Healthy = 1,
    Warning = 2,
    Critical = 3,
}

public enum MonitoringSignal
{
    Agent = 1,
    Network = 2,
    OperatingSystem = 3,
}

public enum ConnectionEventType
{
    /// <summary>First contact after enrollment.</summary>
    Enrolled = 1,

    /// <summary>Online → Warning: heartbeats are overdue.</summary>
    HeartbeatDelayed = 2,

    /// <summary>Warning → Online: contact resumed before the offline timeout.</summary>
    HeartbeatRestored = 3,

    /// <summary>The offline timeout was exceeded.</summary>
    WentOffline = 4,

    /// <summary>Offline → Online.</summary>
    Recovered = 5,

    /// <summary>The agent reported failed attempts that never became a Warning (short interruption).</summary>
    Reconnected = 6,

    /// <summary>The agent process restarted (service restart, update).</summary>
    AgentRestarted = 7,

    /// <summary>The operating system restarted.</summary>
    DeviceRebooted = 8,

    NetworkStatusChanged = 9,

    OsHealthChanged = 10,

    /// <summary>The platform itself was not observing (outage/restart); agents get a grace period.</summary>
    MonitoringResumed = 11,
}

/// <summary>
/// Live monitoring state of one device (1:1 with <c>devices</c>, same id).
/// Kept in its own narrow table so the high-frequency contact timestamps are
/// cheap, HOT-updatable writes that never rewrite the wide device row.
/// </summary>
public sealed class DevicePresence : TenantEntity
{
    public const int SnapshotMaxLength = 16 * 1024;

    private DevicePresence()
    {
    }

    public DevicePresence(Guid tenantId, Guid deviceId, DateTimeOffset now)
        : base(tenantId)
    {
        Id = deviceId;
        DeviceId = deviceId;
        State = ConnectivityState.Online;
        StateChangedAtUtc = now;
        OnlineSinceUtc = now;
        LastSeenAtUtc = now;
        LastCommunicationAtUtc = now;
        AccountedUntilUtc = now;
    }

    public Guid DeviceId { get; private set; }

    public ConnectivityState State { get; private set; }

    public DateTimeOffset StateChangedAtUtc { get; private set; }

    /// <summary>Last authenticated request from the agent (successful or not).</summary>
    public DateTimeOffset LastSeenAtUtc { get; private set; }

    /// <summary>Last request from the agent that the server processed successfully.</summary>
    public DateTimeOffset? LastCommunicationAtUtc { get; private set; }

    public DateTimeOffset? LastHeartbeatAtUtc { get; private set; }

    /// <summary>Start of the current connected period (uptime); null while offline.</summary>
    public DateTimeOffset? OnlineSinceUtc { get; private set; }

    /// <summary>Last evidence of life before the current outage (downtime start); null unless offline.</summary>
    public DateTimeOffset? OfflineSinceUtc { get; private set; }

    public DateTimeOffset? AgentStartedAtUtc { get; private set; }

    public DateTimeOffset? SystemBootedAtUtc { get; private set; }

    public NetworkStatus NetworkStatus { get; private set; }

    public DateTimeOffset? NetworkCheckedAtUtc { get; private set; }

    public string? NetworkJson { get; private set; }

    public OsHealthStatus OsHealthStatus { get; private set; }

    public DateTimeOffset? OsHealthCheckedAtUtc { get; private set; }

    public string? OsHealthJson { get; private set; }

    public DateTimeOffset? SignalsPersistedAtUtc { get; private set; }

    /// <summary>Time up to which the availability ledger has been written.</summary>
    public DateTimeOffset AccountedUntilUtc { get; private set; }

    /// <summary>Optimistic concurrency token for state transitions (the batched contact flush does not bump it).</summary>
    public int Revision { get; private set; }

    public void Touch(DateTimeOffset seenAt, bool successful, bool heartbeat)
    {
        if (seenAt > LastSeenAtUtc)
        {
            LastSeenAtUtc = seenAt;
        }

        if (successful && (LastCommunicationAtUtc is null || seenAt > LastCommunicationAtUtc))
        {
            LastCommunicationAtUtc = seenAt;
        }

        if (heartbeat && (LastHeartbeatAtUtc is null || seenAt > LastHeartbeatAtUtc))
        {
            LastHeartbeatAtUtc = seenAt;
        }
    }

    /// <summary>Moves to <paramref name="next"/>; the caller has already closed the availability ledger up to <paramref name="now"/>.</summary>
    public void TransitionTo(ConnectivityState next, DateTimeOffset now)
    {
        if (next == State)
        {
            return;
        }

        var previous = State;
        State = next;
        StateChangedAtUtc = now;
        switch (next)
        {
            case ConnectivityState.Offline:
                OnlineSinceUtc = null;
                OfflineSinceUtc = LastSeenAtUtc;
                break;
            case ConnectivityState.Online when previous == ConnectivityState.Offline:
                OnlineSinceUtc = now;
                OfflineSinceUtc = null;
                break;
            default:
                break;
        }

        Bump();
    }

    public void SetAccountedUntil(DateTimeOffset until)
    {
        if (until > AccountedUntilUtc)
        {
            AccountedUntilUtc = until;
            Bump();
        }
    }

    public bool SetAgentStarted(DateTimeOffset? startedAt)
    {
        // Compared with a tolerance: the database stores microseconds, the agent sends 100 ns ticks,
        // so an exact comparison would turn every heartbeat into a write.
        if (startedAt is null || (AgentStartedAtUtc is { } previous && (startedAt.Value - previous).Duration() <= TimeSpan.FromSeconds(2)))
        {
            return false;
        }

        var restarted = AgentStartedAtUtc is not null;
        AgentStartedAtUtc = startedAt;
        Bump();
        return restarted;
    }

    /// <summary>Boot time is derived from a tick count on the device: small drifts are not a reboot.</summary>
    public bool SetSystemBooted(DateTimeOffset? bootedAt)
    {
        if (bootedAt is null)
        {
            return false;
        }

        if (SystemBootedAtUtc is { } previous && (bootedAt.Value - previous).Duration() <= TimeSpan.FromSeconds(90))
        {
            return false;
        }

        var rebooted = SystemBootedAtUtc is not null && bootedAt.Value > SystemBootedAtUtc;
        SystemBootedAtUtc = bootedAt;
        Bump();
        return rebooted;
    }

    public void SetSignals(NetworkStatus network, string? networkJson, OsHealthStatus os, string? osJson, DateTimeOffset now)
    {
        NetworkStatus = network;
        NetworkJson = networkJson is { Length: <= SnapshotMaxLength } ? networkJson : null;
        NetworkCheckedAtUtc = networkJson is null ? NetworkCheckedAtUtc : now;
        OsHealthStatus = os;
        OsHealthJson = osJson is { Length: <= SnapshotMaxLength } ? osJson : null;
        OsHealthCheckedAtUtc = osJson is null ? OsHealthCheckedAtUtc : now;
        SignalsPersistedAtUtc = now;
        Bump();
    }

    private void Bump() => Revision++;
}

/// <summary>Connection history: one row per connectivity transition or notable agent/device event.</summary>
public sealed class DeviceConnectionEvent : TenantEntity
{
    public const int DetailMaxLength = 500;

    private DeviceConnectionEvent()
    {
    }

    public DeviceConnectionEvent(
        Guid tenantId,
        Guid deviceId,
        ConnectionEventType type,
        MonitoringSignal signal,
        DateTimeOffset occurredAt,
        string? fromStatus = null,
        string? toStatus = null,
        string? detail = null,
        DateTimeOffset? lastSeenAt = null,
        long? durationSeconds = null,
        int? failedAttempts = null)
        : base(tenantId)
    {
        DeviceId = deviceId;
        Type = type;
        Signal = signal;
        OccurredAtUtc = occurredAt;
        FromStatus = fromStatus;
        ToStatus = toStatus;
        Detail = detail is { Length: > DetailMaxLength } ? detail[..DetailMaxLength] : detail;
        LastSeenAtUtc = lastSeenAt;
        DurationSeconds = durationSeconds;
        FailedAttempts = failedAttempts;
    }

    public Guid DeviceId { get; private set; }

    public ConnectionEventType Type { get; private set; }

    public MonitoringSignal Signal { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public string? FromStatus { get; private set; }

    public string? ToStatus { get; private set; }

    public string? Detail { get; private set; }

    public DateTimeOffset? LastSeenAtUtc { get; private set; }

    /// <summary>Length of the interruption/outage that ended with this event, when applicable.</summary>
    public long? DurationSeconds { get; private set; }

    /// <summary>Failed connection attempts the agent reported for the interruption.</summary>
    public int? FailedAttempts { get; private set; }
}

/// <summary>Daily availability aggregate per device (UTC days), written only on transitions and at day rollover.</summary>
public sealed class DeviceAvailabilityDay : TenantEntity
{
    private DeviceAvailabilityDay()
    {
    }

    public DeviceAvailabilityDay(Guid tenantId, Guid deviceId, DateOnly day)
        : base(tenantId)
    {
        DeviceId = deviceId;
        Day = day;
    }

    public Guid DeviceId { get; private set; }

    public DateOnly Day { get; private set; }

    public long OnlineSeconds { get; private set; }

    public long WarningSeconds { get; private set; }

    public long OfflineSeconds { get; private set; }

    public int OfflineCount { get; private set; }

    public void Add(ConnectivityState state, long seconds)
    {
        switch (state)
        {
            case ConnectivityState.Online:
                OnlineSeconds += seconds;
                break;
            case ConnectivityState.Warning:
                WarningSeconds += seconds;
                break;
            default:
                OfflineSeconds += seconds;
                break;
        }
    }

    public void CountOffline() => OfflineCount++;
}

public enum MonitoringAlertType
{
    DeviceOffline = 1,
}

public enum MonitoringAlertStatus
{
    Open = 1,
    Resolved = 2,
}

/// <summary>Automatic offline alert; resolved (recovery notification) when the device comes back.</summary>
public sealed class MonitoringAlert : TenantEntity
{
    private MonitoringAlert()
    {
    }

    public MonitoringAlert(Guid tenantId, Guid deviceId, string deviceName, DateTimeOffset openedAt, DateTimeOffset lastSeenAt, string message)
        : base(tenantId)
    {
        DeviceId = deviceId;
        DeviceName = deviceName.Length > 255 ? deviceName[..255] : deviceName;
        Type = MonitoringAlertType.DeviceOffline;
        Status = MonitoringAlertStatus.Open;
        OpenedAtUtc = openedAt;
        LastSeenAtUtc = lastSeenAt;
        Message = message;
    }

    public Guid DeviceId { get; private set; }

    public string DeviceName { get; private set; } = string.Empty;

    public MonitoringAlertType Type { get; private set; }

    public MonitoringAlertStatus Status { get; private set; }

    public DateTimeOffset OpenedAtUtc { get; private set; }

    public DateTimeOffset LastSeenAtUtc { get; private set; }

    public string Message { get; private set; } = string.Empty;

    public DateTimeOffset? ResolvedAtUtc { get; private set; }

    public long? DowntimeSeconds { get; private set; }

    public DateTimeOffset? AcknowledgedAtUtc { get; private set; }

    public Guid? AcknowledgedByUserId { get; private set; }

    public void Resolve(DateTimeOffset now)
    {
        if (Status == MonitoringAlertStatus.Resolved)
        {
            return;
        }

        Status = MonitoringAlertStatus.Resolved;
        ResolvedAtUtc = now;
        DowntimeSeconds = Math.Max(0, (long)(now - LastSeenAtUtc).TotalSeconds);
    }

    public void Acknowledge(Guid userId, DateTimeOffset now)
    {
        if (AcknowledgedAtUtc is not null)
        {
            return;
        }

        AcknowledgedAtUtc = now;
        AcknowledgedByUserId = userId;
    }
}

/// <summary>Per-tenant monitoring configuration (thresholds, heartbeat interval, notifications).</summary>
public sealed class MonitoringSettings : TenantEntity
{
    public const int DefaultHeartbeatIntervalSeconds = 60;
    public const int DefaultWarningAfterSeconds = 120;
    public const int DefaultOfflineAfterSeconds = 300;

    private MonitoringSettings()
    {
    }

    public MonitoringSettings(Guid tenantId)
        : base(tenantId)
    {
    }

    public int HeartbeatIntervalSeconds { get; private set; } = DefaultHeartbeatIntervalSeconds;

    public int WarningAfterSeconds { get; private set; } = DefaultWarningAfterSeconds;

    public int OfflineAfterSeconds { get; private set; } = DefaultOfflineAfterSeconds;

    public bool OfflineAlertsEnabled { get; private set; } = true;

    public bool RecoveryNotificationsEnabled { get; private set; } = true;

    public void Update(int heartbeat, int warning, int offline, bool alerts, bool recovery)
    {
        var errors = Validate(heartbeat, warning, offline);
        if (errors.Count > 0)
        {
            throw new ValidationException(errors.ToDictionary(e => e.Key, e => new[] { e.Value }));
        }

        HeartbeatIntervalSeconds = heartbeat;
        WarningAfterSeconds = warning;
        OfflineAfterSeconds = offline;
        OfflineAlertsEnabled = alerts;
        RecoveryNotificationsEnabled = recovery;
    }

    /// <summary>
    /// Thresholds must leave room for at least two heartbeats before a warning,
    /// so a single late or lost heartbeat never changes the state.
    /// </summary>
    public static Dictionary<string, string> Validate(int heartbeat, int warning, int offline)
    {
        var errors = new Dictionary<string, string>();
        if (heartbeat is < 15 or > 3600)
        {
            errors["heartbeatIntervalSeconds"] = "The heartbeat interval must be between 15 seconds and 1 hour.";
        }

        if (warning < 2 * heartbeat)
        {
            errors["warningAfterSeconds"] = "The warning threshold must be at least twice the heartbeat interval.";
        }

        if (offline <= warning)
        {
            errors["offlineAfterSeconds"] = "The offline threshold must be greater than the warning threshold.";
        }
        else if (offline > 7 * 24 * 3600)
        {
            errors["offlineAfterSeconds"] = "The offline threshold cannot exceed 7 days.";
        }

        return errors;
    }
}

/// <summary>Platform-wide bookkeeping of the presence monitor (not tenant data).</summary>
public sealed class MonitorCheckpoint
{
    public const string PresenceMonitor = "presence-monitor";

    private MonitorCheckpoint()
    {
    }

    public MonitorCheckpoint(string name, DateTimeOffset now)
    {
        Name = name;
        LastSweepAtUtc = now;
        ObservingSinceUtc = now;
        EvidenceFloorUtc = now;
        LastRollupDay = DateOnly.FromDateTime(now.UtcDateTime);
        LastPurgeAtUtc = now;
    }

    public string Name { get; private set; } = string.Empty;

    public DateTimeOffset LastSweepAtUtc { get; set; }

    /// <summary>Since when the monitor has been observing without interruption.</summary>
    public DateTimeOffset ObservingSinceUtc { get; set; }

    /// <summary>No device is judged on silence before this instant (grace after a platform outage).</summary>
    public DateTimeOffset EvidenceFloorUtc { get; set; }

    public DateOnly LastRollupDay { get; set; }

    public DateTimeOffset LastPurgeAtUtc { get; set; }
}
