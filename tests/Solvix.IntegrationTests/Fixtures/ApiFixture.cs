namespace Solvix.IntegrationTests.Fixtures;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Solvix.Modules.Identity.Application.Auth;
using Solvix.Modules.Tenancy.Application.Platform;
using Solvix.Modules.Tenancy.Application.Registration;

/// <summary>Shared, sequentially-used API + database for the whole integration run.</summary>
public sealed class ApiFixture : IAsyncLifetime
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private PostgresTestDatabase? _database;
    private int _counter;
    private readonly SemaphoreSlim _operatorGate = new(1, 1);

    public SolvixApiFactory Factory { get; private set; } = null!;

    public PostgresTestDatabase Database => _database!;

    public string RedisConnection { get; } = Environment.GetEnvironmentVariable("SOLVIX_TEST_REDIS") ?? "localhost:6379";

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        Factory = new SolvixApiFactory(_database, RedisConnection, $"solvix-test-{Guid.NewGuid():N}:");
        // Forces host start-up (and therefore MigrateOnStartup).
        using var client = Factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    public HttpClient CreateClient(string? accessToken = null)
    {
        var client = Factory.CreateClient();
        if (accessToken is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return client;
    }

    /// <summary>
    /// Creates a brand-new tenant with a unique slug the way production does it: through
    /// the platform operator (public registration is closed) and returns its owner session.
    /// </summary>
    public async Task<TenantSession> RegisterTenantAsync(string? name = null, string password = "Passw0rd!Secure")
    {
        var n = Interlocked.Increment(ref _counter);
        var slug = $"t{n}-{Guid.NewGuid():N}"[..20];
        name ??= $"Tenant {n}";
        var email = $"owner-{slug}@example.test";
        var operatorSession = await LoginOperatorAsync();
        using var client = CreateClient(operatorSession.AccessToken);
        var response = await client.PostAsJsonAsync(
            "/api/v1/platform/tenants",
            new CreatePlatformTenantRequest(name, slug, email, "Owner " + n, password),
            Json);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.Created, $"Tenant creation failed: {response.StatusCode} {body}");
        var created = JsonSerializer.Deserialize<PlatformTenantDto>(body, Json)!;
        var auth = await LoginAsync(email, password, created.Slug);
        return new TenantSession(created.Id, created.Slug, created.Name, email, password, auth);
    }

    /// <summary>
    /// Signs in as the configured platform operator. The migration round-trip test wipes the shared database,
    /// including the operator the host seeded at start-up, so seed again (exactly what a restart does) when it is gone.
    /// </summary>
    public async Task<AuthResponse> LoginOperatorAsync()
    {
        await _operatorGate.WaitAsync();
        try
        {
            using var client = CreateClient();
            var request = new LoginRequest(SolvixApiFactory.OperatorEmail, SolvixApiFactory.OperatorPassword, null);
            var response = await client.PostAsJsonAsync("/api/v1/auth/login", request, Json);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                await using var scope = Factory.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IPlatformOperatorSeeder>().RunAsync(CancellationToken.None);
                response = await client.PostAsJsonAsync("/api/v1/auth/login", request, Json);
            }

            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"Operator login failed: {(int)response.StatusCode} {body}");
            return JsonSerializer.Deserialize<AuthResponse>(body, Json)!;
        }
        finally
        {
            _operatorGate.Release();
        }
    }

    public async Task<AuthResponse> LoginAsync(string email, string password, string? tenantSlug = null)
    {
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password, tenantSlug), Json);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Login of {email} failed: {(int)response.StatusCode} {body}");
        return JsonSerializer.Deserialize<AuthResponse>(body, Json)!;
    }
}

public sealed record TenantSession(Guid TenantId, string Slug, string Name, string OwnerEmail, string OwnerPassword, AuthResponse Auth)
{
    public string Token => Auth.AccessToken;
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}
