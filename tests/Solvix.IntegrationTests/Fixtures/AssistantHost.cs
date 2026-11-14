namespace Solvix.IntegrationTests.Fixtures;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Solvix.Modules.Assistant.Chat;
using Solvix.Modules.Assistant.Llm;
using Solvix.Modules.Identity.Application.Members;
using Solvix.Modules.Organization.Application.Branches;
using Solvix.Modules.Organization.Application.Departments;
using Solvix.SharedKernel.Identity;

/// <summary>Deterministic stand-in for the LLM: replays queued responses and records every request it receives.</summary>
public sealed class ScriptedChatModel : IChatModel
{
    private readonly Queue<Func<ChatRequest, ChatResponse>> _steps = new();

    public List<ChatRequest> Requests { get; } = [];

    public bool IsConfigured { get; set; } = true;

    public string ModelName => "scripted-model";

    public void Reset()
    {
        _steps.Clear();
        Requests.Clear();
        IsConfigured = true;
    }

    public void Enqueue(params ChatResponse[] responses)
    {
        foreach (var r in responses)
        {
            _steps.Enqueue(_ => r);
        }
    }

    public void EnqueueFactory(Func<ChatRequest, ChatResponse> step) => _steps.Enqueue(step);

    public void Always(Func<ChatRequest, ChatResponse> step) => _alwaysStep = step;

    private Func<ChatRequest, ChatResponse>? _alwaysStep;

    public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (_steps.Count > 0)
        {
            return Task.FromResult(_steps.Dequeue()(request));
        }

        return _alwaysStep is not null
            ? Task.FromResult(_alwaysStep(request))
            : throw new ChatModelException("Scripted model has no more steps.");
    }

    public static ChatResponse Say(string text) => new([new TextBlock(text)], "end_turn", new ChatUsage(100, 20), "scripted-model");

    public static ChatResponse Tool(string name, object args, string id = "toolu_1") =>
        new([new ToolUseBlock(id, name, JsonSerializer.SerializeToElement(args))], "tool_use", new ChatUsage(100, 20), "scripted-model");

    /// <summary>The content of the n-th tool result in the most recent request.</summary>
    public string LastToolResult(int index = 0) =>
        Requests[^1].Messages[^1].Content.OfType<ToolResultBlock>().ElementAt(index).Content;
}

public static class AssistantHost
{
    private static readonly object Gate = new();
    private static WebApplicationFactory<Program>? _factory;

    public static ScriptedChatModel Model { get; } = new();

    public static WebApplicationFactory<Program> Factory(ApiFixture fixture)
    {
        lock (Gate)
        {
            return _factory ??= fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChatModel>();
                services.AddSingleton<IChatModel>(Model);
            }));
        }
    }
}

/// <summary>A tenant with an owner, an admin and a member, two branches and a Finance department.</summary>
public sealed class AssistantTenant
{
    private AssistantTenant()
    {
    }

    public TenantSession Session { get; private set; } = null!;

    public Guid TenantId => Session.TenantId;

    public HttpClient Owner { get; private set; } = null!;

    public HttpClient Admin { get; private set; } = null!;

    public HttpClient Member { get; private set; } = null!;

    public BranchDto Beirut { get; private set; } = null!;

    public DepartmentDto Finance { get; private set; } = null!;

    public static async Task<AssistantTenant> CreateAsync(ApiFixture fixture)
    {
        var factory = AssistantHost.Factory(fixture);
        var session = await fixture.RegisterTenantAsync();
        var tenant = new AssistantTenant { Session = session };

        HttpClient Client(string token)
        {
            var c = factory.CreateClient();
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return c;
        }

        tenant.Owner = Client(session.Token);
        tenant.Beirut = await HttpAssert.OkAsync<BranchDto>(
            await tenant.Owner.PostAsJsonAsync("/api/v1/branches", new CreateBranchRequest("Beirut", "BEI", false, null, null, null, null), ApiFixture.Json), System.Net.HttpStatusCode.Created);
        tenant.Finance = await HttpAssert.OkAsync<DepartmentDto>(
            await tenant.Owner.PostAsJsonAsync("/api/v1/departments", new CreateDepartmentRequest(tenant.Beirut.Id, null, "Finance", "FIN", null), ApiFixture.Json), System.Net.HttpStatusCode.Created);

        foreach (var (role, label) in new[] { (TenantRole.Admin, "admin"), (TenantRole.Member, "member") })
        {
            var email = $"{label}-{session.Slug}@example.test";
            (await tenant.Owner.PostAsJsonAsync("/api/v1/tenant/members", new AddMemberRequest(email, label, "Passw0rd!Secure", role), ApiFixture.Json)).EnsureSuccessStatusCode();
            var auth = await fixture.LoginAsync(email, "Passw0rd!Secure", session.Slug);
            if (role == TenantRole.Admin)
            {
                tenant.Admin = Client(auth.AccessToken);
            }
            else
            {
                tenant.Member = Client(auth.AccessToken);
            }
        }

        return tenant;
    }

    public static async Task<ChatReplyDto> ChatAsync(HttpClient client, string message, Guid? conversationId = null) =>
        await HttpAssert.OkAsync<ChatReplyDto>(await client.PostAsJsonAsync("/api/v1/assistant/chat", new SendMessageRequest(conversationId, message), ApiFixture.Json));
}

/// <summary>Direct SQL access for assertions that must bypass the API (and, where needed, RLS).</summary>
public static class Sql
{
    public static async Task<T> ScalarAsync<T>(ApiFixture fixture, string sql, Guid? tenantId = null, bool asApp = true, bool bypass = false)
    {
        await using var connection = new NpgsqlConnection(asApp ? fixture.Database.AppConnectionString : fixture.Database.MigratorConnectionString);
        await connection.OpenAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false), set_config('app.rls_bypass', @b, false)", connection))
        {
            set.Parameters.AddWithValue("t", tenantId?.ToString() ?? string.Empty);
            set.Parameters.AddWithValue("b", bypass ? "on" : string.Empty);
            await set.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default! : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static async Task<int> ExecuteAsync(ApiFixture fixture, string sql, Guid? tenantId = null, bool asApp = true, bool bypass = false)
    {
        await using var connection = new NpgsqlConnection(asApp ? fixture.Database.AppConnectionString : fixture.Database.MigratorConnectionString);
        await connection.OpenAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false), set_config('app.rls_bypass', @b, false)", connection))
        {
            set.Parameters.AddWithValue("t", tenantId?.ToString() ?? string.Empty);
            set.Parameters.AddWithValue("b", bypass ? "on" : string.Empty);
            await set.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync();
    }
}
