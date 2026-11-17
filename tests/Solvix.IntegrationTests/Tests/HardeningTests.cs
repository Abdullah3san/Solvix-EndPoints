namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Identity.Application.Auth;

/// <summary>Cross-cutting production hardening: response headers and abuse throttling.</summary>
[Collection(ApiCollection.Name)]
public sealed class HardeningTests(ApiFixture fixture)
{
    [Fact]
    public async Task Api_responses_carry_security_headers()
    {
        using var client = fixture.CreateClient();
        var response = await client.GetAsync("/health/live");
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Contains("camera=()", response.Headers.GetValues("Permissions-Policy").Single(), StringComparison.Ordinal);

        // Error responses are covered too.
        var notFound = await client.GetAsync("/api/v1/does-not-exist");
        Assert.Equal("nosniff", notFound.Headers.GetValues("X-Content-Type-Options").Single());

        // The API reference page must stay usable, so it is not given a blanket CSP.
        var docs = await client.GetAsync("/scalar/v1");
        Assert.False(docs.Headers.Contains("Content-Security-Policy"));
        Assert.Equal("nosniff", docs.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task A_demoted_member_loses_admin_and_owner_rights_immediately_not_when_the_token_expires()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var admin = await fixture.AddRoleMemberAsync(tenant, Phase12Role.Admin);
        using var owner = fixture.CreateClient(tenant.Token);
        using var adminClient = fixture.CreateClient(admin.Token); // token issued while the user was an Admin

        Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync("/api/v1/reports")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync("/api/v1/helpdesk/email-channels")).StatusCode);

        // Demote: replace the user's roles with one holding only devices.view (Phase 02 role assignment).
        var plain = await HttpAssert.OkAsync<Solvix.Modules.Identity.Application.Roles.RoleDto>(
            await owner.PostAsJsonAsync("/api/v1/roles", new Solvix.Modules.Identity.Application.Roles.SaveRoleRequest("Plain member", null, [Solvix.SharedKernel.Authorization.Permissions.Devices.View]), ApiFixture.Json),
            HttpStatusCode.Created);
        var demote = await owner.PutAsJsonAsync($"/api/v1/users/{admin.UserId}/roles", new Solvix.Modules.Identity.Application.Users.SetUserRolesRequest([plain.Id]), ApiFixture.Json);
        Assert.True(demote.IsSuccessStatusCode, await demote.Content.ReadAsStringAsync());

        // Same, still-valid token: permissions are evaluated per request, so admin endpoints refuse it.
        Assert.Equal(HttpStatusCode.Forbidden, (await adminClient.GetAsync("/api/v1/reports")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await adminClient.GetAsync("/api/v1/helpdesk/email-channels")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await adminClient.PostAsync("/api/v1/billing/subscription/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync("/api/v1/helpdesk/tickets")).StatusCode); // still a member
    }

}
