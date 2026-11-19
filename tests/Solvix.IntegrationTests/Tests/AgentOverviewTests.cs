namespace Solvix.IntegrationTests.Tests;

using System.Net.Http.Json;
using Solvix.Agent.Contracts;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Devices.Application.Devices;
using Solvix.Modules.Devices.Application.Insights;
using Solvix.Modules.Devices.Application.Staged;
using Solvix.Modules.Organization.Application.Branches;
using Solvix.SharedKernel.Results;

/// <summary>The agent overview (fleet breakdowns), the derived alerts and the device list filters behind them.</summary>
[Collection(ApiCollection.Name)]
public sealed class AgentOverviewTests(ApiFixture fixture)
{
    private async Task<EnrolledDevice> EnrollAsync(TenantSession tenant, string? hostname = null, string? osName = null)
    {
        var identity = DeviceTestKit.Identity(hostname: hostname) with { OsName = osName ?? "Windows 11 Pro" };
        return await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant), identity);
    }

    private async Task HeartbeatAsync(EnrolledDevice device, AgentHealthStatus health, string? version = null)
    {
        using var agent = fixture.CreateDeviceClient(device.Certificate);
        var response = await agent.PostAsJsonAsync(
            "/" + AgentRoutes.Heartbeat,
            new HeartbeatRequest { AgentVersion = version ?? DeviceTestKit.AgentVersion, Health = new AgentHealthReport { Status = health, StartedAtUtc = DateTimeOffset.UtcNow, UptimeSeconds = 5 } },
            ApiFixture.Json);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Overview_breaks_the_active_fleet_down_by_platform_os_health_and_branch()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var hq = Assert.Single((await HttpAssert.OkAsync<PagedResult<BranchDto>>(await admin.GetAsync("/api/v1/branches"))).Items);

        var empty = await HttpAssert.OkAsync<DeviceOverviewDto>(await admin.GetAsync("/api/v1/devices/overview"));
        Assert.Empty(empty.Platforms);
        Assert.Empty(empty.Branches);

        var d1 = await EnrollAsync(tenant, osName: "Windows 11 Pro");
        var d2 = await EnrollAsync(tenant, osName: "Windows 11 Pro");
        var d3 = await EnrollAsync(tenant, osName: "Windows Server 2022 Standard");
        var retired = await EnrollAsync(tenant, osName: "Windows 10 Pro");
        await HeartbeatAsync(d1, AgentHealthStatus.Healthy);
        await HeartbeatAsync(d2, AgentHealthStatus.Degraded);
        await HeartbeatAsync(d3, AgentHealthStatus.Unhealthy);
        await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.PutAsJsonAsync($"/api/v1/devices/{d1.DeviceId}/assignment", new UpdateDeviceAssignmentRequest(null, hq.Id, null), ApiFixture.Json));
        await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.PutAsJsonAsync($"/api/v1/devices/{d2.DeviceId}/assignment", new UpdateDeviceAssignmentRequest(null, hq.Id, null), ApiFixture.Json));
        await fixture.ExecutePlatformSqlAsync("UPDATE devices SET last_seen_at_utc = now() - interval '2 days' WHERE id = @d", ("d", d2.DeviceId));
        await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.PostAsync($"/api/v1/devices/{retired.DeviceId}/retire", null));

        var overview = await HttpAssert.OkAsync<DeviceOverviewDto>(await admin.GetAsync("/api/v1/devices/overview"));
        Assert.Equal([new NamedCountDto("Windows", "Windows", 3)], overview.Platforms); // the retired device is not counted
        Assert.Equal([("Windows 11 Pro", 2), ("Windows Server 2022 Standard", 1)], overview.OperatingSystems.Select(o => (o.Label, o.Count)));
        Assert.Equal([("Healthy", 1), ("Degraded", 1), ("Unhealthy", 1)], overview.Health.Select(h => (h.Key, h.Count)));

        // d1 is online and d2 has been silent for two days; both are in the headquarters, d3 has no branch (listed last).
        var headquarters = overview.Branches[0];
        Assert.Equal((hq.Id, hq.Name, 2, 1, 1), (headquarters.BranchId, headquarters.Name, headquarters.Total, headquarters.Online, headquarters.Offline));
        var unassigned = overview.Branches[^1];
        Assert.Equal((null, "No branch", 1), ((Guid?)unassigned.BranchId, unassigned.Name, unassigned.Total));
    }

    [Fact]
    public async Task Alerts_are_derived_from_live_data_and_link_to_the_affected_devices()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        Assert.Empty(await HttpAssert.OkAsync<List<DeviceAlertDto>>(await admin.GetAsync("/api/v1/devices/alerts")));

        var silent = await EnrollAsync(tenant, "SILENT-PC");
        var unhealthy = await EnrollAsync(tenant, "UNHEALTHY-PC");
        var degraded = await EnrollAsync(tenant, "DEGRADED-PC");
        var expiring = await EnrollAsync(tenant, "EXPIRING-PC");
        var expired = await EnrollAsync(tenant, "EXPIRED-PC");
        var current = await EnrollAsync(tenant, "CURRENT-PC");
        foreach (var d in new[] { silent, unhealthy, degraded, expiring, expired, current })
        {
            await HeartbeatAsync(d, d == unhealthy ? AgentHealthStatus.Unhealthy : d == degraded ? AgentHealthStatus.Degraded : AgentHealthStatus.Healthy, version: SolvixApiFactory.LatestAgentVersion);
        }

        await fixture.ExecutePlatformSqlAsync("UPDATE devices SET last_seen_at_utc = now() - interval '30 hours' WHERE id = @d", ("d", silent.DeviceId));
        await fixture.ExecutePlatformSqlAsync("UPDATE device_credentials SET not_after_utc = now() + interval '3 days' WHERE device_id = @d", ("d", expiring.DeviceId));
        await fixture.ExecutePlatformSqlAsync("UPDATE device_credentials SET not_before_utc = now() - interval '10 days', not_after_utc = now() - interval '1 hour' WHERE device_id = @d", ("d", expired.DeviceId));
        // A renewed device: the old certificate is about to expire but a newer one is already issued.
        await fixture.ExecutePlatformSqlAsync("UPDATE device_credentials SET not_after_utc = now() + interval '2 days' WHERE device_id = @d", ("d", current.DeviceId));
        await fixture.ExecutePlatformSqlAsync(
            """
            INSERT INTO device_credentials (id, tenant_id, device_id, thumbprint, serial_number, not_before_utc, not_after_utc, created_at_utc, updated_at_utc)
            SELECT gen_random_uuid(), tenant_id, device_id, 'renewed-' || md5(random()::text), 'renewed-' || md5(random()::text),
                   now(), now() + interval '90 days', now(), now()
            FROM device_credentials WHERE device_id = @d LIMIT 1
            """,
            ("d", current.DeviceId));
        await admin.PostAsJsonAsync("/api/v1/staged-computers", new CreateStagedComputerRequest("NOT-INSTALLED-1", null, null), ApiFixture.Json);
        await admin.PostAsJsonAsync("/api/v1/staged-computers", new CreateStagedComputerRequest("NOT-INSTALLED-2", null, null), ApiFixture.Json);

        var alerts = await HttpAssert.OkAsync<List<DeviceAlertDto>>(await admin.GetAsync("/api/v1/devices/alerts"));
        DeviceAlertDto Alert(string code) => Assert.Single(alerts, a => a.Code == code);

        Assert.Equal(["certificate_expired", "agent_unhealthy", "not_reporting", "agent_degraded", "certificate_expiring", "staged_pending"], alerts.Select(a => a.Code));
        Assert.Equal([AlertSeverity.Critical, AlertSeverity.Critical, AlertSeverity.Warning, AlertSeverity.Warning, AlertSeverity.Warning, AlertSeverity.Info], alerts.Select(a => a.Severity));
        Assert.Equal("EXPIRED-PC", Assert.Single(Alert("certificate_expired").Devices).Name);
        Assert.Equal("UNHEALTHY-PC", Assert.Single(Alert("agent_unhealthy").Devices).Name);
        Assert.Equal("SILENT-PC", Assert.Single(Alert("not_reporting").Devices).Name);
        Assert.Equal("DEGRADED-PC", Assert.Single(Alert("agent_degraded").Devices).Name);
        Assert.Equal("EXPIRING-PC", Assert.Single(Alert("certificate_expiring").Devices).Name); // CURRENT-PC was renewed and is not flagged
        Assert.Equal(2, Alert("staged_pending").Count);
        Assert.DoesNotContain(alerts, a => a.Code == "agent_outdated"); // everyone runs the latest release

        // Agents on an older release raise an informational alert, and retired devices raise nothing.
        await EnrollAsync(tenant, "OLD-AGENT-PC");
        var retired = await EnrollAsync(tenant, "RETIRED-PC");
        await HeartbeatAsync(retired, AgentHealthStatus.Unhealthy);
        await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.PostAsync($"/api/v1/devices/{retired.DeviceId}/retire", null));
        alerts = await HttpAssert.OkAsync<List<DeviceAlertDto>>(await admin.GetAsync("/api/v1/devices/alerts"));
        Assert.Equal("OLD-AGENT-PC", Assert.Single(Alert("agent_outdated").Devices).Name);
        Assert.Equal(1, Alert("agent_unhealthy").Count);

        // The same conditions are available as device list filters ("view all").
        async Task<List<string>> Hosts(string query) =>
            (await HttpAssert.OkAsync<PagedResult<DeviceSummaryDto>>(await admin.GetAsync("/api/v1/devices?" + query))).Items.Select(d => d.Hostname).ToList();
        Assert.Equal(["UNHEALTHY-PC"], await Hosts("health=Unhealthy"));
        Assert.Equal(["DEGRADED-PC"], await Hosts("health=Degraded"));
        Assert.Equal(["SILENT-PC"], await Hosts("notSeenHours=24"));
        Assert.Equal(["OLD-AGENT-PC"], await Hosts("updateAvailable=true"));
    }

    [Fact]
    public async Task Devices_report_their_domain_and_other_workspaces_see_nothing_of_it()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var other = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        using var stranger = fixture.CreateClient(other.Token);
        var device = await EnrollAsync(tenant, "DOMAIN-PC");
        await EnrollAsync(other, "OTHER-PC");
        await fixture.ExecutePlatformSqlAsync("UPDATE devices SET domain_name = 'corp.example.test' WHERE id = @d", ("d", device.DeviceId));

        var summary = Assert.Single((await HttpAssert.OkAsync<PagedResult<DeviceSummaryDto>>(await admin.GetAsync("/api/v1/devices"))).Items);
        Assert.Equal("corp.example.test", summary.DomainName);

        // Each workspace only ever sees its own fleet.
        var theirs = await HttpAssert.OkAsync<DeviceOverviewDto>(await stranger.GetAsync("/api/v1/devices/overview"));
        Assert.Equal(1, theirs.Platforms.Sum(p => p.Count));
        var theirAlerts = await HttpAssert.OkAsync<List<DeviceAlertDto>>(await stranger.GetAsync("/api/v1/devices/alerts"));
        Assert.Equal(["OTHER-PC"], theirAlerts.SelectMany(a => a.Devices).Select(d => d.Name).Distinct()); // their own outdated agent only
    }
}
