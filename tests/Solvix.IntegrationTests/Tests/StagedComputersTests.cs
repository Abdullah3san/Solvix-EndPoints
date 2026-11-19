namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Devices.Application.Devices;
using Solvix.Modules.Devices.Application.Staged;
using Solvix.Modules.Devices.Domain;
using Solvix.Modules.Organization.Application.Branches;
using Solvix.Modules.Organization.Application.Departments;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Results;

/// <summary>Computers expected to install the agent: derived enrollment status, placement, permissions and isolation.</summary>
[Collection(ApiCollection.Name)]
public sealed class StagedComputersTests(ApiFixture fixture)
{
    private static string Host(string prefix = "STG") => $"{prefix}-{Guid.NewGuid():N}"[..14].ToUpperInvariant();

    [Fact]
    public async Task A_staged_computer_is_pending_until_a_device_with_that_hostname_enrolls_and_follows_its_lifecycle()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var host = Host();

        var created = await HttpAssert.OkAsync<StagedComputerDto>(
            await admin.PostAsJsonAsync("/api/v1/staged-computers", new CreateStagedComputerRequest(host.ToLowerInvariant(), null, null), ApiFixture.Json), HttpStatusCode.Created);
        Assert.Equal(StagedComputerStatus.Pending, created.Status);
        Assert.Equal(StagedComputerSource.Manual, created.Source);
        Assert.Null(created.DeviceId);

        // Hostnames are unique per workspace, ignoring case.
        await HttpAssert.ProblemAsync(
            await admin.PostAsJsonAsync("/api/v1/staged-computers", new CreateStagedComputerRequest(host, null, null), ApiFixture.Json), HttpStatusCode.Conflict, "conflict");

        // The matching device enrolls: no extra bookkeeping, the status is derived.
        var device = await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant), DeviceTestKit.Identity(hostname: host));
        var enrolled = Assert.Single((await HttpAssert.OkAsync<PagedResult<StagedComputerDto>>(await admin.GetAsync("/api/v1/staged-computers"))).Items);
        Assert.Equal(StagedComputerStatus.Enrolled, enrolled.Status);
        Assert.Equal(device.DeviceId, enrolled.DeviceId);
        Assert.NotNull(enrolled.EnrolledAtUtc);

        var summary = await HttpAssert.OkAsync<StagedComputerSummaryDto>(await admin.GetAsync("/api/v1/staged-computers/summary"));
        Assert.Equal(new StagedComputerSummaryDto(1, 0, 1, 0), summary);

        await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.PostAsync($"/api/v1/devices/{device.DeviceId}/retire", null));
        var retired = Assert.Single((await HttpAssert.OkAsync<PagedResult<StagedComputerDto>>(await admin.GetAsync("/api/v1/staged-computers?status=Retired"))).Items);
        Assert.Equal(StagedComputerStatus.Retired, retired.Status);
        Assert.Empty((await HttpAssert.OkAsync<PagedResult<StagedComputerDto>>(await admin.GetAsync("/api/v1/staged-computers?status=Pending"))).Items);

        // Removing the entry never touches the device.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync("/api/v1/staged-computers/remove", new RemoveStagedComputersRequest([created.Id]), ApiFixture.Json)).StatusCode);
        Assert.Empty((await HttpAssert.OkAsync<PagedResult<StagedComputerDto>>(await admin.GetAsync("/api/v1/staged-computers"))).Items);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/v1/devices/{device.DeviceId}")).StatusCode);
    }

    [Fact]
    public async Task Import_reports_what_was_created_skipped_and_rejected()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var existing = Host("OLD");
        await admin.PostAsJsonAsync("/api/v1/staged-computers", new CreateStagedComputerRequest(existing, null, null), ApiFixture.Json);

        var a = Host("NEW");
        var b = Host("NEW");
        var result = await HttpAssert.OkAsync<ImportStagedComputersResult>(await admin.PostAsJsonAsync(
            "/api/v1/staged-computers/import",
            new ImportStagedComputersRequest([a, "  " + b + "  ", a.ToLowerInvariant(), existing.ToLowerInvariant(), "", "   ", "bad name with spaces", "-leading-dash", "ok;rm -rf"], null, null),
            ApiFixture.Json));

        Assert.Equal(2, result.Created);
        Assert.Equal(2, result.AlreadyStaged); // one repeated in the input, one already in the list
        Assert.Equal(["bad name with spaces", "-leading-dash", "ok;rm -rf"], result.Rejected.Select(r => r.Value));

        var list = await HttpAssert.OkAsync<PagedResult<StagedComputerDto>>(await admin.GetAsync("/api/v1/staged-computers?pageSize=100"));
        Assert.Equal(3, list.TotalCount);
        Assert.Contains(list.Items, s => s.Hostname == a && s.Source == StagedComputerSource.Import);
        Assert.Contains(list.Items, s => s.Hostname == existing && s.Source == StagedComputerSource.Manual);

        var tooMany = await admin.PostAsJsonAsync("/api/v1/staged-computers/import", new ImportStagedComputersRequest(Enumerable.Range(0, 1001).Select(i => $"H{i}").ToList(), null, null), ApiFixture.Json);
        await HttpAssert.ProblemAsync(tooMany, HttpStatusCode.BadRequest, "validation_failed");
        await HttpAssert.ProblemAsync(
            await admin.PostAsJsonAsync("/api/v1/staged-computers", new CreateStagedComputerRequest("has space", null, null), ApiFixture.Json), HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Placement_is_validated_inside_the_workspace_and_a_department_implies_its_branch()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var other = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        using var stranger = fixture.CreateClient(other.Token);
        var hq = Assert.Single((await HttpAssert.OkAsync<PagedResult<BranchDto>>(await admin.GetAsync("/api/v1/branches"))).Items);
        var branch2 = await HttpAssert.OkAsync<BranchDto>(
            await admin.PostAsJsonAsync("/api/v1/branches", new CreateBranchRequest("Second", "B2", false, null, null, null, null), ApiFixture.Json), HttpStatusCode.Created);
        var dept = await HttpAssert.OkAsync<DepartmentDto>(
            await admin.PostAsJsonAsync("/api/v1/departments", new CreateDepartmentRequest(hq.Id, null, "IT", "IT", null), ApiFixture.Json), HttpStatusCode.Created);
        var foreignBranch = Assert.Single((await HttpAssert.OkAsync<PagedResult<BranchDto>>(await stranger.GetAsync("/api/v1/branches"))).Items);

        var withDepartment = await HttpAssert.OkAsync<StagedComputerDto>(
            await admin.PostAsJsonAsync("/api/v1/staged-computers", new CreateStagedComputerRequest(Host(), null, dept.Id), ApiFixture.Json), HttpStatusCode.Created);
        Assert.Equal(hq.Id, withDepartment.BranchId);
        Assert.Equal("IT", withDepartment.DepartmentName);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/staged-computers", new CreateStagedComputerRequest(Host(), branch2.Id, dept.Id), ApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync("/api/v1/staged-computers", new CreateStagedComputerRequest(Host(), foreignBranch.Id, null), ApiFixture.Json)).StatusCode);

        var imported = await HttpAssert.OkAsync<ImportStagedComputersResult>(
            await admin.PostAsJsonAsync("/api/v1/staged-computers/import", new ImportStagedComputersRequest([Host(), Host()], branch2.Id, null), ApiFixture.Json));
        Assert.Equal(2, imported.Created);
        var byBranch = await HttpAssert.OkAsync<PagedResult<StagedComputerDto>>(await admin.GetAsync($"/api/v1/staged-computers?branchId={branch2.Id}"));
        Assert.Equal(2, byBranch.TotalCount);
        Assert.All(byBranch.Items, s => Assert.Equal("Second", s.BranchName));

        // Deleting the department only clears that column; the entry stays.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/departments/{dept.Id}")).StatusCode);
        var after = (await HttpAssert.OkAsync<PagedResult<StagedComputerDto>>(await admin.GetAsync("/api/v1/staged-computers?pageSize=100"))).Items.Single(s => s.Id == withDepartment.Id);
        Assert.Null(after.DepartmentId);
        Assert.Equal(hq.Id, after.BranchId);
    }

    [Fact]
    public async Task Members_can_read_but_not_change_and_workspaces_are_isolated()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var other = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        using var stranger = fixture.CreateClient(other.Token);
        var staged = await HttpAssert.OkAsync<StagedComputerDto>(
            await admin.PostAsJsonAsync("/api/v1/staged-computers", new CreateStagedComputerRequest(Host(), null, null), ApiFixture.Json), HttpStatusCode.Created);

        var email = $"member-{Guid.NewGuid():N}@example.test";
        var add = await admin.PostAsJsonAsync(
            "/api/v1/tenant/members", new Solvix.Modules.Identity.Application.Members.AddMemberRequest(email, "Member", "Passw0rd!Secure", TenantRole.Member), ApiFixture.Json);
        Assert.True(add.IsSuccessStatusCode, await add.Content.ReadAsStringAsync());
        using var member = fixture.CreateClient((await fixture.LoginAsync(email, "Passw0rd!Secure", tenant.Slug)).AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/api/v1/staged-computers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/api/v1/staged-computers/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/api/v1/staged-computers", new CreateStagedComputerRequest(Host(), null, null), ApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/api/v1/staged-computers/import", new ImportStagedComputersRequest([Host()], null, null), ApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/api/v1/staged-computers/remove", new RemoveStagedComputersRequest([staged.Id]), ApiFixture.Json)).StatusCode);

        // Another workspace neither sees the entry nor can remove it; the same hostname is free there.
        Assert.Empty((await HttpAssert.OkAsync<PagedResult<StagedComputerDto>>(await stranger.GetAsync("/api/v1/staged-computers"))).Items);
        await stranger.PostAsJsonAsync("/api/v1/staged-computers/remove", new RemoveStagedComputersRequest([staged.Id]), ApiFixture.Json);
        Assert.Single((await HttpAssert.OkAsync<PagedResult<StagedComputerDto>>(await admin.GetAsync("/api/v1/staged-computers"))).Items);
        Assert.Equal(HttpStatusCode.Created, (await stranger.PostAsJsonAsync("/api/v1/staged-computers", new CreateStagedComputerRequest(staged.Hostname, null, null), ApiFixture.Json)).StatusCode);
        using var anonymous = fixture.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/staged-computers")).StatusCode);
    }
}
