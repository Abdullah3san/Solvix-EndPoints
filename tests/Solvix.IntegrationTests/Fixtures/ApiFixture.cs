namespace Solvix.IntegrationTests.Fixtures;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using Solvix.Modules.Identity.Application.Account;
using Solvix.Modules.Identity.Application.Auth;
using Solvix.Modules.Identity.Application.Invitations;
using Solvix.Modules.Identity.Application.Roles;
using Solvix.Modules.Tenancy.Application.Registration;

/// <summary>Shared, sequentially-used API + database for the whole integration run.</summary>
public sealed class ApiFixture : IAsyncLifetime
{
    public const string DefaultPassword = "Passw0rd!Secure";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private PostgresTestDatabase? _database;
    private int _counter;

    public SolvixApiFactory Factory { get; private set; } = null!;

    public PostgresTestDatabase Database => _database!;

    public CapturingEmailSender Emails { get; } = new();

    public string RedisConnection { get; } = Environment.GetEnvironmentVariable("SOLVIX_TEST_REDIS") ?? "localhost:6379";

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        Factory = new SolvixApiFactory(_database, RedisConnection, $"solvix-test-{Guid.NewGuid():N}:", Emails);
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

    public HttpClient CreateClient(string? accessToken = null) => CreateClient(Factory, accessToken);

    /// <summary>Client for a specific host (e.g. one started with different settings).</summary>
    public static HttpClient CreateClient(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, string? accessToken = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var client = factory.CreateClient();
        if (accessToken is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return client;
    }

    public string NewEmail(string prefix = "user") => $"{prefix}-{Interlocked.Increment(ref _counter)}-{Guid.NewGuid():N}"[..28] + "@example.test";

    /// <summary>
    /// Registers a brand-new tenant the way a person would: sign up, open the
    /// confirmation e-mail, confirm, sign in. Returns the owner's session.
    /// </summary>
    public async Task<TenantSession> RegisterTenantAsync(string? name = null, string password = DefaultPassword)
    {
        var pending = await RegisterUnverifiedTenantAsync(name, password);
        await VerifyEmailAsync(pending.OwnerEmail);
        var auth = await LoginAsync(pending.OwnerEmail, password, pending.Slug);
        return pending with { Auth = auth };
    }

    /// <summary>Signs up without confirming the owner's e-mail address.</summary>
    public async Task<TenantSession> RegisterUnverifiedTenantAsync(string? name = null, string password = DefaultPassword)
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
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Registration failed: {response.StatusCode} {body}");
        var result = JsonSerializer.Deserialize<RegisterTenantResponse>(body, Json)!;
        Assert.True(result.EmailVerificationRequired);
        Assert.Null(result.Session);
        return new TenantSession(result.TenantId, result.Slug, result.Name, email, password, null!);
    }

    public async Task VerifyEmailAsync(string email)
    {
        var mail = await Emails.WaitForAsync(email, "Confirm your e-mail");
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/verify-email", new VerifyEmailRequest(CapturingEmailSender.TokenFrom(mail)), Json);
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, $"E-mail verification failed: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    /// <summary>Signs in an account without MFA and returns its session.</summary>
    public async Task<AuthResponse> LoginAsync(string email, string password, string? tenantSlug = null)
    {
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password, tenantSlug), Json);
        var login = await HttpAssert.OkAsync<LoginResponse>(response);
        Assert.False(login.MfaRequired, "Expected a password-only sign-in.");
        return login.Session!;
    }

    public async Task<HttpResponseMessage> TryLoginAsync(string email, string password, string? tenantSlug = null)
    {
        using var client = CreateClient();
        return await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password, tenantSlug), Json);
    }

    /// <summary>The roles of the caller's tenant, by name.</summary>
    public static async Task<Dictionary<string, RoleDto>> RolesAsync(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        var roles = await HttpAssert.OkAsync<List<RoleDto>>(await client.GetAsync("/api/v1/roles"));
        return roles.ToDictionary(r => r.Name, StringComparer.Ordinal);
    }

    /// <summary>
    /// Adds a member the real way: an administrator invites, the invitee opens
    /// the e-mail, accepts (choosing a password when the account is new) and signs in.
    /// </summary>
    public async Task<MemberSession> AddMemberAsync(TenantSession tenant, string roleName, string? email = null, string password = DefaultPassword)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        using var admin = CreateClient(tenant.Token);
        var roles = await RolesAsync(admin);
        return await AddMemberAsync(tenant, [roles[roleName].Id], email, password);
    }

    public async Task<MemberSession> AddMemberAsync(TenantSession tenant, IReadOnlyList<Guid> roleIds, string? email = null, string password = DefaultPassword)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        email ??= NewEmail("member");
        using var admin = CreateClient(tenant.Token);
        var already = Emails.Count(email, "invited");
        var invitation = await HttpAssert.OkAsync<InvitationDto>(
            await admin.PostAsJsonAsync("/api/v1/invitations", new CreateInvitationRequest(email, "Member " + email[..8], roleIds), Json),
            HttpStatusCode.Created);

        var accepted = await AcceptInvitationAsync(email, password, skip: already);
        var auth = await LoginAsync(email, password, tenant.Slug);
        return new MemberSession(auth.User.Id, email, password, invitation.Id, accepted.AccountCreated, auth);
    }

    public async Task<InvitationAcceptedDto> AcceptInvitationAsync(string email, string? password = DefaultPassword, int skip = 0)
    {
        var mail = await Emails.WaitForAsync(email, "invited", skip);
        using var client = CreateClient();
        return await HttpAssert.OkAsync<InvitationAcceptedDto>(await client.PostAsJsonAsync(
            "/api/v1/auth/invitations/accept",
            new AcceptInvitationRequest(CapturingEmailSender.TokenFrom(mail), "Invited Person", password),
            Json));
    }

    /// <summary>Runs SQL as the schema owner with Row-Level Security bypassed. For arranging time-dependent state only.</summary>
    public async Task<int> ExecuteAsOwnerAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Database.MigratorConnectionString);
        await connection.OpenAsync();
        await using (var bypass = new NpgsqlCommand("SELECT set_config('app.rls_bypass', 'on', false)", connection))
        {
            await bypass.ExecuteScalarAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync();
    }

    public async Task<T?> ScalarAsOwnerAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Database.MigratorConnectionString);
        await connection.OpenAsync();
        await using (var bypass = new NpgsqlCommand("SELECT set_config('app.rls_bypass', 'on', false)", connection))
        {
            await bypass.ExecuteScalarAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }
}

public sealed record TenantSession(Guid TenantId, string Slug, string Name, string OwnerEmail, string OwnerPassword, AuthResponse Auth)
{
    public string Token => Auth.AccessToken;

    public Guid OwnerUserId => Auth.User.Id;
}

public sealed record MemberSession(Guid UserId, string Email, string Password, Guid InvitationId, bool AccountCreated, AuthResponse Auth)
{
    public string Token => Auth.AccessToken;
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}
