namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Organization.Application;
using Solvix.Modules.Organization.Application.Branches;
using Solvix.Modules.Organization.Application.CompanyProfiles;
using Solvix.Modules.Organization.Application.Departments;
using Solvix.SharedKernel.Results;

[Collection(ApiCollection.Name)]
public sealed class OrganizationApiTests(ApiFixture fixture)
{
    [Fact]
    public async Task Registration_provisions_profile_and_headquarters()
    {
        var t = await fixture.RegisterTenantAsync("Acme GmbH");
        using var client = fixture.CreateClient(t.Token);

        var profile = await HttpAssert.OkAsync<CompanyProfileDto>(await client.GetAsync("/api/v1/company-profile"));
        Assert.Equal("Acme GmbH", profile.LegalName);
        Assert.Equal("UTC", profile.TimeZone);

        var branches = await HttpAssert.OkAsync<PagedResult<BranchDto>>(await client.GetAsync("/api/v1/branches"));
        var hq = Assert.Single(branches.Items);
        Assert.True(hq.IsHeadquarters);
        Assert.Equal("HQ", hq.Code);
    }

    [Fact]
    public async Task Company_profile_can_be_updated_and_validated()
    {
        var t = await fixture.RegisterTenantAsync();
        using var client = fixture.CreateClient(t.Token);

        var updated = await HttpAssert.OkAsync<CompanyProfileDto>(await client.PutAsJsonAsync(
            "/api/v1/company-profile",
            new UpdateCompanyProfileRequest("Acme Inc", "Acme", "DE123", "HRB 1", "Software", "https://acme.example", "hello@acme.example", "+49 30 1", "Europe/Berlin",
                new AddressDto("Unter den Linden 1", null, "Berlin", "BE", "10117", "de")),
            ApiFixture.Json));
        Assert.Equal("Acme Inc", updated.LegalName);
        Assert.Equal("Europe/Berlin", updated.TimeZone);
        Assert.Equal("DE", updated.Address.CountryCode);

        var problem = await HttpAssert.ProblemAsync(
            await client.PutAsJsonAsync(
                "/api/v1/company-profile",
                new UpdateCompanyProfileRequest(string.Empty, null, null, null, null, "not a url", "nope", null, "Mars/Olympus", null),
                ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");
        var errors = problem.GetProperty("errors");
        Assert.True(errors.TryGetProperty("legalName", out _));
        Assert.True(errors.TryGetProperty("website", out _));
        Assert.True(errors.TryGetProperty("contactEmail", out _));
        Assert.True(errors.TryGetProperty("timeZone", out _));
    }

    [Fact]
    public async Task Branch_codes_are_unique_per_tenant_and_only_one_headquarters()
    {
        var t = await fixture.RegisterTenantAsync();
        var other = await fixture.RegisterTenantAsync();
        using var client = fixture.CreateClient(t.Token);
        using var otherClient = fixture.CreateClient(other.Token);

        await HttpAssert.ProblemAsync(
            await client.PostAsJsonAsync("/api/v1/branches", new CreateBranchRequest("Dup", "hq", false, null, null, null, null), ApiFixture.Json),
            HttpStatusCode.Conflict, "conflict");

        // Same code is fine in another tenant.
        await HttpAssert.OkAsync<BranchDto>(
            await otherClient.PostAsJsonAsync("/api/v1/branches", new CreateBranchRequest("Berlin", "BER", false, null, null, null, null), ApiFixture.Json),
            HttpStatusCode.Created);
        var berlin = await HttpAssert.OkAsync<BranchDto>(
            await client.PostAsJsonAsync("/api/v1/branches", new CreateBranchRequest("Berlin", "ber", true, null, null, "Europe/Berlin", null), ApiFixture.Json),
            HttpStatusCode.Created);
        Assert.True(berlin.IsHeadquarters);
        Assert.Equal("BER", berlin.Code);

        var all = await HttpAssert.OkAsync<PagedResult<BranchDto>>(await client.GetAsync("/api/v1/branches?includeInactive=true"));
        Assert.Equal(2, all.TotalCount);
        Assert.Single(all.Items, b => b.IsHeadquarters);
        Assert.Equal("BER", all.Items.Single(b => b.IsHeadquarters).Code);

        var search = await HttpAssert.OkAsync<PagedResult<BranchDto>>(await client.GetAsync("/api/v1/branches?search=berl"));
        Assert.Single(search.Items);

        await HttpAssert.ProblemAsync(
            await client.PostAsJsonAsync("/api/v1/branches", new CreateBranchRequest(string.Empty, "bad code!", false, null, "no", null, null), ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Departments_support_hierarchy_within_a_branch()
    {
        var t = await fixture.RegisterTenantAsync();
        using var client = fixture.CreateClient(t.Token);
        var hq = (await HttpAssert.OkAsync<PagedResult<BranchDto>>(await client.GetAsync("/api/v1/branches"))).Items.Single();
        var second = await HttpAssert.OkAsync<BranchDto>(
            await client.PostAsJsonAsync("/api/v1/branches", new CreateBranchRequest("Second", "SEC", false, null, null, null, null), ApiFixture.Json),
            HttpStatusCode.Created);

        var eng = await HttpAssert.OkAsync<DepartmentDto>(
            await client.PostAsJsonAsync("/api/v1/departments", new CreateDepartmentRequest(hq.Id, null, "Engineering", "eng", "Builds things"), ApiFixture.Json),
            HttpStatusCode.Created);
        Assert.Equal("ENG", eng.Code);
        Assert.Equal(hq.Name, eng.BranchName);

        var platform = await HttpAssert.OkAsync<DepartmentDto>(
            await client.PostAsJsonAsync("/api/v1/departments", new CreateDepartmentRequest(hq.Id, eng.Id, "Platform", "PLAT", null), ApiFixture.Json),
            HttpStatusCode.Created);
        Assert.Equal(eng.Id, platform.ParentDepartmentId);

        // Duplicate code inside the same branch.
        await HttpAssert.ProblemAsync(
            await client.PostAsJsonAsync("/api/v1/departments", new CreateDepartmentRequest(hq.Id, null, "Eng again", "ENG", null), ApiFixture.Json),
            HttpStatusCode.Conflict, "conflict");

        // Same code in another branch is fine.
        await HttpAssert.OkAsync<DepartmentDto>(
            await client.PostAsJsonAsync("/api/v1/departments", new CreateDepartmentRequest(second.Id, null, "Engineering", "ENG", null), ApiFixture.Json),
            HttpStatusCode.Created);

        // Parent must be in the same branch.
        await HttpAssert.ProblemAsync(
            await client.PostAsJsonAsync("/api/v1/departments", new CreateDepartmentRequest(second.Id, eng.Id, "Cross", "X", null), ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");

        // Cycles are rejected.
        await HttpAssert.ProblemAsync(
            await client.PutAsJsonAsync($"/api/v1/departments/{eng.Id}", new UpdateDepartmentRequest(platform.Id, "Engineering", "ENG", null), ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");

        var tree = await HttpAssert.OkAsync<List<DepartmentTreeNode>>(await client.GetAsync($"/api/v1/departments/tree/{hq.Id}"));
        var root = Assert.Single(tree);
        Assert.Equal("ENG", root.Code);
        Assert.Equal("PLAT", Assert.Single(root.Children).Code);

        // Cannot delete a parent with children, nor a branch with departments.
        await HttpAssert.ProblemAsync(await client.DeleteAsync($"/api/v1/departments/{eng.Id}"), HttpStatusCode.Conflict, "conflict");
        await HttpAssert.ProblemAsync(await client.DeleteAsync($"/api/v1/branches/{hq.Id}"), HttpStatusCode.Conflict, "conflict");
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/departments/{platform.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/departments/{eng.Id}")).StatusCode);

        var list = await HttpAssert.OkAsync<PagedResult<DepartmentDto>>(await client.GetAsync($"/api/v1/departments?branchId={hq.Id}"));
        Assert.Empty(list.Items);
    }

    [Fact]
    public async Task Registration_validation_and_conflicts()
    {
        var existing = await fixture.RegisterTenantAsync();
        using var client = fixture.CreateClient();

        var weak = await client.PostAsJsonAsync("/api/v1/tenant/register",
            new { companyName = "X", slug = "bad slug", ownerEmail = "not-an-email", ownerDisplayName = "", ownerPassword = "short" }, ApiFixture.Json);
        var problem = await HttpAssert.ProblemAsync(weak, HttpStatusCode.BadRequest, "validation_failed");
        var errors = problem.GetProperty("errors");
        foreach (var field in new[] { "slug", "ownerEmail", "ownerDisplayName", "ownerPassword" })
        {
            Assert.True(errors.TryGetProperty(field, out _), $"missing error for {field}: {problem}");
        }

        var dupSlug = await client.PostAsJsonAsync("/api/v1/tenant/register",
            new { companyName = "Y", slug = existing.Slug, ownerEmail = $"{Guid.NewGuid():N}@example.test", ownerDisplayName = "Y", ownerPassword = "Passw0rd!Secure" }, ApiFixture.Json);
        await HttpAssert.ProblemAsync(dupSlug, HttpStatusCode.Conflict, "conflict");

        var dupEmail = await client.PostAsJsonAsync("/api/v1/tenant/register",
            new { companyName = "Z", slug = (string?)null, ownerEmail = existing.OwnerEmail, ownerDisplayName = "Z", ownerPassword = "Passw0rd!Secure" }, ApiFixture.Json);
        await HttpAssert.ProblemAsync(dupEmail, HttpStatusCode.Conflict, "conflict");

        var availability = await client.GetAsync($"/api/v1/tenant/slug-availability?slug={existing.Slug}");
        var json = JsonDocument.Parse(await availability.Content.ReadAsStringAsync()).RootElement;
        Assert.False(json.GetProperty("isAvailable").GetBoolean());
    }
}
