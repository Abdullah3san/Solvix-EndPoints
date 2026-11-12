namespace Solvix.UnitTests.Monitoring;

using Solvix.Agent.Contracts;
using Solvix.Modules.Devices.Monitoring;

public sealed class PresencePolicyTests
{
    private static readonly DateTimeOffset Seen = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly MonitoringThresholds Defaults = MonitoringThresholds.Default;

    [Theory]
    [InlineData(0, ConnectivityState.Online)]
    [InlineData(119, ConnectivityState.Online)]
    [InlineData(120, ConnectivityState.Warning)]
    [InlineData(240, ConnectivityState.Warning)]
    [InlineData(300, ConnectivityState.Warning)] // the timeout must be EXCEEDED
    [InlineData(301, ConnectivityState.Offline)]
    public void Default_thresholds(int silenceSeconds, ConnectivityState expected) =>
        Assert.Equal(expected, PresencePolicy.Evaluate(Seen, Seen.AddSeconds(silenceSeconds), Defaults));

    [Fact]
    public void Tolerance_only_ever_delays_a_transition()
    {
        var tolerance = TimeSpan.FromSeconds(7);
        Assert.Equal(ConnectivityState.Warning, PresencePolicy.Evaluate(Seen, Seen.AddSeconds(307), Defaults, tolerance: tolerance));
        Assert.Equal(ConnectivityState.Offline, PresencePolicy.Evaluate(Seen, Seen.AddSeconds(308), Defaults, tolerance: tolerance));
    }

    [Fact]
    public void Evidence_floor_counts_silence_only_after_a_platform_outage()
    {
        var floor = Seen.AddHours(1);
        Assert.Equal(ConnectivityState.Online, PresencePolicy.Evaluate(Seen, floor.AddSeconds(60), Defaults, floor));
        Assert.Equal(ConnectivityState.Offline, PresencePolicy.Evaluate(Seen, floor.AddSeconds(301), Defaults, floor));
        // A floor older than the last contact changes nothing.
        Assert.Equal(ConnectivityState.Offline, PresencePolicy.Evaluate(Seen, Seen.AddSeconds(301), Defaults, Seen.AddDays(-1)));
    }

    [Fact]
    public void Network_signal_is_independent_of_connectivity()
    {
        Assert.Equal(NetworkStatus.Unknown, PresencePolicy.ClassifyNetwork(null));
        Assert.Equal(NetworkStatus.Reachable, PresencePolicy.ClassifyNetwork(Network(true, true, true)));
        Assert.Equal(NetworkStatus.Degraded, PresencePolicy.ClassifyNetwork(Network(true, false, true)));
        Assert.Equal(NetworkStatus.Unreachable, PresencePolicy.ClassifyNetwork(Network(true, false, false)));
        Assert.Equal(NetworkStatus.Unreachable, PresencePolicy.ClassifyNetwork(Network(false, true, true)));
    }

    [Fact]
    public void Os_health_uses_memory_disk_and_pending_reboot()
    {
        Assert.Equal(OsHealthStatus.Unknown, PresencePolicy.ClassifyOs(null));
        Assert.Equal(OsHealthStatus.Healthy, PresencePolicy.ClassifyOs(Os(0.5, 0.5, false)));
        Assert.Equal(OsHealthStatus.Warning, PresencePolicy.ClassifyOs(Os(0.08, 0.5, false)));
        Assert.Equal(OsHealthStatus.Warning, PresencePolicy.ClassifyOs(Os(0.5, 0.5, true)));
        Assert.Equal(OsHealthStatus.Critical, PresencePolicy.ClassifyOs(Os(0.5, 0.02, false)));
        Assert.Equal(3, PresencePolicy.DescribeOsIssues(Os(0.08, 0.08, true)).Count());
    }

    [Fact]
    public void Intervals_are_split_at_utc_midnight()
    {
        var from = new DateTimeOffset(2026, 11, 12, 23, 0, 0, TimeSpan.Zero);
        var parts = PresencePolicy.SplitByDay(from, from.AddHours(26)).ToList();
        Assert.Equal(
            [(new DateOnly(2026, 11, 12), 3600L), (new DateOnly(2026, 11, 13), 86400L), (new DateOnly(2026, 11, 14), 3600L)],
            parts);
        Assert.Empty(PresencePolicy.SplitByDay(from, from));
    }

    [Fact]
    public void Availability_counts_warning_as_available()
    {
        Assert.Null(PresencePolicy.AvailabilityPercent(0, 0, 0));
        Assert.Equal(75, PresencePolicy.AvailabilityPercent(60, 15, 25));
    }

    [Theory]
    [InlineData(60, 120, 300, true)]
    [InlineData(60, 119, 300, false)]
    [InlineData(60, 120, 120, false)]
    [InlineData(10, 120, 300, false)]
    public void Settings_validation(int heartbeat, int warning, int offline, bool valid) =>
        Assert.Equal(valid, MonitoringSettings.Validate(heartbeat, warning, offline).Count == 0);

    [Fact]
    public void Presence_tracks_uptime_and_downtime_across_transitions()
    {
        var presence = new DevicePresence(Guid.NewGuid(), Guid.NewGuid(), Seen);
        presence.Touch(Seen.AddSeconds(30), successful: true, heartbeat: true);
        presence.TransitionTo(ConnectivityState.Warning, Seen.AddSeconds(160));
        Assert.Equal(Seen, presence.OnlineSinceUtc); // a warning is not an interruption of uptime
        presence.TransitionTo(ConnectivityState.Offline, Seen.AddSeconds(340));
        Assert.Null(presence.OnlineSinceUtc);
        Assert.Equal(Seen.AddSeconds(30), presence.OfflineSinceUtc); // downtime starts at the last evidence of life
        presence.TransitionTo(ConnectivityState.Online, Seen.AddSeconds(900));
        Assert.Equal(Seen.AddSeconds(900), presence.OnlineSinceUtc);
        Assert.Null(presence.OfflineSinceUtc);
    }

    [Fact]
    public void Agent_start_time_precision_loss_is_not_a_restart()
    {
        var presence = new DevicePresence(Guid.NewGuid(), Guid.NewGuid(), Seen);
        Assert.False(presence.SetAgentStarted(Seen.AddTicks(1234567)));
        var revision = presence.Revision;
        Assert.False(presence.SetAgentStarted(Seen.AddTicks(1234560))); // microsecond-rounded copy from the database
        Assert.Equal(revision, presence.Revision);
        Assert.True(presence.SetAgentStarted(Seen.AddMinutes(5)));
        Assert.False(presence.SetSystemBooted(Seen.AddHours(-1)));
        Assert.False(presence.SetSystemBooted(Seen.AddHours(-1).AddSeconds(30))); // tick-derived jitter
        Assert.True(presence.SetSystemBooted(Seen.AddMinutes(1)));
    }

    private static NetworkReachabilitySnapshot Network(bool adapter, bool gateway, bool dns) => new()
    {
        Checks =
        [
            new NetworkCheck { Kind = NetworkCheckKind.Interface, Target = "eth", Success = adapter },
            new NetworkCheck { Kind = NetworkCheckKind.Gateway, Target = "gw", Success = gateway },
            new NetworkCheck { Kind = NetworkCheckKind.Dns, Target = "dns", Success = dns },
        ],
    };

    private static SystemHealthSnapshot Os(double memoryFree, double diskFree, bool reboot) => new()
    {
        TotalMemoryBytes = 1000,
        AvailableMemoryBytes = (long)(1000 * memoryFree),
        SystemDriveTotalBytes = 1000,
        SystemDriveFreeBytes = (long)(1000 * diskFree),
        RebootPending = reboot,
    };
}
