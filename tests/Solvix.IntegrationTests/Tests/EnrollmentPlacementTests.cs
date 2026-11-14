namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Devices.Application.Devices;
using Solvix.Modules.Devices.Application.Enrollment;
using Solvix.Modules.Organization.Application.Branches;
using Solvix.Modules.Organization.Application.Departments;
using Solvix.SharedKernel.Results;

/// <summary>Enrollment tokens that place devices in a branch / department (e.g. one token per store).</summary>
[Collection(ApiCollection.Name)]
public sealed class EnrollmentPlacementTests(ApiFixture fixture)
{
    [Fact]
    public async Task Devices_enrolled_with_a_token_land_in_its_branch_and_department()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var store = await CreateBranchAsync(admin, "Berlin store", "BER");
        var tills = await CreateDepartmentAsync(admin, store.Id, "Tills", "TILL");

        var created = await HttpAssert.OkAsync<CreatedEnrollmentTokenDto>(
            await admin.PostAsJsonAsync("/api/v1/enrollment-tokens", new CreateEnrollmentTokenRequest("Berlin tills", 24, 5, null, null, tills.Id), ApiFixture.Json),
            HttpStatusCode.Created);

        // The department implies its branch; names are resolved for display.
        Assert.Equal(store.Id, created.EnrollmentToken.BranchId);
        Assert.Equal("Berlin store", created.EnrollmentToken.BranchName);
        Assert.Equal(tills.Id, created.EnrollmentToken.DepartmentId);
        Assert.Equal("Tills", created.EnrollmentToken.DepartmentName);

        var a = await fixture.EnrollAsync(created.Token);
        var b = await fixture.EnrollAsync(created.Token);
        foreach (var device in new[] { a, b })
        {
            var details = await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.GetAsync($"/api/v1/devices/{device.DeviceId}"));
            Assert.Equal(store.Id, details.Assignment.Branch!.Id);
            Assert.Equal("Berlin store", details.Assignment.Branch.Name);
            Assert.Equal(tills.Id, details.Assignment.Department!.Id);
        }

        var listed = await HttpAssert.OkAsync<PagedResult<EnrollmentTokenDto>>(await admin.GetAsync("/api/v1/enrollment-tokens"));
        Assert.Equal("Berlin store", Assert.Single(listed.Items).BranchName);
    }

    [Fact]
    public async Task Branch_only_token_assigns_just_the_branch_and_one_token_per_store_keeps_stores_apart()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var berlin = await CreateBranchAsync(admin, "Berlin", "BER");
        var paris = await CreateBranchAsync(admin, "Paris", "PAR");

        var berlinPos = await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant, maxUses: 2, branchId: berlin.Id));
        var parisPos = await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant, maxUses: 2, branchId: paris.Id));

        var inBerlin = await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.GetAsync($"/api/v1/devices/{berlinPos.DeviceId}"));
        var inParis = await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.GetAsync($"/api/v1/devices/{parisPos.DeviceId}"));
        Assert.Equal(berlin.Id, inBerlin.Assignment.Branch!.Id);
        Assert.Null(inBerlin.Assignment.Department);
        Assert.Equal(paris.Id, inParis.Assignment.Branch!.Id);
    }

    [Fact]
    public async Task Placement_is_validated_inside_the_tenant()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();
        using var clientA = fixture.CreateClient(a.Token);
        using var clientB = fixture.CreateClient(b.Token);
        var branchA = await CreateBranchAsync(clientA, "A-branch", "AAA");
        var deptA = await CreateDepartmentAsync(clientA, branchA.Id, "A-dept", "AD");
        var branchB = await CreateBranchAsync(clientB, "B-branch", "BBB");

        // Another tenant's branch or department is "not found", never accepted.
        await HttpAssert.ProblemAsync(
            await clientB.PostAsJsonAsync("/api/v1/enrollment-tokens", new CreateEnrollmentTokenRequest("x", 1, 1, null, branchA.Id), ApiFixture.Json), HttpStatusCode.NotFound, "not_found");
        await HttpAssert.ProblemAsync(
            await clientB.PostAsJsonAsync("/api/v1/enrollment-tokens", new CreateEnrollmentTokenRequest("x", 1, 1, null, null, deptA.Id), ApiFixture.Json), HttpStatusCode.NotFound, "not_found");

        // A department that belongs to a different branch than the one chosen.
        var otherBranch = await CreateBranchAsync(clientA, "Other", "OTH");
        await HttpAssert.ProblemAsync(
            await clientA.PostAsJsonAsync("/api/v1/enrollment-tokens", new CreateEnrollmentTokenRequest("x", 1, 1, null, otherBranch.Id, deptA.Id), ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");

        // B's token list never shows A's placement; B's own token works.
        var own = await fixture.CreateTokenAsync(b, branchId: branchB.Id);
        var device = await fixture.EnrollAsync(own);
        var details = await HttpAssert.OkAsync<DeviceDetailsDto>(await clientB.GetAsync($"/api/v1/devices/{device.DeviceId}"));
        Assert.Equal(branchB.Id, details.Assignment.Branch!.Id);
    }

    [Fact]
    public async Task Re_enrollment_keeps_an_existing_assignment_and_only_fills_in_an_empty_one()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var berlin = await CreateBranchAsync(admin, "Berlin", "BER");
        var paris = await CreateBranchAsync(admin, "Paris", "PAR");

        var identity = DeviceTestKit.Identity();
        var first = await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant, branchId: berlin.Id), identity);

        // Re-enrolled with a Paris token: the Berlin assignment (made by an administrator's workflow) is kept.
        await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant, branchId: paris.Id), identity);
        var details = await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.GetAsync($"/api/v1/devices/{first.DeviceId}"));
        Assert.Equal(berlin.Id, details.Assignment.Branch!.Id);

        // An unassigned device takes the placement of the token it re-enrolls with.
        var plain = DeviceTestKit.Identity();
        var unassigned = await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant), plain);
        await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant, branchId: paris.Id), plain);
        details = await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.GetAsync($"/api/v1/devices/{unassigned.DeviceId}"));
        Assert.Equal(paris.Id, details.Assignment.Branch!.Id);
    }

    [Fact]
    public async Task Deleting_the_department_or_branch_keeps_the_token_usable()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var branch = await CreateBranchAsync(admin, "Closing store", "CLS");
        var dept = await CreateDepartmentAsync(admin, branch.Id, "Tills", "TILL");
        var created = await HttpAssert.OkAsync<CreatedEnrollmentTokenDto>(
            await admin.PostAsJsonAsync("/api/v1/enrollment-tokens", new CreateEnrollmentTokenRequest("closing", 24, 3, null, null, dept.Id), ApiFixture.Json),
            HttpStatusCode.Created);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/departments/{dept.Id}")).StatusCode);
        var tokens = await HttpAssert.OkAsync<PagedResult<EnrollmentTokenDto>>(await admin.GetAsync("/api/v1/enrollment-tokens"));
        var token = Assert.Single(tokens.Items);
        Assert.Null(token.DepartmentId);
        Assert.Equal(branch.Id, token.BranchId); // only the deleted reference is cleared

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/branches/{branch.Id}")).StatusCode);
        tokens = await HttpAssert.OkAsync<PagedResult<EnrollmentTokenDto>>(await admin.GetAsync("/api/v1/enrollment-tokens"));
        Assert.Null(Assert.Single(tokens.Items).BranchId);

        var device = await fixture.EnrollAsync(created.Token);
        var details = await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.GetAsync($"/api/v1/devices/{device.DeviceId}"));
        Assert.Null(details.Assignment.Branch);
        Assert.Null(details.Assignment.Department);
    }

    private static async Task<BranchDto> CreateBranchAsync(HttpClient client, string name, string code) =>
        await HttpAssert.OkAsync<BranchDto>(
            await client.PostAsJsonAsync("/api/v1/branches", new CreateBranchRequest(name, code, false, null, null, null, null), ApiFixture.Json),
            HttpStatusCode.Created);

    private static async Task<DepartmentDto> CreateDepartmentAsync(HttpClient client, Guid branchId, string name, string code) =>
        await HttpAssert.OkAsync<DepartmentDto>(
            await client.PostAsJsonAsync("/api/v1/departments", new CreateDepartmentRequest(branchId, null, name, code, null), ApiFixture.Json),
            HttpStatusCode.Created);
}
