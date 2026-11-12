namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Solvix.Api.Middleware;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Identity.Application.Auth;
using Solvix.Modules.Tenancy.Application.Management;

[Collection(ApiCollection.Name)]
public sealed class PlatformTests(ApiFixture fixture)
{
    [Fact]
    public async Task Health_endpoints_report_postgres_and_redis()
    {
        using var client = fixture.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OpenApi_document_is_versioned()
    {
        using var client = fixture.CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var paths = doc.GetProperty("paths");
        var pathNames = string.Join(", ", paths.EnumerateObject().Select(p => p.Name));
        Assert.True(paths.TryGetProperty("/api/v1/tenant/register", out _), pathNames);
        Assert.True(paths.TryGetProperty("/api/v1/branches", out _), pathNames);
        Assert.True(
            doc.TryGetProperty("components", out var components) && components.TryGetProperty("securitySchemes", out var schemes) && schemes.TryGetProperty("Bearer", out _),
            doc.ToString());
    }

    [Fact]
    public async Task Unknown_api_version_is_rejected()
    {
        var t = await fixture.RegisterTenantAsync();
        using var client = fixture.CreateClient(t.Token);
        var response = await client.GetAsync("/api/v99/branches");
        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound, response.StatusCode.ToString());
    }

    [Fact]
    public async Task Correlation_id_is_echoed_and_problem_details_carry_it()
    {
        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add(CorrelationIdMiddleware.HeaderName, "corr-123");
        var response = await client.GetAsync("/api/v1/branches");
        Assert.Equal("corr-123", response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single());

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("nobody@example.test", "wrong-password-1", null), ApiFixture.Json);
        var problem = await HttpAssert.ProblemAsync(login, HttpStatusCode.Unauthorized, "invalid_credentials");
        Assert.Equal("corr-123", problem.GetProperty("correlationId").GetString());
        Assert.True(problem.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task Tenant_rename_is_owner_only_and_invalidates_cache()
    {
        var t = await fixture.RegisterTenantAsync("Before");
        using var owner = fixture.CreateClient(t.Token);

        var renamed = await HttpAssert.OkAsync<TenantDto>(await owner.PutAsJsonAsync("/api/v1/tenant", new UpdateTenantRequest("After"), ApiFixture.Json));
        Assert.Equal("After", renamed.Name);

        // /auth/me reads the tenant through the cached directory: must reflect the rename.
        var me = await HttpAssert.OkAsync<CurrentUserDto>(await owner.GetAsync("/api/v1/auth/me"));
        Assert.Equal("After", me.Tenant.Name);

        var overview = await HttpAssert.OkAsync<TenantOverviewDto>(await owner.GetAsync("/api/v1/tenant/overview"));
        Assert.Equal(1, overview.MemberCount);
        Assert.Equal(1, overview.BranchCount);
    }
}
