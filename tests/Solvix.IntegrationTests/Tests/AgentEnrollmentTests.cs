namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Solvix.Agent.Core.Api;
using Solvix.Endpoint.Contracts.Agent;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Devices.Application.Devices;
using Solvix.Modules.Organization.Application.Branches;
using Solvix.SharedKernel.Results;

[Collection(ApiCollection.Name)]
public sealed class AgentEnrollmentTests(ApiFixture fixture)
{
    [Fact]
    public async Task Enrolment_places_the_device_and_heartbeat_reports_inventory()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var hq = Assert.Single((await HttpAssert.OkAsync<PagedResult<BranchDto>>(await admin.GetAsync("/api/v1/branches"))).Items);
        var group = await HttpAssert.OkAsync<DeviceGroupDto>(
            await admin.PostAsJsonAsync("/api/v1/device-groups", new SaveDeviceGroupRequest("Pilot ring", null), ApiFixture.Json), HttpStatusCode.Created);
        var token = await fixture.CreateEnrollmentTokenAsync(tenant, branchId: hq.Id, groupId: group.Id);

        var device = await fixture.EnrollDeviceAsync(token, "LAPTOP-001");
        Assert.Equal(tenant.TenantId, device.TenantId);
        Assert.StartsWith(AgentAuth.DeviceCredentialPrefix, device.Credential, StringComparison.Ordinal);

        var listed = await HttpAssert.OkAsync<DeviceDto>(await admin.GetAsync($"/api/v1/devices/{device.DeviceId}"));
        Assert.Equal(hq.Id, listed.BranchId);
        Assert.Contains(group.Id, listed.GroupIds);
        Assert.Equal("Windows 11 Enterprise", listed.OsName);
        Assert.Equal("26100.2314", listed.OsBuild);

        var work = await device.HeartbeatAsync();
        Assert.Empty(work.PatchJobs);
        Assert.True(work.InventoryRequested, "A new device is asked for its first Windows Update scan.");
        Assert.False(string.IsNullOrEmpty(work.ConfigurationRevision));
    }

    [Fact]
    public async Task Enrolment_tokens_are_validated_limited_and_revocable()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var token = await fixture.CreateEnrollmentTokenAsync(tenant, maxUses: 1);
        await fixture.EnrollDeviceAsync(token, "PC-1");

        // Exhausted, malformed and tampered tokens are all rejected the same way.
        await Assert.ThrowsAsync<AgentUnauthorizedException>(() => fixture.EnrollDeviceAsync(token, "PC-2"));
        await Assert.ThrowsAsync<AgentUnauthorizedException>(() => fixture.EnrollDeviceAsync("not-a-token", "PC-3"));
        await Assert.ThrowsAsync<AgentUnauthorizedException>(() => fixture.EnrollDeviceAsync(token[..^4] + "AAAA", "PC-4"));

        var revocable = await fixture.CreateEnrollmentTokenAsync(tenant);
        using var admin = fixture.CreateClient(tenant.Token);
        var tokens = await HttpAssert.OkAsync<List<EnrollmentTokenDto>>(await admin.GetAsync("/api/v1/enrollment-tokens"));
        var newest = tokens.OrderByDescending(t => t.CreatedAtUtc).First();
        await HttpAssert.OkAsync<EnrollmentTokenDto>(await admin.PostAsync($"/api/v1/enrollment-tokens/{newest.Id}/revoke", null));
        await Assert.ThrowsAsync<AgentUnauthorizedException>(() => fixture.EnrollDeviceAsync(revocable, "PC-5"));
    }

    [Fact]
    public async Task Re_enrolment_with_the_same_hardware_id_reuses_the_device_and_revokes_the_old_credential()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var token = await fixture.CreateEnrollmentTokenAsync(tenant);
        var hardwareId = Guid.NewGuid().ToString();
        var first = await fixture.EnrollDeviceAsync(token, "PC-REIMAGED", hardwareId);
        var second = await fixture.EnrollDeviceAsync(token, "PC-REIMAGED", hardwareId);

        Assert.Equal(first.DeviceId, second.DeviceId);
        await Assert.ThrowsAsync<AgentUnauthorizedException>(first.HeartbeatAsync);
        await second.HeartbeatAsync();
    }

    [Fact]
    public async Task Agent_and_user_credentials_are_not_interchangeable()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var device = await fixture.EnrollDeviceAsync(await fixture.CreateEnrollmentTokenAsync(tenant), "PC-AUTH");

        // A device credential cannot reach administrative endpoints.
        using var asDevice = fixture.CreateClient();
        asDevice.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(AgentAuth.Scheme, device.Credential);
        var adminCall = await asDevice.GetAsync("/api/v1/devices");
        Assert.True(adminCall.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, adminCall.StatusCode.ToString());
        var tokenCall = await asDevice.PostAsJsonAsync("/api/v1/enrollment-tokens", new CreateEnrollmentTokenRequest("x", 1, null, null, null, null), ApiFixture.Json);
        Assert.True(tokenCall.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, tokenCall.StatusCode.ToString());

        // A person's token cannot impersonate an agent.
        using var asUser = fixture.CreateClient(tenant.Token);
        var heartbeat = await asUser.PostAsJsonAsync("/api/v1/agent/heartbeat", new AgentHeartbeatRequest("x", null, "w", "v", "b", "1", DateTimeOffset.UtcNow), ApiFixture.Json);
        Assert.True(heartbeat.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, heartbeat.StatusCode.ToString());

        // Forged or malformed device credentials are rejected.
        using var forged = fixture.CreateClient();
        forged.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(AgentAuth.Scheme, $"sxd_{device.DeviceId:N}_forged");
        Assert.Equal(HttpStatusCode.Unauthorized, (await forged.PostAsJsonAsync("/api/v1/agent/heartbeat", new AgentHeartbeatRequest("x", null, "w", "v", "b", "1", DateTimeOffset.UtcNow), ApiFixture.Json)).StatusCode);
    }

    [Fact]
    public async Task Retired_devices_are_locked_out_immediately()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var device = await fixture.EnrollDeviceAsync(await fixture.CreateEnrollmentTokenAsync(tenant), "PC-RETIRE");
        using var admin = fixture.CreateClient(tenant.Token);
        await HttpAssert.OkAsync<DeviceDto>(await admin.PostAsync($"/api/v1/devices/{device.DeviceId}/retire", null));

        await Assert.ThrowsAsync<AgentUnauthorizedException>(device.HeartbeatAsync);
        var active = await HttpAssert.OkAsync<PagedResult<DeviceDto>>(await admin.GetAsync("/api/v1/devices"));
        Assert.DoesNotContain(active.Items, d => d.Id == device.DeviceId);
    }

    [Fact]
    public async Task Devices_and_groups_of_another_tenant_are_invisible()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();
        var deviceA = await fixture.EnrollDeviceAsync(await fixture.CreateEnrollmentTokenAsync(a), "A-PC");
        using var clientB = fixture.CreateClient(b.Token);

        await HttpAssert.ProblemAsync(await clientB.GetAsync($"/api/v1/devices/{deviceA.DeviceId}"), HttpStatusCode.NotFound, "not_found");
        await HttpAssert.ProblemAsync(await clientB.PostAsync($"/api/v1/devices/{deviceA.DeviceId}/retire", null), HttpStatusCode.NotFound, "not_found");
        var groupB = await HttpAssert.OkAsync<DeviceGroupDto>(
            await clientB.PostAsJsonAsync("/api/v1/device-groups", new SaveDeviceGroupRequest("B group", null), ApiFixture.Json), HttpStatusCode.Created);
        await HttpAssert.ProblemAsync(
            await clientB.PostAsJsonAsync($"/api/v1/device-groups/{groupB.Id}/members", new DeviceGroupMembersRequest([deviceA.DeviceId]), ApiFixture.Json),
            HttpStatusCode.BadRequest,
            "validation_failed");
        Assert.Empty((await HttpAssert.OkAsync<PagedResult<DeviceDto>>(await clientB.GetAsync("/api/v1/devices"))).Items);
    }
}
