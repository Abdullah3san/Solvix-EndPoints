namespace Solvix.IntegrationTests.Tests;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Solvix.Agent.Contracts;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Devices.Application.Devices;
using Solvix.Modules.Devices.Monitoring;
using Solvix.SharedKernel.Results;

/// <summary>
/// Real-time monitoring against the real API and database. Time is simulated:
/// <see cref="StepAsync"/> advances the API clock in sweep-sized steps, running
/// the contact flush and the monitor after each step exactly as production does.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class MonitoringTests(ApiFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan SweepStep = TimeSpan.FromSeconds(15);

    private TestClock Clock => fixture.Factory.Clock;

    public async Task InitializeAsync()
    {
        Clock.Reset();
        await ResetMonitorAsync();
    }

    public Task DisposeAsync()
    {
        Clock.Reset();
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------- thresholds

    [Fact]
    public async Task Default_thresholds_online_below_2_min_warning_2_to_5_offline_after_5()
    {
        var (tenant, device, agent, admin) = await EnrollAsync();
        await HttpAssert.OkAsync<HeartbeatResponse>(await agent.HeartbeatAsync());

        await StepAsync(TimeSpan.FromSeconds(105));
        Assert.Equal(ConnectivityState.Online, await StateAsync(admin, device.DeviceId));

        await StepAsync(TimeSpan.FromSeconds(30)); // 135 s of silence
        Assert.Equal(ConnectivityState.Warning, await StateAsync(admin, device.DeviceId));

        // At the offline threshold the device is still only a warning: the timeout must be EXCEEDED.
        await StepAsync(TimeSpan.FromSeconds(165)); // 300 s
        Assert.Equal(ConnectivityState.Warning, await StateAsync(admin, device.DeviceId));
        Assert.Empty(await OpenAlertsAsync(admin));

        await StepAsync(TimeSpan.FromSeconds(15)); // 315 s (> 300 s + flush tolerance)
        Assert.Equal(ConnectivityState.Offline, await StateAsync(admin, device.DeviceId));

        var events = await EventsAsync(admin, device.DeviceId);
        Assert.Equal(
            [ConnectionEventType.Enrolled, ConnectionEventType.HeartbeatDelayed, ConnectionEventType.WentOffline],
            events.Select(e => e.Type).Reverse());
        var alert = Assert.Single(await OpenAlertsAsync(admin));
        Assert.Equal(device.DeviceId, alert.DeviceId);

        var summary = await HttpAssert.OkAsync<MonitoringSummaryDto>(await admin.GetAsync("/api/v1/monitoring/summary"));
        Assert.Equal(new StateCountsDto(1, 0, 0, 1), summary.Devices);
        Assert.Equal(1, summary.OpenAlerts);
        var stats = await HttpAssert.OkAsync<DeviceStatsDto>(await admin.GetAsync("/api/v1/devices/stats"));
        Assert.Equal(1, stats.Offline);
        _ = tenant;
    }

    [Fact]
    public async Task Delayed_heartbeat_returns_to_online_without_offline_alert()
    {
        var (_, device, agent, admin) = await EnrollAsync();
        await agent.HeartbeatAsync();
        await StepAsync(TimeSpan.FromMinutes(3));
        Assert.Equal(ConnectivityState.Warning, await StateAsync(admin, device.DeviceId));

        await HttpAssert.OkAsync<HeartbeatResponse>(await agent.HeartbeatAsync(Health(failures: 2)));
        Assert.Equal(ConnectivityState.Online, await StateAsync(admin, device.DeviceId));

        var restored = (await EventsAsync(admin, device.DeviceId)).First();
        Assert.Equal(ConnectionEventType.HeartbeatRestored, restored.Type);
        Assert.Equal(2, restored.FailedAttempts);
        Assert.Empty(await OpenAlertsAsync(admin));

        var monitoring = await DeviceMonitoringAsync(admin, device.DeviceId);
        Assert.Equal(0, monitoring.Today.OfflineSeconds);
        Assert.Equal(100, monitoring.Today.AvailabilityPercent);
    }

    [Fact]
    public async Task Short_network_interruption_is_recorded_as_reconnection_without_state_change()
    {
        var (_, device, agent, admin) = await EnrollAsync();
        await agent.HeartbeatAsync();
        await StepAsync(TimeSpan.FromSeconds(90));
        await HttpAssert.OkAsync<HeartbeatResponse>(await agent.HeartbeatAsync(Health(failures: 3)));

        Assert.Equal(ConnectivityState.Online, await StateAsync(admin, device.DeviceId));
        var events = await EventsAsync(admin, device.DeviceId);
        var reconnected = events.First();
        Assert.Equal(ConnectionEventType.Reconnected, reconnected.Type);
        Assert.Equal(3, reconnected.FailedAttempts);
        Assert.DoesNotContain(events, e => e.Type is ConnectionEventType.HeartbeatDelayed or ConnectionEventType.WentOffline);
    }

    [Fact]
    public async Task Agent_restart_and_device_reboot_are_history_not_outages()
    {
        var (_, device, agent, admin) = await EnrollAsync();
        var boot = Clock.UtcNow.AddHours(-5);
        var started = Clock.UtcNow.AddHours(-5);
        await agent.HeartbeatAsync(Health(started: started, booted: boot));

        // Service restart: new agent start time, same boot.
        await StepAsync(TimeSpan.FromSeconds(30));
        await agent.HeartbeatAsync(Health(started: Clock.UtcNow, booted: boot.AddSeconds(20))); // tick-derived boot time jitters
        Assert.Equal(ConnectionEventType.AgentRestarted, (await EventsAsync(admin, device.DeviceId)).First().Type);

        // Reboot: ~1 minute without contact, new boot time. Recorded once, as a reboot.
        await StepAsync(TimeSpan.FromSeconds(75));
        await agent.HeartbeatAsync(Health(started: Clock.UtcNow, booted: Clock.UtcNow.AddSeconds(-40)));
        var events = await EventsAsync(admin, device.DeviceId);
        Assert.Equal(ConnectionEventType.DeviceRebooted, events.First().Type);
        Assert.Single(events, e => e.Type == ConnectionEventType.AgentRestarted);
        Assert.Equal(ConnectivityState.Online, await StateAsync(admin, device.DeviceId));

        var monitoring = await DeviceMonitoringAsync(admin, device.DeviceId);
        Assert.InRange(monitoring.SystemUptimeSeconds!.Value, 30, 60);
    }

    [Fact]
    public async Task Offline_device_recovers_with_downtime_and_resolved_alert()
    {
        var (_, device, agent, admin) = await EnrollAsync();
        await agent.HeartbeatAsync();
        await StepAsync(TimeSpan.FromMinutes(10));
        Assert.Equal(ConnectivityState.Offline, await StateAsync(admin, device.DeviceId));
        var offline = await DeviceMonitoringAsync(admin, device.DeviceId);
        Assert.NotNull(offline.OfflineSinceUtc);
        Assert.InRange(offline.CurrentDowntimeSeconds!.Value, 595, 610);
        Assert.True(offline.Network.Stale);

        await HttpAssert.OkAsync<HeartbeatResponse>(await agent.HeartbeatAsync(Health(failures: 9)));
        var recovered = (await EventsAsync(admin, device.DeviceId)).First();
        Assert.Equal(ConnectionEventType.Recovered, recovered.Type);
        Assert.InRange(recovered.DurationSeconds!.Value, 595, 610);
        Assert.Equal(9, recovered.FailedAttempts);
        Assert.Empty(await OpenAlertsAsync(admin));
        var resolved = Assert.Single((await HttpAssert.OkAsync<PagedResult<MonitoringAlertDto>>(await admin.GetAsync($"/api/v1/monitoring/alerts?deviceId={device.DeviceId}"))).Items);
        Assert.Equal(MonitoringAlertStatus.Resolved, resolved.Status);

        var monitoring = await DeviceMonitoringAsync(admin, device.DeviceId);
        Assert.Equal(ConnectivityState.Online, monitoring.Connectivity);
        Assert.Equal(0, monitoring.CurrentUptimeSeconds);
        // Downtime counts from the start of the Warning that preceded the outage (≈ last contact +
        // warning threshold, at sweep granularity), not from when the offline timeout fired.
        Assert.InRange(monitoring.Today.OfflineSeconds, 455, 490);
        Assert.Equal(1, monitoring.Today.OfflineCount);
        Assert.True(monitoring.Today.AvailabilityPercent < 30);
    }

    // ------------------------------------------------- false-offline protection

    [Fact]
    public async Task Platform_outage_does_not_declare_devices_offline()
    {
        var (tenant, returning, agent, admin) = await EnrollAsync();
        var (_, silent, silentAgent, _) = await EnrollAsync(tenant, admin);
        await agent.HeartbeatAsync();
        await silentAgent.HeartbeatAsync();
        await StepAsync(TimeSpan.FromSeconds(30));

        // The platform is down for 20 minutes: no sweeps, no heartbeats received.
        Clock.Advance(TimeSpan.FromMinutes(20));
        var first = await SweepAsync();
        Assert.True(first.OutageDetected);
        Assert.Equal(ConnectivityState.Online, await StateAsync(admin, returning.DeviceId));
        Assert.Equal(ConnectivityState.Online, await StateAsync(admin, silent.DeviceId));

        // Agents still in their retry backoff come back several minutes later.
        await StepAsync(TimeSpan.FromMinutes(8));
        Assert.Equal(ConnectivityState.Online, await StateAsync(admin, silent.DeviceId));
        await agent.HeartbeatAsync(Health(failures: 6));

        // A device that never comes back: silence only counts after the grace (min(outage, 15 min)),
        // so Warning after grace + 2 min and Offline after grace + 5 min. The returning agent keeps
        // its normal 60 s heartbeat meanwhile.
        await StepWithHeartbeatsAsync(agent, TimeSpan.FromMinutes(8)); // 16 min after the outage: still within grace + warning
        Assert.Equal(ConnectivityState.Online, await StateAsync(admin, silent.DeviceId));
        await StepWithHeartbeatsAsync(agent, TimeSpan.FromMinutes(2)); // 18 min
        Assert.Equal(ConnectivityState.Warning, await StateAsync(admin, silent.DeviceId));
        await StepWithHeartbeatsAsync(agent, TimeSpan.FromMinutes(3)); // 21 min after the outage
        Assert.Equal(ConnectivityState.Offline, await StateAsync(admin, silent.DeviceId));
        Assert.Equal(ConnectivityState.Online, await StateAsync(admin, returning.DeviceId));
        Assert.DoesNotContain(await EventsAsync(admin, returning.DeviceId), e => e.Type == ConnectionEventType.WentOffline);
    }

    [Fact]
    public async Task Contact_through_any_agent_endpoint_counts_and_is_flushed_in_batches()
    {
        var (_, device, agent, admin) = await EnrollAsync();
        await agent.HeartbeatAsync();
        await StepAsync(TimeSpan.FromMinutes(4));
        Assert.Equal(ConnectivityState.Warning, await StateAsync(admin, device.DeviceId));

        // Not a heartbeat: an update check. Proof of life all the same, recorded via the buffer.
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync("/" + AgentRoutes.Updates)).StatusCode);
        await fixture.FlushPresenceAsync();
        Assert.Equal(ConnectivityState.Online, await StateAsync(admin, device.DeviceId));
        Assert.Equal(ConnectionEventType.HeartbeatRestored, (await EventsAsync(admin, device.DeviceId)).First().Type);
    }

    [Fact]
    public async Task Routine_heartbeats_do_not_write_to_the_database_until_the_batched_flush()
    {
        var (_, device, agent, admin) = await EnrollAsync();
        await agent.HeartbeatAsync(); // first heartbeat stores signals/agent start (a real change)
        await fixture.FlushPresenceAsync();
        var before = await ReadRowAsync(device.DeviceId);

        for (var i = 0; i < 5; i++)
        {
            Clock.Advance(TimeSpan.FromSeconds(10));
            await HttpAssert.OkAsync<HeartbeatResponse>(await agent.HeartbeatAsync());
        }

        // Neither the device row nor the presence state was written synchronously (the
        // background flush of the shared fixture may have advanced last_seen in between).
        var during = await ReadRowAsync(device.DeviceId);
        Assert.Equal(before.Revision, during.Revision);
        Assert.Equal(before.DeviceUpdated, during.DeviceUpdated);

        await fixture.FlushPresenceAsync();
        var after = await ReadRowAsync(device.DeviceId);
        Assert.Equal(before.DeviceUpdated, after.DeviceUpdated);
        Assert.Equal(before.Revision, after.Revision);
        Assert.True(after.LastSeen > before.LastSeen.AddSeconds(45));
        Assert.Equal(ConnectivityState.Online, await StateAsync(admin, device.DeviceId));
    }

    // ------------------------------------------------------------------ signals

    [Fact]
    public async Task Network_and_os_health_are_separate_signals_from_agent_connectivity()
    {
        var (_, device, agent, admin) = await EnrollAsync();
        await agent.HeartbeatAsync(Health(gatewayOk: true, freeDiskRatio: 0.5));
        await agent.HeartbeatAsync(Health(gatewayOk: false, freeDiskRatio: 0.03));

        var monitoring = await DeviceMonitoringAsync(admin, device.DeviceId);
        Assert.Equal(ConnectivityState.Online, monitoring.Connectivity); // the agent reaches us...
        Assert.Equal(NetworkStatus.Degraded, monitoring.Network.Status); // ...but the gateway does not answer
        Assert.Contains(monitoring.Network.Issues, i => i.Contains("Gateway", StringComparison.Ordinal));
        Assert.Equal(OsHealthStatus.Critical, monitoring.OperatingSystem.Status);
        Assert.False(monitoring.Network.Stale);

        var events = await EventsAsync(admin, device.DeviceId);
        Assert.Contains(events, e => e.Type == ConnectionEventType.NetworkStatusChanged && e.Signal == MonitoringSignal.Network && e.ToStatus == "Degraded");
        Assert.Contains(events, e => e.Type == ConnectionEventType.OsHealthChanged && e.Signal == MonitoringSignal.OperatingSystem && e.ToStatus == "Critical");

        var summary = await HttpAssert.OkAsync<MonitoringSummaryDto>(await admin.GetAsync("/api/v1/monitoring/summary"));
        Assert.Equal(1, summary.Signals.Network["Degraded"]);
        Assert.Equal(1, summary.Signals.OperatingSystem["Critical"]);

        // Once the agent is offline its last network/OS readings are stale and not counted as current.
        await StepAsync(TimeSpan.FromMinutes(6));
        summary = await HttpAssert.OkAsync<MonitoringSummaryDto>(await admin.GetAsync("/api/v1/monitoring/summary"));
        Assert.Equal(1, summary.Signals.Network["Unknown"]);
        Assert.True((await DeviceMonitoringAsync(admin, device.DeviceId)).OperatingSystem.Stale);
    }

    // ----------------------------------------------------------------- settings

    [Fact]
    public async Task Administrators_configure_thresholds_and_heartbeat_interval()
    {
        var (tenant, device, agent, admin) = await EnrollAsync();
        using var member = await MemberClientAsync(tenant); // before simulated time moves (token issue time)
        var defaults = await HttpAssert.OkAsync<MonitoringSettingsDto>(await admin.GetAsync("/api/v1/monitoring/settings"));
        Assert.Equal(new MonitoringSettingsDto(60, 120, 300, true, true, true), defaults);

        await HttpAssert.ProblemAsync(await admin.PutAsJsonAsync("/api/v1/monitoring/settings", new UpdateMonitoringSettingsRequest(60, 90, 300, true, true), ApiFixture.Json), HttpStatusCode.BadRequest, "validation_failed");
        await HttpAssert.ProblemAsync(await admin.PutAsJsonAsync("/api/v1/monitoring/settings", new UpdateMonitoringSettingsRequest(30, 120, 120, true, true), ApiFixture.Json), HttpStatusCode.BadRequest, "validation_failed");

        var updated = await HttpAssert.OkAsync<MonitoringSettingsDto>(
            await admin.PutAsJsonAsync("/api/v1/monitoring/settings", new UpdateMonitoringSettingsRequest(30, 60, 90, false, true), ApiFixture.Json));
        Assert.False(updated.IsDefault);

        // Agents receive the new heartbeat interval with their next heartbeat.
        var response = await HttpAssert.OkAsync<HeartbeatResponse>(await agent.HeartbeatAsync());
        Assert.Equal(30, response.Configuration.HeartbeatIntervalSeconds);

        await StepAsync(TimeSpan.FromSeconds(75));
        Assert.Equal(ConnectivityState.Warning, await StateAsync(admin, device.DeviceId));
        await StepAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(ConnectivityState.Offline, await StateAsync(admin, device.DeviceId));
        Assert.Empty(await OpenAlertsAsync(admin)); // offline alerts disabled for this tenant

        // Members may read but not change monitoring settings.
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/api/v1/monitoring/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync("/api/v1/monitoring/settings", new UpdateMonitoringSettingsRequest(60, 120, 300, true, true), ApiFixture.Json)).StatusCode);
    }

    // ------------------------------------------------------- dashboard + reports

    [Fact]
    public async Task Dashboard_groups_by_branch_and_os_and_reports_availability()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var branch = await HttpAssert.OkAsync<Solvix.Modules.Organization.Application.Branches.BranchDto>(
            await admin.PostAsJsonAsync("/api/v1/branches", new Solvix.Modules.Organization.Application.Branches.CreateBranchRequest("Riyadh HQ", "RUH", false, null, null, null, null), ApiFixture.Json), HttpStatusCode.Created);

        var (_, a, agentA, _) = await EnrollAsync(tenant, admin);
        var (_, b, agentB, _) = await EnrollAsync(tenant, admin);
        await admin.PutAsJsonAsync($"/api/v1/devices/{a.DeviceId}/assignment", new UpdateDeviceAssignmentRequest(null, branch.Id, null), ApiFixture.Json);
        await agentA.HeartbeatAsync();
        await agentB.HeartbeatAsync();
        for (var i = 0; i < 8; i++)
        {
            await StepAsync(TimeSpan.FromSeconds(45));
            await agentA.HeartbeatAsync(); // A stays online, B goes silent
        }

        var summary = await HttpAssert.OkAsync<MonitoringSummaryDto>(await admin.GetAsync("/api/v1/monitoring/summary"));
        Assert.Equal(new StateCountsDto(2, 1, 0, 1), summary.Devices);
        var hq = Assert.Single(summary.Branches, x => x.BranchId == branch.Id);
        Assert.Equal(new StateCountsDto(1, 1, 0, 0), hq.Devices);
        Assert.Equal(100, hq.Availability7dPercent);
        Assert.Equal(new StateCountsDto(1, 0, 0, 1), Assert.Single(summary.Branches, x => x.BranchId is null).Devices);
        Assert.Equal(2, Assert.Single(summary.OperatingSystems).Devices.Total);
        Assert.Contains(summary.RecentEvents, e => e.DeviceId == b.DeviceId && e.Type == ConnectionEventType.WentOffline);
        Assert.InRange(summary.Availability7dPercent!.Value, 50, 99.9);

        var report = await HttpAssert.OkAsync<AvailabilityReportDto>(await admin.GetAsync("/api/v1/monitoring/availability"));
        Assert.Equal(7, report.Days.Count);
        Assert.Equal(b.DeviceId, report.Rows[0].Id); // least available first
        Assert.True(report.Rows[0].Totals.DowntimeSeconds > 0);
        Assert.Equal(100, report.Rows[1].Totals.AvailabilityPercent);

        var byBranch = await HttpAssert.OkAsync<AvailabilityReportDto>(await admin.GetAsync($"/api/v1/monitoring/availability?groupBy=Branch&branchId={branch.Id}"));
        Assert.Equal("Riyadh HQ", Assert.Single(byBranch.Rows).Name);

        var csv = await admin.GetAsync("/api/v1/monitoring/availability/export");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        var lines = (await csv.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("device,branch,operating_system,availability_percent", lines[0], StringComparison.Ordinal);
        Assert.Equal(3, lines.Length);

        await HttpAssert.ProblemAsync(await admin.GetAsync("/api/v1/monitoring/availability?from=2026-01-10&to=2026-01-01"), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Retired_devices_are_not_monitored()
    {
        var (_, device, agent, admin) = await EnrollAsync();
        await agent.HeartbeatAsync();
        await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.PostAsync($"/api/v1/devices/{device.DeviceId}/retire", null));
        await StepAsync(TimeSpan.FromMinutes(6));

        Assert.Empty(await OpenAlertsAsync(admin));
        Assert.DoesNotContain(await EventsAsync(admin, device.DeviceId), e => e.Type == ConnectionEventType.WentOffline);
        var summary = await HttpAssert.OkAsync<MonitoringSummaryDto>(await admin.GetAsync("/api/v1/monitoring/summary"));
        Assert.Equal(0, summary.Devices.Total);
        Assert.Equal(1, summary.RetiredDevices);
    }

    // --------------------------------------------------------- live + isolation

    [Fact]
    public async Task Live_updates_are_pushed_over_signalr_to_the_owning_tenant_only()
    {
        var (tenantA, device, agent, adminA) = await EnrollAsync();
        var tenantB = await fixture.RegisterTenantAsync();
        await using var hubA = await ConnectAsync(tenantA.Token);
        await using var hubB = await ConnectAsync(tenantB.Token);

        await agent.HeartbeatAsync();
        await StepAsync(TimeSpan.FromMinutes(6));
        var offline = await hubA.WaitForAsync(u => u.StatusChanges.Any(c => c.DeviceId == device.DeviceId && c.To == ConnectivityState.Offline));
        Assert.Contains(offline.Alerts, a => a.DeviceId == device.DeviceId && a.Status == MonitoringAlertStatus.Open);
        Assert.Contains(offline.Events, e => e.Type == ConnectionEventType.WentOffline && e.DeviceName == device.Identity.Hostname);

        await agent.HeartbeatAsync();
        var recovered = await hubA.WaitForAsync(u => u.StatusChanges.Any(c => c.DeviceId == device.DeviceId && c.To == ConnectivityState.Online));
        Assert.Contains(recovered.Alerts, a => a.DeviceId == device.DeviceId && a.Status == MonitoringAlertStatus.Resolved); // recovery notification
        Assert.Contains(recovered.Events, e => e.Type == ConnectionEventType.Recovered);

        Assert.DoesNotContain(hubB.Updates, u => u.StatusChanges.Any(c => c.DeviceId == device.DeviceId));

        // REST isolation as well.
        using var adminB = fixture.CreateClient(tenantB.Token);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/v1/monitoring/devices/{device.DeviceId}")).StatusCode);
        var eventsB = await HttpAssert.OkAsync<PagedResult<ConnectionEventDto>>(await adminB.GetAsync($"/api/v1/monitoring/events?deviceId={device.DeviceId}"));
        Assert.Empty(eventsB.Items);
        var alertsB = await HttpAssert.OkAsync<PagedResult<MonitoringAlertDto>>(await adminB.GetAsync("/api/v1/monitoring/alerts"));
        Assert.Empty(alertsB.Items);
        var alertId = (await HttpAssert.OkAsync<PagedResult<MonitoringAlertDto>>(await adminA.GetAsync("/api/v1/monitoring/alerts"))).Items[0].Id;
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PostAsync($"/api/v1/monitoring/alerts/{alertId}/acknowledge", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await adminA.PostAsync($"/api/v1/monitoring/alerts/{alertId}/acknowledge", null)).StatusCode);
    }

    [Fact]
    public async Task Hub_rejects_anonymous_connections()
    {
        var connection = BuildHub(token: null);
        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
        await connection.DisposeAsync();
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(TenantSession Tenant, EnrolledDevice Device, HttpClient Agent, HttpClient Admin)> EnrollAsync(TenantSession? tenant = null, HttpClient? admin = null)
    {
        tenant ??= await fixture.RegisterTenantAsync();
        admin ??= fixture.CreateClient(tenant.Token);
        var device = await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant));
        return (tenant, device, fixture.CreateDeviceClient(device.Certificate), admin);
    }

    /// <summary>Lets <paramref name="duration"/> pass: flush + sweep every 15 simulated seconds.</summary>
    private async Task StepAsync(TimeSpan duration)
    {
        var remaining = duration;
        while (remaining > TimeSpan.Zero)
        {
            var step = remaining < SweepStep ? remaining : SweepStep;
            Clock.Advance(step);
            remaining -= step;
            var result = await SweepAsync();
            Assert.False(result.OutageDetected, "simulated time must not look like a platform outage");
        }
    }

    private async Task StepWithHeartbeatsAsync(HttpClient agent, TimeSpan duration)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += TimeSpan.FromMinutes(1))
        {
            await StepAsync(TimeSpan.FromMinutes(1));
            await HttpAssert.OkAsync<HeartbeatResponse>(await agent.HeartbeatAsync());
        }
    }

    private Task<SweepResult> SweepAsync() => fixture.SweepAsync(Clock.UtcNow);

    private async Task ResetMonitorAsync()
    {
        await fixture.SweepAsync(); // ensures the checkpoint exists
        await fixture.ExecutePlatformSqlAsync(
            "UPDATE monitor_checkpoints SET last_sweep_at_utc = now(), observing_since_utc = now() - interval '30 days', evidence_floor_utc = now() - interval '30 days'");
    }

    private static async Task<ConnectivityState> StateAsync(HttpClient admin, Guid deviceId) =>
        (await DeviceMonitoringAsync(admin, deviceId)).Connectivity;

    private static Task<DeviceMonitoringDto> DeviceMonitoringAsync(HttpClient admin, Guid deviceId) =>
        HttpAssert.OkAsync<DeviceMonitoringDto>(admin.GetAsync($"/api/v1/monitoring/devices/{deviceId}").Result);

    private static async Task<IReadOnlyList<ConnectionEventDto>> EventsAsync(HttpClient admin, Guid deviceId) =>
        (await HttpAssert.OkAsync<PagedResult<ConnectionEventDto>>(await admin.GetAsync($"/api/v1/monitoring/events?deviceId={deviceId}"))).Items;

    private static async Task<IReadOnlyList<MonitoringAlertDto>> OpenAlertsAsync(HttpClient admin) =>
        (await HttpAssert.OkAsync<PagedResult<MonitoringAlertDto>>(await admin.GetAsync("/api/v1/monitoring/alerts?status=Open"))).Items;

    private AgentHealthReport Health(int failures = 0, DateTimeOffset? started = null, DateTimeOffset? booted = null, bool gatewayOk = true, double freeDiskRatio = 0.5) => new()
    {
        Status = failures > 0 ? AgentHealthStatus.Degraded : AgentHealthStatus.Healthy,
        StartedAtUtc = started ?? DeviceTestKit.AgentStartedAtUtc,
        ConsecutiveFailures = failures,
        LastSuccessfulContactAtUtc = Clock.UtcNow.AddMinutes(-1),
        SystemBootedAtUtc = booted,
        System = new SystemHealthSnapshot
        {
            TotalMemoryBytes = 16L << 30,
            AvailableMemoryBytes = 8L << 30,
            SystemDrive = "C:\\",
            SystemDriveTotalBytes = 100L << 30,
            SystemDriveFreeBytes = (long)((100L << 30) * freeDiskRatio),
            RebootPending = false,
        },
        Network = new NetworkReachabilitySnapshot
        {
            CheckedAtUtc = Clock.UtcNow,
            Checks =
            [
                new NetworkCheck { Kind = NetworkCheckKind.Interface, Target = "Ethernet", Success = true },
                new NetworkCheck { Kind = NetworkCheckKind.Gateway, Target = "10.0.0.1", Success = gatewayOk, Error = gatewayOk ? null : "TimedOut" },
                new NetworkCheck { Kind = NetworkCheckKind.Dns, Target = "agents.solvix.test", Success = true },
            ],
        },
    };

    private async Task<(DateTimeOffset LastSeen, int Revision, DateTimeOffset DeviceUpdated)> ReadRowAsync(Guid deviceId)
    {
        await using var connection = new Npgsql.NpgsqlConnection(fixture.Database.MigratorConnectionString);
        await connection.OpenAsync();
        await using (var bypass = new Npgsql.NpgsqlCommand("SELECT set_config('app.rls_bypass', 'on', false)", connection))
        {
            await bypass.ExecuteNonQueryAsync();
        }

        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT p.last_seen_at_utc, p.revision, d.updated_at_utc FROM device_presence p JOIN devices d ON d.id = p.id WHERE p.id = @d", connection);
        command.Parameters.AddWithValue("d", deviceId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetFieldValue<DateTimeOffset>(0), reader.GetInt32(1), reader.GetFieldValue<DateTimeOffset>(2));
    }

    private async Task<HttpClient> MemberClientAsync(TenantSession tenant)
    {
        using var owner = fixture.CreateClient(tenant.Token);
        var email = $"member-{Guid.NewGuid():N}@example.test";
        var add = await owner.PostAsJsonAsync(
            "/api/v1/tenant/members",
            new Solvix.Modules.Identity.Application.Members.AddMemberRequest(email, "Member", "Passw0rd!Secure", Solvix.SharedKernel.Identity.TenantRole.Member),
            ApiFixture.Json);
        Assert.True(add.IsSuccessStatusCode, await add.Content.ReadAsStringAsync());
        var member = await fixture.LoginAsync(email, "Passw0rd!Secure", tenant.Slug);
        return fixture.CreateClient(member.AccessToken);
    }

    private HubConnection BuildHub(string? token) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(fixture.Factory.Server.BaseAddress, MonitoringHub.Route.TrimStart('/')), options =>
            {
                options.HttpMessageHandlerFactory = _ => fixture.Factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult(token);
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();

    private async Task<LiveClient> ConnectAsync(string token)
    {
        var connection = BuildHub(token);
        var client = new LiveClient(connection);
        connection.On<MonitoringUpdateDto>(MonitoringHub.UpdateMethod, client.Updates.Enqueue);
        await connection.StartAsync();
        return client;
    }

    private sealed class LiveClient(HubConnection connection) : IAsyncDisposable
    {
        public ConcurrentQueue<MonitoringUpdateDto> Updates { get; } = new();

        public async Task<MonitoringUpdateDto> WaitForAsync(Func<MonitoringUpdateDto, bool> predicate)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                var match = Updates.FirstOrDefault(predicate);
                if (match is not null)
                {
                    return match;
                }

                await Task.Delay(50);
            }

            throw new TimeoutException("Expected live update was not received.");
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}
