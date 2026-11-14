namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using Solvix.Infrastructure.Tenancy;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Identity.Application.Auth;
using Solvix.Modules.Identity.Application.Members;
using Solvix.Modules.Organization.Application.Branches;
using Solvix.Modules.Organization.Application.Departments;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Results;

/// <summary>Cross-tenant access attempts through the public API must never succeed.</summary>
[Collection(ApiCollection.Name)]
public sealed class TenantIsolationApiTests(ApiFixture fixture)
{
    [Fact]
    public async Task Tenant_cannot_list_read_update_or_delete_another_tenants_branch()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();
        using var clientA = fixture.CreateClient(a.Token);
        using var clientB = fixture.CreateClient(b.Token);

        var created = await HttpAssert.OkAsync<BranchDto>(
            await clientA.PostAsJsonAsync("/api/v1/branches", new CreateBranchRequest("Berlin", "BER", false, null, null, null, null), ApiFixture.Json),
            HttpStatusCode.Created);

        var listA = await HttpAssert.OkAsync<PagedResult<BranchDto>>(await clientA.GetAsync("/api/v1/branches"));
        Assert.Contains(listA.Items, x => x.Id == created.Id);

        var listB = await HttpAssert.OkAsync<PagedResult<BranchDto>>(await clientB.GetAsync("/api/v1/branches"));
        Assert.DoesNotContain(listB.Items, x => x.Id == created.Id);
        Assert.All(listB.Items, x => Assert.Equal("HQ", x.Code));

        await HttpAssert.ProblemAsync(await clientB.GetAsync($"/api/v1/branches/{created.Id}"), HttpStatusCode.NotFound, "not_found");
        await HttpAssert.ProblemAsync(
            await clientB.PutAsJsonAsync($"/api/v1/branches/{created.Id}", new UpdateBranchRequest("Hijacked", "BER", false, null, null, null, null), ApiFixture.Json),
            HttpStatusCode.NotFound, "not_found");
        await HttpAssert.ProblemAsync(await clientB.DeleteAsync($"/api/v1/branches/{created.Id}"), HttpStatusCode.NotFound, "not_found");
        await HttpAssert.ProblemAsync(await clientB.PostAsync($"/api/v1/branches/{created.Id}/deactivate", null), HttpStatusCode.NotFound, "not_found");

        // Still intact for A.
        var again = await HttpAssert.OkAsync<BranchDto>(await clientA.GetAsync($"/api/v1/branches/{created.Id}"));
        Assert.Equal("Berlin", again.Name);
        Assert.True(again.IsActive);
    }

    [Fact]
    public async Task Department_cannot_reference_a_branch_or_parent_of_another_tenant()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();
        using var clientA = fixture.CreateClient(a.Token);
        using var clientB = fixture.CreateClient(b.Token);

        var branchesA = await HttpAssert.OkAsync<PagedResult<BranchDto>>(await clientA.GetAsync("/api/v1/branches"));
        var hqA = Assert.Single(branchesA.Items);
        var deptA = await HttpAssert.OkAsync<DepartmentDto>(
            await clientA.PostAsJsonAsync("/api/v1/departments", new CreateDepartmentRequest(hqA.Id, null, "Engineering", "ENG", null), ApiFixture.Json),
            HttpStatusCode.Created);

        // B tries to attach a department to A's branch.
        await HttpAssert.ProblemAsync(
            await clientB.PostAsJsonAsync("/api/v1/departments", new CreateDepartmentRequest(hqA.Id, null, "Spy", "SPY", null), ApiFixture.Json),
            HttpStatusCode.NotFound, "not_found");

        // B tries to use A's department as a parent for its own branch.
        var branchesB = await HttpAssert.OkAsync<PagedResult<BranchDto>>(await clientB.GetAsync("/api/v1/branches"));
        var hqB = Assert.Single(branchesB.Items);
        await HttpAssert.ProblemAsync(
            await clientB.PostAsJsonAsync("/api/v1/departments", new CreateDepartmentRequest(hqB.Id, deptA.Id, "Spy", "SPY", null), ApiFixture.Json),
            HttpStatusCode.NotFound, "not_found");

        await HttpAssert.ProblemAsync(await clientB.GetAsync($"/api/v1/departments/{deptA.Id}"), HttpStatusCode.NotFound, "not_found");
        await HttpAssert.ProblemAsync(await clientB.GetAsync($"/api/v1/departments/tree/{hqA.Id}"), HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Tenant_header_that_disagrees_with_token_is_rejected()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();
        using var client = fixture.CreateClient(a.Token);

        client.DefaultRequestHeaders.Add(TenantResolutionMiddleware.TenantHeader, b.TenantId.ToString());
        await HttpAssert.ProblemAsync(await client.GetAsync("/api/v1/branches"), HttpStatusCode.Forbidden, "cross_tenant_access");

        client.DefaultRequestHeaders.Remove(TenantResolutionMiddleware.TenantHeader);
        client.DefaultRequestHeaders.Add(TenantResolutionMiddleware.TenantHeader, "not-a-guid");
        await HttpAssert.ProblemAsync(await client.GetAsync("/api/v1/branches"), HttpStatusCode.Forbidden, "cross_tenant_access");

        client.DefaultRequestHeaders.Remove(TenantResolutionMiddleware.TenantHeader);
        client.DefaultRequestHeaders.Add(TenantResolutionMiddleware.TenantHeader, a.TenantId.ToString());
        (await client.GetAsync("/api/v1/branches")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Tenant_endpoints_only_ever_act_on_the_token_tenant()
    {
        var a = await fixture.RegisterTenantAsync("Alpha Co");
        var b = await fixture.RegisterTenantAsync("Beta Co");
        using var clientA = fixture.CreateClient(a.Token);
        using var clientB = fixture.CreateClient(b.Token);

        var me = await HttpAssert.OkAsync<CurrentUserDto>(await clientA.GetAsync("/api/v1/auth/me"));
        Assert.Equal(a.TenantId, me.Tenant.Id);
        Assert.Equal(TenantRole.Owner, me.Tenant.Role);

        var tenantB = await HttpAssert.OkAsync<Solvix.Modules.Tenancy.Application.Management.TenantDto>(await clientB.GetAsync("/api/v1/tenant"));
        Assert.Equal(b.TenantId, tenantB.Id);
        Assert.Equal("Beta Co", tenantB.Name);

        // Members are tenant-scoped: B does not see A's owner.
        var membersB = await HttpAssert.OkAsync<PagedResult<MemberDto>>(await clientB.GetAsync("/api/v1/tenant/members"));
        var onlyB = Assert.Single(membersB.Items);
        Assert.Equal(b.OwnerEmail, onlyB.Email);
    }

    [Fact]
    public async Task Removed_member_is_locked_out_even_with_a_valid_token()
    {
        var a = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(a.Token);

        var memberEmail = $"member-{Guid.NewGuid():N}@example.test";
        var member = await HttpAssert.OkAsync<MemberDto>(
            await owner.PostAsJsonAsync("/api/v1/tenant/members", new AddMemberRequest(memberEmail, "Member", "Passw0rd!Secure", TenantRole.Member), ApiFixture.Json),
            HttpStatusCode.Created);

        var memberAuth = await fixture.LoginAsync(memberEmail, "Passw0rd!Secure");
        Assert.Equal(a.TenantId, memberAuth.Tenant.Id);
        using var memberClient = fixture.CreateClient(memberAuth.AccessToken);
        (await memberClient.GetAsync("/api/v1/branches")).EnsureSuccessStatusCode();

        // Members cannot administer.
        await HttpAssert.ProblemAsync(
            await memberClient.PostAsJsonAsync("/api/v1/branches", new CreateBranchRequest("X", "X", false, null, null, null, null), ApiFixture.Json),
            HttpStatusCode.Forbidden);

        (await owner.DeleteAsync($"/api/v1/tenant/members/{member.MembershipId}")).EnsureSuccessStatusCode();

        // Token is still cryptographically valid, but the membership check fails.
        await HttpAssert.ProblemAsync(await memberClient.GetAsync("/api/v1/branches"), HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task Login_binds_the_token_to_a_tenant_the_user_belongs_to()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();

        // A's owner cannot obtain a token for B.
        using var anonymous = fixture.CreateClient();
        var response = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(a.OwnerEmail, a.OwnerPassword, b.Slug), ApiFixture.Json);
        await HttpAssert.ProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");

        var ok = await fixture.LoginAsync(a.OwnerEmail, a.OwnerPassword, a.Slug);
        Assert.Equal(a.TenantId, ok.Tenant.Id);
        Assert.Single(ok.AvailableTenants);

        using var authed = fixture.CreateClient(ok.AccessToken);
        await HttpAssert.ProblemAsync(
            await authed.PostAsJsonAsync("/api/v1/auth/switch-tenant", new SwitchTenantRequest(b.Slug), ApiFixture.Json),
            HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task Anonymous_and_forged_tokens_are_rejected()
    {
        using var anonymous = fixture.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/branches")).StatusCode);

        using var forged = fixture.CreateClient("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4IiwidGlkIjoieSJ9.invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, (await forged.GetAsync("/api/v1/branches")).StatusCode);
    }
}
