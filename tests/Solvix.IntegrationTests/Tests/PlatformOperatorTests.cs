namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Identity.Application.Auth;
using Solvix.Modules.Identity.Application.Members;
using Solvix.Modules.Tenancy.Application.Platform;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Tenancy;

/// <summary>
/// Closed registration and the provider console: only a configured platform operator can create,
/// list, suspend and reactivate companies, and nobody can become one through the API.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PlatformOperatorTests(ApiFixture fixture)
{
    private async Task<HttpClient> OperatorClientAsync()
    {
        var session = await fixture.LoginOperatorAsync();
        return fixture.CreateClient(session.AccessToken);
    }

    [Fact]
    public async Task Public_registration_is_closed_by_default()
    {
        using var client = fixture.CreateClient();
        var status = await client.GetAsync("/api/v1/tenant/registration");
        Assert.False((await HttpAssert.OkAsync<RegistrationStatusDto>(status)).Open);

        var response = await client.PostAsJsonAsync(
            "/api/v1/tenant/register",
            new { companyName = "Uninvited Co", slug = "uninvited-co", ownerEmail = "walk-in@example.test", ownerDisplayName = "Walk In", ownerPassword = "Passw0rd!Secure" },
            ApiFixture.Json);
        await HttpAssert.ProblemAsync(response, HttpStatusCode.Forbidden);

        // Nothing was created: the e-mail cannot sign in and the workspace id is still free.
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("walk-in@example.test", "Passw0rd!Secure", null), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        var operatorClient = await OperatorClientAsync();
        var list = await HttpAssert.OkAsync<PagedResult<PlatformTenantDto>>(await operatorClient.GetAsync("/api/v1/platform/tenants?search=uninvited"));
        Assert.Empty(list.Items);

        // The workspace-id probe is not an open oracle either.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/tenant/slug-availability?slug=anything")).StatusCode);
    }

    [Fact]
    public async Task Only_platform_operators_can_use_the_provider_console()
    {
        var customer = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(customer.Token);
        using var anonymous = fixture.CreateClient();

        foreach (var path in new[] { "/api/v1/platform/summary", "/api/v1/platform/tenants" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        }

        var create = await owner.PostAsJsonAsync(
            "/api/v1/platform/tenants",
            new CreatePlatformTenantRequest("Sneaky", null, "sneaky@example.test", "Sneaky", "Passw0rd!Secure"),
            ApiFixture.Json);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsync($"/api/v1/platform/tenants/{customer.TenantId}/suspend", null)).StatusCode);

        // A customer's owner is not an operator even inside the response of their own login.
        Assert.False(customer.Auth.User.IsPlatformAdmin);
    }

    [Fact]
    public async Task Operator_creates_a_company_whose_owner_can_sign_in_and_is_listed_with_real_counts()
    {
        using var op = await OperatorClientAsync();
        var slug = $"acme-{Guid.NewGuid():N}"[..16];
        var email = $"it@{slug}.test";

        var created = await HttpAssert.OkAsync<PlatformTenantDto>(
            await op.PostAsJsonAsync("/api/v1/platform/tenants", new CreatePlatformTenantRequest("Acme Subscribers", slug, email, "Acme Admin", "Passw0rd!Secure"), ApiFixture.Json),
            HttpStatusCode.Created);
        Assert.Equal(TenantStatus.Active, created.Status);
        Assert.Equal([email], created.OwnerEmails);
        Assert.Equal(1, created.MemberCount);
        Assert.Equal(0, created.DeviceCount);

        // The owner signs in to that workspace as Owner and is not an operator.
        var auth = await fixture.LoginAsync(email, "Passw0rd!Secure");
        Assert.Equal(slug, auth.Tenant.Slug);
        Assert.Equal(TenantRole.Owner, auth.Tenant.Role);
        Assert.False(auth.User.IsPlatformAdmin);

        var session = new TenantSession(created.Id, slug, created.Name, email, "Passw0rd!Secure", auth);
        await fixture.EnrollAsync(await fixture.CreateTokenAsync(session));

        var listed = await HttpAssert.OkAsync<PagedResult<PlatformTenantDto>>(await op.GetAsync($"/api/v1/platform/tenants?search={slug}"));
        var row = Assert.Single(listed.Items);
        Assert.Equal(1, row.DeviceCount);

        var summary = await HttpAssert.OkAsync<PlatformSummaryDto>(await op.GetAsync("/api/v1/platform/summary"));
        Assert.True(summary.Total >= 2); // the operator's own workspace plus this one
        Assert.False(summary.PublicRegistrationOpen);

        // The operator's own workspace is flagged so the console can protect it.
        var all = await HttpAssert.OkAsync<PagedResult<PlatformTenantDto>>(await op.GetAsync("/api/v1/platform/tenants?pageSize=100"));
        Assert.Single(all.Items, t => t.IsCurrent);
    }

    [Fact]
    public async Task Suspending_a_company_blocks_its_users_and_reactivating_restores_them()
    {
        var customer = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(customer.Token);
        using var op = await OperatorClientAsync();
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/branches")).StatusCode);

        var suspended = await HttpAssert.OkAsync<PlatformTenantDto>(await op.PostAsync($"/api/v1/platform/tenants/{customer.TenantId}/suspend", null));
        Assert.Equal(TenantStatus.Suspended, suspended.Status);
        Assert.NotNull(suspended.SuspendedAtUtc);

        // An already-issued token stops working immediately (no cache lag) and sign-in is refused.
        await HttpAssert.ProblemAsync(await owner.GetAsync("/api/v1/branches"), HttpStatusCode.Forbidden, "tenant_inactive");
        using var anonymous = fixture.CreateClient();
        var login = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(customer.OwnerEmail, customer.OwnerPassword, null), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);

        var reactivated = await HttpAssert.OkAsync<PlatformTenantDto>(await op.PostAsync($"/api/v1/platform/tenants/{customer.TenantId}/reactivate", null));
        Assert.Equal(TenantStatus.Active, reactivated.Status);
        Assert.Null(reactivated.SuspendedAtUtc);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/branches")).StatusCode);

        var filtered = await HttpAssert.OkAsync<PagedResult<PlatformTenantDto>>(await op.GetAsync("/api/v1/platform/tenants?status=Suspended&pageSize=100"));
        Assert.DoesNotContain(filtered.Items, t => t.Id == customer.TenantId);
        Assert.Equal(HttpStatusCode.NotFound, (await op.PostAsync($"/api/v1/platform/tenants/{Guid.NewGuid()}/suspend", null)).StatusCode);
    }

    [Fact]
    public async Task The_operator_cannot_suspend_the_workspace_they_are_signed_in_to()
    {
        using var op = await OperatorClientAsync();
        var all = await HttpAssert.OkAsync<PagedResult<PlatformTenantDto>>(await op.GetAsync("/api/v1/platform/tenants?pageSize=100"));
        var own = Assert.Single(all.Items, t => t.IsCurrent);
        await HttpAssert.ProblemAsync(await op.PostAsync($"/api/v1/platform/tenants/{own.Id}/suspend", null), HttpStatusCode.Conflict, "cannot_suspend_own_workspace");
    }

    [Fact]
    public async Task Members_cannot_make_themselves_operators()
    {
        var customer = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(customer.Token);

        // No member-management request can carry an operator flag; extra fields are ignored.
        var email = $"{Guid.NewGuid():N}@example.test";
        var body = System.Text.Json.JsonSerializer.SerializeToNode(
            new AddMemberRequest(email, "Aspiring Operator", "Passw0rd!Secure", TenantRole.Admin), ApiFixture.Json)!.AsObject();
        body["isPlatformAdmin"] = true;
        var invite = await owner.PostAsJsonAsync("/api/v1/tenant/members", body, ApiFixture.Json);
        Assert.True(invite.StatusCode == HttpStatusCode.Created, await invite.Content.ReadAsStringAsync());
        var invited = await fixture.LoginAsync(email, "Passw0rd!Secure");
        Assert.False(invited.User.IsPlatformAdmin);
        using var client = fixture.CreateClient(invited.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/platform/summary")).StatusCode);
    }
}
