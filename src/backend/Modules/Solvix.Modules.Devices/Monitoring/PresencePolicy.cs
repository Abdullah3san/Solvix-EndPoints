namespace Solvix.Modules.Devices.Monitoring;

using Solvix.Agent.Contracts;

/// <summary>Effective thresholds of a tenant.</summary>
public sealed record MonitoringThresholds(int HeartbeatIntervalSeconds, int WarningAfterSeconds, int OfflineAfterSeconds)
{
    public static MonitoringThresholds Default { get; } = new(
        MonitoringSettings.DefaultHeartbeatIntervalSeconds,
        MonitoringSettings.DefaultWarningAfterSeconds,
        MonitoringSettings.DefaultOfflineAfterSeconds);
}

/// <summary>
/// Pure rules of the monitoring model. The three signals are evaluated
/// independently: agent connectivity from contact timestamps only, network
/// reachability and OS health from what the agent measured on the device.
/// </summary>
public static class PresencePolicy
{
    public const double MemoryWarningRatio = 0.10;
    public const double MemoryCriticalRatio = 0.05;
    public const double DiskWarningRatio = 0.10;
    public const double DiskCriticalRatio = 0.05;

    /// <summary>
    /// Connectivity for a silence of (now − last contact). Online below the warning
    /// threshold, Warning up to and INCLUDING the offline threshold, Offline only once
    /// it has been exceeded. <paramref name="evidenceFloor"/> is the earliest instant
    /// silence may be counted from (after a platform outage nobody could reach us, so
    /// the silence is not the device's fault); <paramref name="tolerance"/> covers the
    /// delay with which contact timestamps reach the database.
    /// </summary>
    public static ConnectivityState Evaluate(
        DateTimeOffset lastSeen,
        DateTimeOffset now,
        MonitoringThresholds thresholds,
        DateTimeOffset? evidenceFloor = null,
        TimeSpan tolerance = default)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        var reference = evidenceFloor is { } floor && floor > lastSeen ? floor : lastSeen;
        var silence = now - reference - tolerance;
        if (silence > TimeSpan.FromSeconds(thresholds.OfflineAfterSeconds))
        {
            return ConnectivityState.Offline;
        }

        return silence >= TimeSpan.FromSeconds(thresholds.WarningAfterSeconds) ? ConnectivityState.Warning : ConnectivityState.Online;
    }

    /// <summary>
    /// Network reachability from the agent's local probes. A successful heartbeat
    /// is NOT taken as evidence here: it only proves the path to the platform.
    /// </summary>
    public static NetworkStatus ClassifyNetwork(NetworkReachabilitySnapshot? snapshot)
    {
        if (snapshot is null || snapshot.Checks.Count == 0)
        {
            return NetworkStatus.Unknown;
        }

        if (snapshot.Checks.Any(c => c.Kind == NetworkCheckKind.Interface && !c.Success))
        {
            return NetworkStatus.Unreachable;
        }

        var probes = snapshot.Checks.Where(c => c.Kind != NetworkCheckKind.Interface).ToList();
        if (probes.Count == 0 || probes.All(c => c.Success))
        {
            return NetworkStatus.Reachable;
        }

        return probes.All(c => !c.Success) ? NetworkStatus.Unreachable : NetworkStatus.Degraded;
    }

    public static OsHealthStatus ClassifyOs(SystemHealthSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return OsHealthStatus.Unknown;
        }

        var status = OsHealthStatus.Healthy;
        if (Ratio(snapshot.AvailableMemoryBytes, snapshot.TotalMemoryBytes) is { } memory)
        {
            status = Worst(status, memory < MemoryCriticalRatio ? OsHealthStatus.Critical : memory < MemoryWarningRatio ? OsHealthStatus.Warning : OsHealthStatus.Healthy);
        }

        if (Ratio(snapshot.SystemDriveFreeBytes, snapshot.SystemDriveTotalBytes) is { } disk)
        {
            status = Worst(status, disk < DiskCriticalRatio ? OsHealthStatus.Critical : disk < DiskWarningRatio ? OsHealthStatus.Warning : OsHealthStatus.Healthy);
        }

        if (snapshot.RebootPending == true)
        {
            status = Worst(status, OsHealthStatus.Warning);
        }

        return status;
    }

    public static IEnumerable<string> DescribeOsIssues(SystemHealthSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            yield break;
        }

        if (Ratio(snapshot.AvailableMemoryBytes, snapshot.TotalMemoryBytes) is { } memory && memory < MemoryWarningRatio)
        {
            yield return $"Low memory ({memory:P0} available)";
        }

        if (Ratio(snapshot.SystemDriveFreeBytes, snapshot.SystemDriveTotalBytes) is { } disk && disk < DiskWarningRatio)
        {
            yield return $"Low disk space on {snapshot.SystemDrive ?? "system drive"} ({disk:P0} free)";
        }

        if (snapshot.RebootPending == true)
        {
            yield return "Restart pending";
        }
    }

    /// <summary>Splits [from, to) at UTC midnights.</summary>
    public static IEnumerable<(DateOnly Day, long Seconds)> SplitByDay(DateTimeOffset from, DateTimeOffset to)
    {
        var cursor = from.ToUniversalTime();
        var end = to.ToUniversalTime();
        while (cursor < end)
        {
            var day = DateOnly.FromDateTime(cursor.UtcDateTime);
            var nextMidnight = new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var sliceEnd = nextMidnight < end ? nextMidnight : end;
            var seconds = (long)Math.Round((sliceEnd - cursor).TotalSeconds);
            if (seconds > 0)
            {
                yield return (day, seconds);
            }

            cursor = sliceEnd;
        }
    }

    /// <summary>Availability counts Warning as available: the device is only "down" once it is declared offline.</summary>
    public static double? AvailabilityPercent(long online, long warning, long offline)
    {
        var total = online + warning + offline;
        return total <= 0 ? null : Math.Round(100.0 * (online + warning) / total, 3);
    }

    private static double? Ratio(long? part, long? total) =>
        part is { } p && total is { } t && t > 0 ? (double)p / t : null;

    private static OsHealthStatus Worst(OsHealthStatus a, OsHealthStatus b) => (OsHealthStatus)Math.Max((int)a, (int)b);
}
