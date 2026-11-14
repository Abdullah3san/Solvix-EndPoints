namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using Npgsql;
using Solvix.Agent.Contracts;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Devices.Application.Devices;
using Solvix.Modules.Devices.Application.Enrollment;
using Solvix.Modules.Devices.Application.Groups;
using Solvix.Modules.Organization.Application.Branches;
using Solvix.Modules.Organization.Application.Departments;
using Solvix.SharedKernel.Results;

/// <summary>Inventory, disconnection, groups, assignment and tenant isolation of device data.</summary>
[Collection(ApiCollection.Name)]
public sealed class DeviceManagementApiTests(ApiFixture fixture)
{
    [Fact]
    public async Task Inventory_is_stored_and_summarised_on_the_device()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var device = await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant));
        using var agent = fixture.CreateDeviceClient(device.Certificate);

        var report = SampleInventory(device.Identity.Hostname);
        Assert.Equal(HttpStatusCode.NoContent, (await agent.PutAsJsonAsync("/" + AgentRoutes.Inventory, report, ApiFixture.Json)).StatusCode);

        using var admin = fixture.CreateClient(tenant.Token);
        var details = await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.GetAsync($"/api/v1/devices/{device.DeviceId}"));
        Assert.Equal("Windows 11 Pro", details.Summary.OsName);
        Assert.Equal("23H2", details.Summary.OsDisplayVersion);
        Assert.Equal("22631.4460", details.Summary.OsBuild);
        Assert.Equal("Intel(R) Core(TM) i7-1365U", details.ProcessorName);
        Assert.Equal(16L * 1024 * 1024 * 1024, details.TotalMemoryBytes);
        Assert.Equal(["10.0.0.15", "fe80::1"], details.Summary.IpAddresses);
        Assert.Equal(["00:11:22:33:44:55"], details.MacAddresses);
        Assert.NotNull(details.Summary.LastInventoryAtUtc);
        Assert.NotNull(details.LastBootAtUtc);

        var inventory = await HttpAssert.OkAsync<DeviceInventoryDto>(await admin.GetAsync($"/api/v1/devices/{device.DeviceId}/inventory"));
        Assert.Equal(2, inventory.Report.Software.Count);
        Assert.Equal("7-Zip 24.08 (x64)", inventory.Report.Software[0].Name);
        Assert.Equal("BIOS-SN-1", inventory.Report.Bios!.SerialNumber);
        Assert.Equal("NVMe", Assert.Single(inventory.Report.Disks).BusType);
        Assert.Equal("C:", Assert.Single(inventory.Report.Volumes).DriveLetter);

        // Search finds the device by IP address.
        var byIp = await HttpAssert.OkAsync<PagedResult<DeviceSummaryDto>>(await admin.GetAsync("/api/v1/devices?search=10.0.0.15"));
        Assert.Equal(device.DeviceId, Assert.Single(byIp.Items).Id);

        // A second upload replaces the first.
        Assert.Equal(HttpStatusCode.NoContent, (await agent.PutAsJsonAsync("/" + AgentRoutes.Inventory, report with { Software = [] }, ApiFixture.Json)).StatusCode);
        inventory = await HttpAssert.OkAsync<DeviceInventoryDto>(await admin.GetAsync($"/api/v1/devices/{device.DeviceId}/inventory"));
        Assert.Empty(inventory.Report.Software);
    }

    [Fact]
    public async Task Oversized_inventory_is_rejected()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var device = await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant));
        using var agent = fixture.CreateDeviceClient(device.Certificate);
        var tooMany = Enumerable.Range(0, InventoryReport.MaxSoftwareEntries + 1).Select(i => new InstalledApplication { Name = $"App {i}" }).ToList();
        await HttpAssert.ProblemAsync(
            await agent.PutAsJsonAsync("/" + AgentRoutes.Inventory, SampleInventory("HOST") with { Software = tooMany }, ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Admin_inventory_request_is_signalled_on_heartbeat_until_inventory_arrives()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var device = await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant));
        using var agent = fixture.CreateDeviceClient(device.Certificate);
        using var admin = fixture.CreateClient(tenant.Token);

        Assert.False((await HttpAssert.OkAsync<HeartbeatResponse>(await agent.HeartbeatAsync())).InventoryRequested);
        var requested = await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.PostAsync($"/api/v1/devices/{device.DeviceId}/inventory-request", null));
        Assert.True(requested.InventoryRequested);
        Assert.True((await HttpAssert.OkAsync<HeartbeatResponse>(await agent.HeartbeatAsync())).InventoryRequested);

        await agent.PutAsJsonAsync("/" + AgentRoutes.Inventory, SampleInventory(device.Identity.Hostname), ApiFixture.Json);
        Assert.False((await HttpAssert.OkAsync<HeartbeatResponse>(await agent.HeartbeatAsync())).InventoryRequested);
    }

    [Fact]
    public async Task Device_goes_offline_after_missed_heartbeats_and_comes_back_on_reconnect()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var device = await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant));
        using var admin = fixture.CreateClient(tenant.Token);

        var online = await HttpAssert.OkAsync<PagedResult<DeviceSummaryDto>>(await admin.GetAsync("/api/v1/devices?connectivity=Online"));
        Assert.Contains(online.Items, d => d.Id == device.DeviceId);

        // Agent disconnected: no contact for longer than the offline threshold.
        await fixture.ExecutePlatformSqlAsync("UPDATE devices SET last_seen_at_utc = now() - interval '1 hour' WHERE id = @d", ("d", device.DeviceId));
        var details = await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.GetAsync($"/api/v1/devices/{device.DeviceId}"));
        Assert.Equal(DeviceConnectivity.Offline, details.Summary.Connectivity);
        var offline = await HttpAssert.OkAsync<PagedResult<DeviceSummaryDto>>(await admin.GetAsync("/api/v1/devices?connectivity=Offline"));
        Assert.Contains(offline.Items, d => d.Id == device.DeviceId);
        var stats = await HttpAssert.OkAsync<DeviceStatsDto>(await admin.GetAsync("/api/v1/devices/stats"));
        Assert.Equal(1, stats.Offline);
        Assert.Equal(0, stats.Online);

        // The agent reconnects with the same credential.
        using var agent = fixture.CreateDeviceClient(device.Certificate);
        using var reconnect = new HttpRequestMessage(HttpMethod.Post, "/" + AgentRoutes.Heartbeat)
        {
            Content = JsonContent.Create(
                new HeartbeatRequest
                {
                    AgentVersion = DeviceTestKit.AgentVersion,
                    Health = new AgentHealthReport { Status = AgentHealthStatus.Degraded, ConsecutiveFailures = 7, Issues = [new HealthIssue { Component = "communication", Message = "Server unreachable for 58 minutes" }] },
                },
                options: ApiFixture.Json),
        };
        await HttpAssert.OkAsync<HeartbeatResponse>(await agent.SendAsync(reconnect));
        details = await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.GetAsync($"/api/v1/devices/{device.DeviceId}"));
        Assert.Equal(DeviceConnectivity.Online, details.Summary.Connectivity);
        Assert.Equal(AgentHealthStatus.Degraded, details.Summary.HealthStatus);
        Assert.Equal(7, details.Health!.ConsecutiveFailures);
        Assert.Equal("communication", Assert.Single(details.Health.Issues).Component);
    }

    [Fact]
    public async Task Groups_can_be_managed_and_assigned()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var token = await fixture.CreateTokenAsync(tenant, maxUses: 3);
        var d1 = await fixture.EnrollAsync(token);
        var d2 = await fixture.EnrollAsync(token);
        using var admin = fixture.CreateClient(tenant.Token);

        var finance = await HttpAssert.OkAsync<DeviceGroupDto>(
            await admin.PostAsJsonAsync("/api/v1/device-groups", new CreateDeviceGroupRequest("Finance", "Finance laptops"), ApiFixture.Json), HttpStatusCode.Created);
        var servers = await HttpAssert.OkAsync<DeviceGroupDto>(
            await admin.PostAsJsonAsync("/api/v1/device-groups", new CreateDeviceGroupRequest("Servers", null), ApiFixture.Json), HttpStatusCode.Created);
        await HttpAssert.ProblemAsync(
            await admin.PostAsJsonAsync("/api/v1/device-groups", new CreateDeviceGroupRequest("FINANCE", null), ApiFixture.Json), HttpStatusCode.Conflict);

        var withDevices = await HttpAssert.OkAsync<DeviceGroupDto>(
            await admin.PostAsJsonAsync($"/api/v1/device-groups/{finance.Id}/devices", new AddGroupDevicesRequest([d1.DeviceId, d2.DeviceId]), ApiFixture.Json));
        Assert.Equal(2, withDevices.DeviceCount);
        // Idempotent.
        withDevices = await HttpAssert.OkAsync<DeviceGroupDto>(
            await admin.PostAsJsonAsync($"/api/v1/device-groups/{finance.Id}/devices", new AddGroupDevicesRequest([d1.DeviceId]), ApiFixture.Json));
        Assert.Equal(2, withDevices.DeviceCount);

        var details = await HttpAssert.OkAsync<DeviceDetailsDto>(
            await admin.PutAsJsonAsync($"/api/v1/devices/{d1.DeviceId}/groups", new SetDeviceGroupsRequest([servers.Id]), ApiFixture.Json));
        Assert.Equal("Servers", Assert.Single(details.Groups).Name);

        var removed = await HttpAssert.OkAsync<DeviceGroupDto>(await admin.DeleteAsync($"/api/v1/device-groups/{finance.Id}/devices/{d2.DeviceId}"));
        Assert.Equal(0, removed.DeviceCount);

        var renamed = await HttpAssert.OkAsync<DeviceGroupDto>(
            await admin.PutAsJsonAsync($"/api/v1/device-groups/{servers.Id}", new UpdateDeviceGroupRequest("Data centre", "racks"), ApiFixture.Json));
        Assert.Equal("Data centre", renamed.Name);

        // Deleting a group referenced by an enrollment token detaches the token and drops memberships.
        var created = await HttpAssert.OkAsync<CreatedEnrollmentTokenDto>(
            await admin.PostAsJsonAsync("/api/v1/enrollment-tokens", new CreateEnrollmentTokenRequest("dc", 1, 1, servers.Id), ApiFixture.Json), HttpStatusCode.Created);
        Assert.Equal("Data centre", created.EnrollmentToken.DeviceGroupName);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/device-groups/{servers.Id}")).StatusCode);
        details = await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.GetAsync($"/api/v1/devices/{d1.DeviceId}"));
        Assert.Empty(details.Groups);
        var tokens = await HttpAssert.OkAsync<PagedResult<EnrollmentTokenDto>>(await admin.GetAsync("/api/v1/enrollment-tokens"));
        Assert.Null(tokens.Items.Single(t => t.Id == created.EnrollmentToken.Id).DeviceGroupId);
    }

    [Fact]
    public async Task Devices_can_be_assigned_to_members_branches_and_departments()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var device = await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant));
        using var admin = fixture.CreateClient(tenant.Token);
        var hq = Assert.Single((await HttpAssert.OkAsync<PagedResult<BranchDto>>(await admin.GetAsync("/api/v1/branches"))).Items);
        var dept = await HttpAssert.OkAsync<DepartmentDto>(
            await admin.PostAsJsonAsync("/api/v1/departments", new CreateDepartmentRequest(hq.Id, null, "IT", "IT", null), ApiFixture.Json), HttpStatusCode.Created);

        var assigned = await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.PutAsJsonAsync(
            $"/api/v1/devices/{device.DeviceId}/assignment",
            new UpdateDeviceAssignmentRequest(tenant.Auth.User.Id, null, dept.Id),
            ApiFixture.Json));
        Assert.Equal(tenant.OwnerEmail, assigned.Assignment.User!.Email);
        Assert.True(assigned.Assignment.User.IsMember);
        Assert.Equal(hq.Id, assigned.Assignment.Branch!.Id); // derived from the department
        Assert.Equal("IT", assigned.Assignment.Department!.Name);

        var mine = await HttpAssert.OkAsync<PagedResult<DeviceSummaryDto>>(await admin.GetAsync($"/api/v1/devices?assignedUserId={tenant.Auth.User.Id}"));
        Assert.Single(mine.Items);

        // Deleting the department clears only that column (database-level SET NULL).
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/departments/{dept.Id}")).StatusCode);
        var after = await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.GetAsync($"/api/v1/devices/{device.DeviceId}"));
        Assert.Null(after.Assignment.Department);
        Assert.Equal(hq.Id, after.Assignment.Branch!.Id);

        var cleared = await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.PutAsJsonAsync(
            $"/api/v1/devices/{device.DeviceId}/assignment", new UpdateDeviceAssignmentRequest(null, null, null), ApiFixture.Json));
        Assert.Null(cleared.Assignment.User);
        Assert.Null(cleared.Assignment.Branch);

        var renamed = await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.PutAsJsonAsync(
            $"/api/v1/devices/{device.DeviceId}", new UpdateDeviceRequest("Reception PC", "Front desk"), ApiFixture.Json));
        Assert.Equal("Reception PC", renamed.Summary.DisplayName);
        Assert.Equal("Front desk", renamed.Notes);
    }

    [Fact]
    public async Task Tenant_cannot_see_or_touch_another_tenants_devices_groups_or_tokens()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();
        var deviceA = await fixture.EnrollAsync(await fixture.CreateTokenAsync(a));
        await fixture.EnrollAsync(await fixture.CreateTokenAsync(b));
        using var clientA = fixture.CreateClient(a.Token);
        using var clientB = fixture.CreateClient(b.Token);

        var groupA = await HttpAssert.OkAsync<DeviceGroupDto>(
            await clientA.PostAsJsonAsync("/api/v1/device-groups", new CreateDeviceGroupRequest("A-only", null), ApiFixture.Json), HttpStatusCode.Created);
        var groupB = await HttpAssert.OkAsync<DeviceGroupDto>(
            await clientB.PostAsJsonAsync("/api/v1/device-groups", new CreateDeviceGroupRequest("B-only", null), ApiFixture.Json), HttpStatusCode.Created);
        var tokenA = await HttpAssert.OkAsync<CreatedEnrollmentTokenDto>(
            await clientA.PostAsJsonAsync("/api/v1/enrollment-tokens", new CreateEnrollmentTokenRequest("a", 1, 1, null), ApiFixture.Json), HttpStatusCode.Created);

        // Listing: B sees only its own data.
        var listB = await HttpAssert.OkAsync<PagedResult<DeviceSummaryDto>>(await clientB.GetAsync("/api/v1/devices"));
        Assert.DoesNotContain(listB.Items, d => d.Id == deviceA.DeviceId);
        Assert.Single(listB.Items);
        Assert.DoesNotContain((await HttpAssert.OkAsync<PagedResult<DeviceGroupDto>>(await clientB.GetAsync("/api/v1/device-groups"))).Items, g => g.Id == groupA.Id);
        Assert.DoesNotContain((await HttpAssert.OkAsync<PagedResult<EnrollmentTokenDto>>(await clientB.GetAsync("/api/v1/enrollment-tokens"))).Items, t => t.Id == tokenA.EnrollmentToken.Id);
        Assert.Equal(1, (await HttpAssert.OkAsync<DeviceStatsDto>(await clientB.GetAsync("/api/v1/devices/stats"))).Total);

        // Direct access by id: 404 everywhere.
        var id = deviceA.DeviceId;
        await HttpAssert.ProblemAsync(await clientB.GetAsync($"/api/v1/devices/{id}"), HttpStatusCode.NotFound, "not_found");
        await HttpAssert.ProblemAsync(await clientB.GetAsync($"/api/v1/devices/{id}/inventory"), HttpStatusCode.NotFound, "not_found");
        await HttpAssert.ProblemAsync(await clientB.PutAsJsonAsync($"/api/v1/devices/{id}", new UpdateDeviceRequest("pwned", null), ApiFixture.Json), HttpStatusCode.NotFound, "not_found");
        await HttpAssert.ProblemAsync(await clientB.PostAsync($"/api/v1/devices/{id}/retire", null), HttpStatusCode.NotFound, "not_found");
        await HttpAssert.ProblemAsync(await clientB.PostAsync($"/api/v1/devices/{id}/inventory-request", null), HttpStatusCode.NotFound, "not_found");
        await HttpAssert.ProblemAsync(await clientB.DeleteAsync($"/api/v1/devices/{id}"), HttpStatusCode.NotFound, "not_found");
        await HttpAssert.ProblemAsync(await clientB.GetAsync($"/api/v1/device-groups/{groupA.Id}"), HttpStatusCode.NotFound, "not_found");
        await HttpAssert.ProblemAsync(await clientB.DeleteAsync($"/api/v1/device-groups/{groupA.Id}"), HttpStatusCode.NotFound, "not_found");
        await HttpAssert.ProblemAsync(await clientB.PostAsync($"/api/v1/enrollment-tokens/{tokenA.EnrollmentToken.Id}/revoke", null), HttpStatusCode.NotFound, "not_found");

        // Cross-tenant references: B cannot pull A's device into B's group, or use A's group/branch/user.
        await HttpAssert.ProblemAsync(
            await clientB.PostAsJsonAsync($"/api/v1/device-groups/{groupB.Id}/devices", new AddGroupDevicesRequest([id]), ApiFixture.Json), HttpStatusCode.NotFound, "not_found");
        await HttpAssert.ProblemAsync(
            await clientB.PostAsJsonAsync("/api/v1/enrollment-tokens", new CreateEnrollmentTokenRequest("x", 1, 1, groupA.Id), ApiFixture.Json), HttpStatusCode.NotFound, "not_found");
        var deviceB = Assert.Single(listB.Items).Id;
        await HttpAssert.ProblemAsync(
            await clientB.PutAsJsonAsync($"/api/v1/devices/{deviceB}/groups", new SetDeviceGroupsRequest([groupA.Id]), ApiFixture.Json), HttpStatusCode.NotFound, "not_found");
        var hqA = Assert.Single((await HttpAssert.OkAsync<PagedResult<BranchDto>>(await clientA.GetAsync("/api/v1/branches"))).Items);
        await HttpAssert.ProblemAsync(
            await clientB.PutAsJsonAsync($"/api/v1/devices/{deviceB}/assignment", new UpdateDeviceAssignmentRequest(null, hqA.Id, null), ApiFixture.Json), HttpStatusCode.NotFound, "not_found");
        await HttpAssert.ProblemAsync(
            await clientB.PutAsJsonAsync($"/api/v1/devices/{deviceB}/assignment", new UpdateDeviceAssignmentRequest(a.Auth.User.Id, null, null), ApiFixture.Json), HttpStatusCode.NotFound, "not_found");

        // A device certificate of tenant A re-targeted at tenant B (CA-signed forgery) finds nothing in B.
        using var forged = fixture.IssueDirect(b.TenantId, deviceA.DeviceId, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1), out _);
        using var forgedClient = fixture.CreateDeviceClient(forged);
        Assert.Equal(HttpStatusCode.Unauthorized, (await forgedClient.HeartbeatAsync()).StatusCode);

        // A is untouched.
        var stillThere = await HttpAssert.OkAsync<DeviceDetailsDto>(await clientA.GetAsync($"/api/v1/devices/{id}"));
        Assert.Null(stillThere.Summary.DisplayName);
        Assert.Equal(Solvix.Modules.Devices.Domain.DeviceStatus.Active, stillThere.Summary.Status);
    }

    [Fact]
    public async Task Device_rows_are_protected_by_row_level_security()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();
        var deviceA = await fixture.EnrollAsync(await fixture.CreateTokenAsync(a));

        await using var connection = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await connection.OpenAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false)", connection))
        {
            set.Parameters.AddWithValue("t", b.TenantId.ToString());
            await set.ExecuteNonQueryAsync();
        }

        foreach (var table in new[] { "devices", "device_credentials", "enrollment_tokens" })
        {
            await using var count = new NpgsqlCommand($"SELECT count(*) FROM {table} WHERE tenant_id = @a", connection);
            count.Parameters.AddWithValue("a", a.TenantId);
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
        }

        await using var update = new NpgsqlCommand("UPDATE devices SET hostname = 'pwned' WHERE id = @id", connection);
        update.Parameters.AddWithValue("id", deviceA.DeviceId);
        Assert.Equal(0, await update.ExecuteNonQueryAsync());
    }

    private static InventoryReport SampleInventory(string hostname) => new()
    {
        CollectedAtUtc = DateTimeOffset.UtcNow,
        AgentVersion = DeviceTestKit.AgentVersion,
        Hostname = hostname,
        ContentHash = new string('b', 64),
        System = new SystemInfo { Manufacturer = "Contoso", Model = "Book 15", SerialNumber = "SN-1", HardwareId = Guid.NewGuid().ToString(), DomainName = "corp.contoso.test", PartOfDomain = true },
        Bios = new BiosInfo { Manufacturer = "Contoso", Version = "1.2.3", SerialNumber = "BIOS-SN-1", FirmwareType = "UEFI", SecureBootEnabled = true },
        OperatingSystem = new OperatingSystemInfo
        {
            Name = "Windows 11 Pro",
            Family = "Windows 11",
            Edition = "Professional",
            Version = "10.0.22631",
            DisplayVersion = "23H2",
            BuildNumber = "22631",
            UpdateBuildRevision = 4460,
            Architecture = "x64",
            ProductType = "Workstation",
            IsSupported = true,
            LastBootUpTimeUtc = DateTimeOffset.UtcNow.AddHours(-3),
        },
        Processors = [new ProcessorInfo { Name = "Intel(R) Core(TM) i7-1365U", Cores = 10, LogicalProcessors = 12, MaxClockSpeedMhz = 1800 }],
        Memory = new MemoryInfo { TotalPhysicalBytes = 16L * 1024 * 1024 * 1024, AvailablePhysicalBytes = 8L * 1024 * 1024 * 1024, Modules = [new MemoryModuleInfo { CapacityBytes = 16L * 1024 * 1024 * 1024, SpeedMhz = 5200 }] },
        Disks = [new PhysicalDiskInfo { Model = "Contoso NVMe 512", SizeBytes = 512L * 1000 * 1000 * 1000, MediaType = "SSD", BusType = "NVMe" }],
        Volumes = [new VolumeInfo { DriveLetter = "C:", FileSystem = "NTFS", SizeBytes = 500L * 1000 * 1000 * 1000, FreeBytes = 200L * 1000 * 1000 * 1000, DriveType = "Fixed" }],
        NetworkAdapters =
        [
            new NetworkAdapterInfo { Name = "Ethernet", MacAddress = "00:11:22:33:44:55", Status = "Up", IsPhysical = true, IPv4Addresses = ["10.0.0.15"], IPv6Addresses = ["fe80::1"] },
            new NetworkAdapterInfo { Name = "vEthernet", MacAddress = "00:15:5D:00:00:01", Status = "Down", IsPhysical = false, IPv4Addresses = ["172.20.0.1"] },
        ],
        Software =
        [
            new InstalledApplication { Name = "7-Zip 24.08 (x64)", Version = "24.08", Publisher = "Igor Pavlov", Scope = "x64" },
            new InstalledApplication { Name = "Microsoft Edge", Version = "130.0.2849.80", Publisher = "Microsoft Corporation", Scope = "x86" },
        ],
    };
}
