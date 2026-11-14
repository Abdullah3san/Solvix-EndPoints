namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Solvix.Agent.Contracts;
using Solvix.Infrastructure.Fleet;
using Solvix.IntegrationTests.Fixtures;
using Solvix.SharedKernel.Authorization;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Fleet;
using Solvix.SharedKernel.Tenancy;

/// <summary>The AI assistant's fleet port backed by real device, presence, inventory and software data.</summary>
[Collection(ApiCollection.Name)]
public sealed class AssistantFleetAdapterTests(ApiFixture fixture)
{
    private static readonly IReadOnlySet<string> Everything = new HashSet<string>(PermissionCatalog.AllKeys, StringComparer.Ordinal);

    [Fact]
    public async Task Real_device_data_is_reported_with_permission_checks_and_tenant_isolation()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var device = await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant), DeviceTestKit.Identity(hostname: "FIN-PC-001"));
        using var agent = fixture.CreateDeviceClient(device.Certificate);

        var inventory = DeviceManagementApiTests.SampleInventory("FIN-PC-001") with
        {
            OperatingSystem = DeviceManagementApiTests.SampleInventory("FIN-PC-001").OperatingSystem! with { IsSupported = false },
            Volumes = [new VolumeInfo { DriveLetter = "C:", FileSystem = "NTFS", SizeBytes = 256L * 1024 * 1024 * 1024, FreeBytes = 5L * 1024 * 1024 * 1024, DriveType = "Fixed", BitLockerProtected = false }],
        };
        Assert.Equal(HttpStatusCode.NoContent, (await agent.PutAsJsonAsync("/" + AgentRoutes.Inventory, inventory, ApiFixture.Json)).StatusCode);
        var heartbeat = await agent.HeartbeatAsync(new AgentHealthReport
        {
            Status = AgentHealthStatus.Healthy,
            StartedAtUtc = DeviceTestKit.AgentStartedAtUtc,
            UptimeSeconds = 5,
            System = new SystemHealthSnapshot
            {
                TotalMemoryBytes = 16L * 1024 * 1024 * 1024,
                AvailableMemoryBytes = 4L * 1024 * 1024 * 1024,
                SystemDrive = "C:",
                SystemDriveTotalBytes = 256L * 1024 * 1024 * 1024,
                SystemDriveFreeBytes = 5L * 1024 * 1024 * 1024,
                RebootPending = true,
            },
        });
        Assert.True(heartbeat.IsSuccessStatusCode);
        await fixture.FlushPresenceAsync();

        var scopes = fixture.Factory.Services.GetRequiredService<ITenantScopeFactory>();
        await using (var scope = scopes.CreateScope(tenant.TenantId))
        {
            var fleet = scope.ServiceProvider.GetRequiredService<IFleetQueryService>();
            Assert.IsType<PlatformFleetQueryService>(fleet);
            Assert.True(fleet.IsAvailable);
            var access = new FleetAccess(tenant.TenantId, tenant.Auth.User.Id, Everything);

            var summary = await fleet.GetSummaryAsync(access, new FleetScope(), default);
            Assert.Equal(1, summary.Total);
            Assert.Equal(1, summary.Online);
            Assert.Equal(1, summary.LowStorage);

            var (items, total) = await fleet.SearchAsync(access, new DeviceSearchCriteria { Text = "fin-pc" }, 0, 10, default);
            var found = Assert.Single(items);
            Assert.Equal(1, total);
            Assert.Equal(device.DeviceId, found.Id);
            Assert.Equal(DeviceConnectivity.Online, found.Status);
            Assert.Equal(75.0, found.MemoryUsagePercent);
            Assert.Equal(5L * 1024 * 1024 * 1024, found.FreeStorageBytes);
            Assert.Null(found.CpuUsagePercent);

            var resolution = await fleet.ResolveAsync(access, ["FIN-PC-001", "NOPE-PC"], default);
            Assert.Equal(device.DeviceId, Assert.Single(resolution.Devices).Id);
            Assert.Equal(["NOPE-PC"], resolution.NotFound);

            var detail = await fleet.GetDetailAsync(access, device.DeviceId, default);
            Assert.NotNull(detail);
            Assert.True(detail.PendingReboot);
            Assert.Equal("1.2.3", detail.BiosVersion);
            Assert.Equal(10, detail.CpuCores);
            Assert.False(Assert.Single(detail.Disks).Encrypted);

            Assert.Null(await fleet.GetPerformanceAsync(access, device.DeviceId, TimeSpan.FromHours(1), default));

            var posture = await fleet.GetSecurityPostureAsync(access, new FleetScope(), default);
            Assert.Equal(1, posture.TotalDevices);
            Assert.Contains(posture.Findings, f => f.Kind == SecurityFindingKind.OsUnsupported && f.DeviceId == device.DeviceId);
            Assert.Contains(posture.Findings, f => f.Kind == SecurityFindingKind.DiskNotEncrypted);
            Assert.Contains(posture.Findings, f => f.Kind == SecurityFindingKind.PendingReboot);
            Assert.DoesNotContain(posture.Findings, f => f.Kind == SecurityFindingKind.StaleAgent);

            var software = await fleet.GetInstalledSoftwareAsync(access, device.DeviceId, null, default);
            Assert.NotNull(software);
            Assert.Contains(software, s => s.Name == "Microsoft Edge" && s.Version == "130.0.2849.80");
            var fleetSoftware = await fleet.FindSoftwareAsync(access, new SoftwareSearchCriteria("7-Zip", null, false, new FleetScope()), 10, default);
            Assert.Equal(1, Assert.Single(fleetSoftware).DeviceCount);

            // The caller's own permissions are enforced by the port, not only by the assistant.
            var noDevices = new FleetAccess(tenant.TenantId, tenant.Auth.User.Id, new HashSet<string>([Permissions.Assistant.Use], StringComparer.Ordinal));
            await Assert.ThrowsAnyAsync<ForbiddenException>(() => fleet.GetSummaryAsync(noDevices, new FleetScope(), default));
            var devicesOnly = new FleetAccess(tenant.TenantId, tenant.Auth.User.Id, new HashSet<string>([Permissions.Devices.View], StringComparer.Ordinal));
            await Assert.ThrowsAnyAsync<ForbiddenException>(() => fleet.GetInstalledSoftwareAsync(devicesOnly, device.DeviceId, null, default));
        }

        // Another tenant sees nothing, even when it asks for this tenant's device by id.
        var other = await fixture.RegisterTenantAsync();
        await using (var scope = scopes.CreateScope(other.TenantId))
        {
            var fleet = scope.ServiceProvider.GetRequiredService<IFleetQueryService>();
            var access = new FleetAccess(other.TenantId, other.Auth.User.Id, Everything);
            Assert.Equal(0, (await fleet.GetSummaryAsync(access, new FleetScope(), default)).Total);
            Assert.Null(await fleet.GetDetailAsync(access, device.DeviceId, default));
            Assert.Equal(["FIN-PC-001"], (await fleet.ResolveAsync(access, ["FIN-PC-001"], default)).NotFound);
        }
    }

    [Theory]
    [InlineData("10.0.19045", "10.0.22631", -1)]
    [InlineData("24.08", "24.8", 0)]
    [InlineData("130.0.2849.80", "130.0.2849.79", 1)]
    [InlineData("1.2", "1.2.0.0", 0)]
    [InlineData("beta", "1.0", 0)]
    public void Versions_compare_numerically(string left, string right, int expected) =>
        Assert.Equal(expected, Math.Sign(PlatformFleetQueryService.VersionComparer.Instance.Compare(left, right)));
}
