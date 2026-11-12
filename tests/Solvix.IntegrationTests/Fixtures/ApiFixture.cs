namespace Solvix.IntegrationTests.Fixtures;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Solvix.Modules.Identity.Application.Auth;
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

    /// <summary>Registers a brand-new tenant with a unique slug and returns its owner session.</summary>
    public async Task<TenantSession> RegisterTenantAsync(string? name = null, string password = "Passw0rd!Secure")
    {
        var n = Interlocked.Increment(ref _counter);
        var slug = $"t{n}-{Guid.NewGuid():N}"[..20];
        name ??= $"Tenant {n}";
        var email = $"owner-{slug}@example.test";
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/v1/tenant/register",
            new RegisterTenantRequest(name, slug, email, "Owner " + n, password),
            Json);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.Created, $"Registration failed: {response.StatusCode} {body}");
        var result = JsonSerializer.Deserialize<RegisterTenantResponse>(body, Json)!;
        return new TenantSession(result.TenantId, result.Slug, result.Name, email, password, result.Auth);
    }

    public async Task<AuthResponse> LoginAsync(string email, string password, string? tenantSlug = null)
    {
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password, tenantSlug), Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
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
