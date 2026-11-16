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
        var admin = await fixture.AddMemberAsync(tenant, Solvix.SharedKernel.Identity.TenantRole.Admin);
        using var owner = fixture.CreateClient(tenant.Token);
        using var adminClient = fixture.CreateClient(admin.Token); // token issued while the user was an Admin

        Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync("/api/v1/reports")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync("/api/v1/helpdesk/email-channels")).StatusCode);

        var members = await owner.GetFromJsonAsync<Solvix.SharedKernel.Results.PagedResult<Solvix.Modules.Identity.Application.Members.MemberDto>>("/api/v1/tenant/members", ApiFixture.Json);
        var membershipId = members!.Items.Single(m => m.UserId == admin.UserId).MembershipId;
        var demote = await owner.PatchAsJsonAsync($"/api/v1/tenant/members/{membershipId}/role", new Solvix.Modules.Identity.Application.Members.ChangeMemberRoleRequest(Solvix.SharedKernel.Identity.TenantRole.Member), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.OK, demote.StatusCode);

        // Same, still-valid token: the role now comes from the membership, so admin-only endpoints refuse it.
        Assert.Equal(HttpStatusCode.Forbidden, (await adminClient.GetAsync("/api/v1/reports")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await adminClient.GetAsync("/api/v1/helpdesk/email-channels")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await adminClient.PostAsync("/api/v1/billing/subscription/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync("/api/v1/helpdesk/tickets")).StatusCode); // still a member
    }

    [Fact]
    public async Task Sign_in_and_registration_are_throttled_per_client()
    {
        // A dedicated host with a tiny budget; the shared one is effectively unlimited for the other tests.
        await using var limited = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Solvix:RateLimits:AuthPerMinute"] = "3" })));
        using var client = limited.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("nobody@example.test", "wrong-password-1", null), ApiFixture.Json);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.Unauthorized));
        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.TooManyRequests));

        // Registration shares the same abuse budget.
        var register = await client.PostAsJsonAsync("/api/v1/tenant/register", new { companyName = "X", ownerEmail = "x@example.test", ownerDisplayName = "X", ownerPassword = "Passw0rd!Secure" }, ApiFixture.Json);
        Assert.Equal(HttpStatusCode.TooManyRequests, register.StatusCode);
    }
}
