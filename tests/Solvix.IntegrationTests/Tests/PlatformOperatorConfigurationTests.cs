namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using Npgsql;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Identity.Application.Auth;

/// <summary>
/// Configuration is the single source of truth for who the operator is. Uses its own database and runs in the
/// shared collection so that it never overlaps with the tests whose host reads the same environment variables.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PlatformOperatorConfigurationTests : IAsyncLifetime
{
    private PostgresTestDatabase _database = null!;
    private readonly string _redis = Environment.GetEnvironmentVariable("SOLVIX_TEST_REDIS") ?? "localhost:6379";
    private readonly List<SolvixApiFactory> _factories = [];

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        foreach (var factory in _factories)
        {
            await factory.DisposeAsync();
        }

        await _database.DisposeAsync();
        // Leave the process environment the way the shared fixture expects it.
        foreach (var key in Overrides)
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    private static readonly string[] Overrides = ["Solvix__Platform__Operator__Email", "Solvix__Platform__Operator__InitialPassword", "Solvix__Platform__AllowPublicRegistration"];

    private HttpClient Start(string? operatorEmail, string? password, bool openRegistration = false)
    {
        var factory = new SolvixApiFactory(
            _database,
            _redis,
            $"solvix-test-{Guid.NewGuid():N}:",
            new Dictionary<string, string>
            {
                ["Solvix__Platform__Operator__Email"] = operatorEmail ?? string.Empty,
                ["Solvix__Platform__Operator__InitialPassword"] = password ?? string.Empty,
                ["Solvix__Platform__AllowPublicRegistration"] = openRegistration ? "true" : "false",
            });
        _factories.Add(factory);
        return factory.CreateClient();
    }

    private static async Task<AuthResponse> LoginAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password, null), ApiFixture.Json);
        return await HttpAssert.OkAsync<AuthResponse>(response);
    }

    private async Task<bool> IsOperatorInDatabaseAsync(string email)
    {
        await using var connection = new NpgsqlConnection(_database.MigratorConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT is_platform_admin FROM users WHERE normalized_email = @e", connection);
        command.Parameters.AddWithValue("e", email.ToUpperInvariant());
        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }

    [Fact]
    public async Task Changing_the_configured_operator_revokes_the_previous_one_immediately()
    {
        const string first = "first-operator@platform.test";
        const string second = "second-operator@platform.test";
        const string password = "Operat0r!Passw0rd";

        var firstHost = Start(first, password);
        var firstSession = await LoginAsync(firstHost, first, password);
        Assert.True(firstSession.User.IsPlatformAdmin);
        firstHost.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", firstSession.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await firstHost.GetAsync("/api/v1/platform/summary")).StatusCode);

        // Restart with a different operator: the old account keeps working as a normal user but loses operator rights.
        var secondHost = Start(second, password);
        Assert.True(await IsOperatorInDatabaseAsync(second));
        Assert.False(await IsOperatorInDatabaseAsync(first));

        // The old, still-valid operator token is refused: the flag is re-checked in the database on every call.
        Assert.Equal(HttpStatusCode.Forbidden, (await firstHost.GetAsync("/api/v1/platform/summary")).StatusCode);
        Assert.False((await LoginAsync(secondHost, first, password)).User.IsPlatformAdmin);
        Assert.True((await LoginAsync(secondHost, second, password)).User.IsPlatformAdmin);
    }

    [Fact]
    public async Task No_configured_operator_means_nobody_is_one()
    {
        var host = Start(null, null);
        var list = await host.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("nobody@platform.test", "Passw0rd!Secure", null), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
    }

    [Fact]
    public async Task A_weak_initial_password_creates_no_operator_account()
    {
        var host = Start("weak-operator@platform.test", "short");
        var login = await host.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("weak-operator@platform.test", "short", null), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Existing_account_becomes_operator_without_its_password_being_touched()
    {
        const string email = "existing-owner@platform.test";
        // Registered while public registration is open (first host), then promoted by a restart (second host).
        var openHost = Start(null, null, openRegistration: true);
        var registered = await openHost.PostAsJsonAsync(
            "/api/v1/tenant/register",
            new { companyName = "Existing Co", slug = "existing-co", ownerEmail = email, ownerDisplayName = "Existing Owner", ownerPassword = "Original!Passw0rd" },
            ApiFixture.Json);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        var host = Start(email, "Different!Passw0rd");
        Assert.True(await IsOperatorInDatabaseAsync(email));
        var session = await LoginAsync(host, email, "Original!Passw0rd");
        Assert.True(session.User.IsPlatformAdmin);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, "Different!Passw0rd", null), ApiFixture.Json)).StatusCode);

        // And registration is closed again on the restarted host.
        var closed = await host.PostAsJsonAsync(
            "/api/v1/tenant/register",
            new { companyName = "Late Co", slug = "late-co", ownerEmail = "late@platform.test", ownerDisplayName = "Late", ownerPassword = "Passw0rd!Secure" },
            ApiFixture.Json);
        Assert.Equal(HttpStatusCode.Forbidden, closed.StatusCode);
    }
}
