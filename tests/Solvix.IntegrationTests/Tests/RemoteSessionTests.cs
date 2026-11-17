namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using Solvix.Agent.Contracts;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Support.Application;
using Solvix.Modules.Support.Domain;
using Solvix.SharedKernel.Results;

/// <summary>Remote command sessions: policy gate, consent, relay, audit, time limits and isolation.</summary>
[Collection(ApiCollection.Name)]
public sealed class RemoteSessionTests(ApiFixture fixture)
{
    private async Task<(TenantSession Tenant, EnrolledDevice Device, HttpClient Agent, HttpClient Admin)> SetUpAsync(RemoteAccessMode mode = RemoteAccessMode.ConsentRequired)
    {
        var tenant = await fixture.RegisterTenantAsync();
        var device = await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant));
        var agent = fixture.CreateDeviceClient(device.Certificate);
        await Heartbeat(agent, mode);
        return (tenant, device, agent, fixture.CreateClient(tenant.Token));
    }

    private static async Task Heartbeat(HttpClient agent, RemoteAccessMode mode) =>
        Assert.True((await agent.PostAsJsonAsync(
            "/" + AgentRoutes.Heartbeat,
            new HeartbeatRequest
            {
                AgentVersion = DeviceTestKit.AgentVersion,
                Health = new AgentHealthReport { StartedAtUtc = DateTimeOffset.UtcNow, UptimeSeconds = 5, RemoteAccess = mode },
            },
            ApiFixture.Json)).IsSuccessStatusCode);

    private static async Task<RemoteSessionDto> StartAsync(HttpClient admin, Guid deviceId, string? reason = "Fix printer") =>
        await HttpAssert.OkAsync<RemoteSessionDto>(
            await admin.PostAsJsonAsync($"/api/v1/devices/{deviceId}/remote-sessions", new StartRemoteSessionRequest(reason), ApiFixture.Json), HttpStatusCode.Created);

    private static string Remote(Guid id, string action) => $"/{AgentRoutes.Remote}/{id}/{action}";

    [Fact]
    public async Task A_session_runs_from_request_to_consent_to_relay_to_end_and_is_audited()
    {
        var (_, device, agent, admin) = await SetUpAsync();

        var session = await StartAsync(admin, device.DeviceId);
        Assert.Equal(RemoteSessionStatus.Pending, session.Status);
        Assert.Equal("Fix printer", session.Reason);

        // The laptop learns of the request, and the person says yes.
        var pending = await HttpAssert.OkAsync<PendingRemoteResponse>(await agent.GetAsync($"/{AgentRoutes.Remote}/pending?wait=5"));
        Assert.Equal(session.Id, pending.Request!.SessionId);
        Assert.Equal(session.RequestedByName, pending.Request.RequestedBy);
        Assert.Equal(HttpStatusCode.NoContent, (await agent.PostAsJsonAsync(Remote(session.Id, "accept"), new AcceptRemoteRequest { Consent = RemoteConsent.User }, ApiFixture.Json)).StatusCode);

        // Technician types; the agent receives it. The agent answers; the technician sees it.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/v1/remote-sessions/{session.Id}/input", new RemoteInputRequest("hostname\n"), ApiFixture.Json)).StatusCode);
        var input = await HttpAssert.OkAsync<RemoteInputResponse>(await agent.GetAsync($"/{AgentRoutes.Remote}/{session.Id}/input?after=0&wait=5"));
        Assert.Equal("hostname\n", Assert.Single(input.Chunks).Text);
        Assert.False(input.Ended);

        Assert.Equal(HttpStatusCode.NoContent, (await agent.PostAsJsonAsync(Remote(session.Id, "output"), new RemoteOutputRequest { Text = "LAPTOP-01\r\n" }, ApiFixture.Json)).StatusCode);
        var output = await HttpAssert.OkAsync<RemoteOutputDto>(await admin.GetAsync($"/api/v1/remote-sessions/{session.Id}/output?after=0&wait=5"));
        Assert.Equal("LAPTOP-01\r\n", Assert.Single(output.Chunks).Text);
        Assert.Equal(RemoteSessionStatus.Active, output.Status);

        // Reading again after the last position returns nothing new.
        var none = await HttpAssert.OkAsync<RemoteOutputDto>(await admin.GetAsync($"/api/v1/remote-sessions/{session.Id}/output?after={output.Chunks[0].Sequence}&wait=1"));
        Assert.Empty(none.Chunks);

        // The technician ends it; the agent is told to stop and can no longer send output.
        var ended = await HttpAssert.OkAsync<RemoteSessionDto>(await admin.PostAsync($"/api/v1/remote-sessions/{session.Id}/end", null));
        Assert.Equal(RemoteSessionStatus.Ended, ended.Status);
        Assert.True((await HttpAssert.OkAsync<RemoteInputResponse>(await agent.GetAsync($"/{AgentRoutes.Remote}/{session.Id}/input?after=1&wait=1"))).Ended);
        await HttpAssert.ProblemAsync(
            await agent.PostAsJsonAsync(Remote(session.Id, "output"), new RemoteOutputRequest { Text = "late" }, ApiFixture.Json), HttpStatusCode.Conflict, "remote_session_not_active");
        await HttpAssert.ProblemAsync(
            await admin.PostAsJsonAsync($"/api/v1/remote-sessions/{session.Id}/input", new RemoteInputRequest("x"), ApiFixture.Json), HttpStatusCode.Conflict, "remote_session_not_active");

        // Audit trail.
        var audit = await HttpAssert.OkAsync<PagedResult<RemoteSessionDto>>(await admin.GetAsync($"/api/v1/remote-sessions?deviceId={device.DeviceId}"));
        var row = Assert.Single(audit.Items);
        Assert.Equal(RemoteConsent.User, row.Consent);
        Assert.NotNull(row.StartedAtUtc);
        Assert.NotNull(row.EndedAtUtc);
        Assert.Equal(device.Identity.Hostname, row.DeviceName);
        Assert.Contains("Ended by", row.EndReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_computers_own_policy_gates_sessions_and_the_server_cannot_override_it()
    {
        var (_, device, agent, admin) = await SetUpAsync(RemoteAccessMode.Disabled);
        await HttpAssert.ProblemAsync(
            await admin.PostAsJsonAsync($"/api/v1/devices/{device.DeviceId}/remote-sessions", new StartRemoteSessionRequest("x"), ApiFixture.Json), HttpStatusCode.Conflict, "remote_access_disabled");

        // A computer that never reported a policy (older agent) counts as disabled too.
        var tenant = await fixture.RegisterTenantAsync();
        var fresh = await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant));
        using var freshAdmin = fixture.CreateClient(tenant.Token);
        await HttpAssert.ProblemAsync(
            await freshAdmin.PostAsJsonAsync($"/api/v1/devices/{fresh.DeviceId}/remote-sessions", new StartRemoteSessionRequest(null), ApiFixture.Json), HttpStatusCode.Conflict, "remote_access_disabled");

        // Turning it on locally (next heartbeat) is what enables it.
        await Heartbeat(agent, RemoteAccessMode.Unattended);
        Assert.Equal(RemoteSessionStatus.Pending, (await StartAsync(admin, device.DeviceId)).Status);
    }

    [Fact]
    public async Task One_open_session_per_device_and_a_denied_request_frees_the_device()
    {
        var (_, device, agent, admin) = await SetUpAsync();
        var first = await StartAsync(admin, device.DeviceId);
        await HttpAssert.ProblemAsync(
            await admin.PostAsJsonAsync($"/api/v1/devices/{device.DeviceId}/remote-sessions", new StartRemoteSessionRequest(null), ApiFixture.Json), HttpStatusCode.Conflict, "remote_session_open");

        Assert.Equal(HttpStatusCode.NoContent, (await agent.PostAsJsonAsync(Remote(first.Id, "deny"), new DenyRemoteRequest { Reason = "Not now" }, ApiFixture.Json)).StatusCode);
        var denied = await HttpAssert.OkAsync<RemoteSessionDto>(await admin.GetAsync($"/api/v1/remote-sessions/{first.Id}"));
        Assert.Equal(RemoteSessionStatus.Denied, denied.Status);
        Assert.Equal("Not now", denied.EndReason);
        Assert.Null(denied.Consent);

        // Answering twice is refused; a new request is fine.
        await HttpAssert.ProblemAsync(
            await agent.PostAsJsonAsync(Remote(first.Id, "accept"), new AcceptRemoteRequest { Consent = RemoteConsent.User }, ApiFixture.Json), HttpStatusCode.Conflict, "remote_session_not_pending");
        await StartAsync(admin, device.DeviceId);
    }

    [Fact]
    public async Task Unanswered_requests_expire_and_idle_sessions_end()
    {
        var (_, device, agent, admin) = await SetUpAsync();
        var stale = await StartAsync(admin, device.DeviceId);
        await fixture.ExecutePlatformSqlAsync("UPDATE remote_sessions SET created_at_utc = now() - interval '10 minutes' WHERE id = @id", ("id", stale.Id));
        var expired = await HttpAssert.OkAsync<RemoteSessionDto>(await admin.GetAsync($"/api/v1/remote-sessions/{stale.Id}"));
        Assert.Equal(RemoteSessionStatus.Expired, expired.Status);
        // An expired request is not offered to the laptop any more, and cannot be accepted.
        Assert.Null((await HttpAssert.OkAsync<PendingRemoteResponse>(await agent.GetAsync($"/{AgentRoutes.Remote}/pending?wait=0"))).Request);

        var session = await StartAsync(admin, device.DeviceId);
        await agent.PostAsJsonAsync(Remote(session.Id, "accept"), new AcceptRemoteRequest { Consent = RemoteConsent.User }, ApiFixture.Json);
        await fixture.ExecutePlatformSqlAsync("UPDATE remote_sessions SET last_activity_at_utc = now() - interval '1 hour' WHERE id = @id", ("id", session.Id));
        Assert.True((await HttpAssert.OkAsync<RemoteInputResponse>(await agent.GetAsync($"/{AgentRoutes.Remote}/{session.Id}/input?after=0&wait=0"))).Ended);
        var idle = await HttpAssert.OkAsync<RemoteSessionDto>(await admin.GetAsync($"/api/v1/remote-sessions/{session.Id}"));
        Assert.Equal(RemoteSessionStatus.Ended, idle.Status);
        Assert.Contains("idle", idle.EndReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Long_polls_return_as_soon_as_data_arrives()
    {
        var (_, device, agent, admin) = await SetUpAsync(RemoteAccessMode.Unattended);
        var pendingPoll = agent.GetAsync($"/{AgentRoutes.Remote}/pending?wait=20");
        await Task.Delay(1000);
        var session = await StartAsync(admin, device.DeviceId);
        Assert.Equal(session.Id, (await HttpAssert.OkAsync<PendingRemoteResponse>(await pendingPoll)).Request!.SessionId);

        await agent.PostAsJsonAsync(Remote(session.Id, "accept"), new AcceptRemoteRequest { Consent = RemoteConsent.Unattended }, ApiFixture.Json);
        var inputPoll = agent.GetAsync($"/{AgentRoutes.Remote}/{session.Id}/input?after=0&wait=20");
        var outputPoll = admin.GetAsync($"/api/v1/remote-sessions/{session.Id}/output?after=0&wait=20");
        await Task.Delay(1000);
        await admin.PostAsJsonAsync($"/api/v1/remote-sessions/{session.Id}/input", new RemoteInputRequest("dir\n"), ApiFixture.Json);
        await agent.PostAsJsonAsync(Remote(session.Id, "output"), new RemoteOutputRequest { Text = "files" }, ApiFixture.Json);
        Assert.Equal("dir\n", Assert.Single((await HttpAssert.OkAsync<RemoteInputResponse>(await inputPoll)).Chunks).Text);
        Assert.Equal("files", Assert.Single((await HttpAssert.OkAsync<RemoteOutputDto>(await outputPoll)).Chunks).Text);
        Assert.Equal(RemoteConsent.Unattended, (await HttpAssert.OkAsync<RemoteSessionDto>(await admin.GetAsync($"/api/v1/remote-sessions/{session.Id}"))).Consent);
    }

    [Fact]
    public async Task Sessions_are_isolated_between_tenants_and_between_devices()
    {
        var (_, deviceA, agentA, adminA) = await SetUpAsync();
        var (_, _, agentB, adminB) = await SetUpAsync();
        var session = await StartAsync(adminA, deviceA.DeviceId);
        await agentA.PostAsJsonAsync(Remote(session.Id, "accept"), new AcceptRemoteRequest { Consent = RemoteConsent.User }, ApiFixture.Json);

        // Another tenant's administrator.
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/v1/remote-sessions/{session.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/v1/remote-sessions/{session.Id}/output?after=0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PostAsJsonAsync($"/api/v1/remote-sessions/{session.Id}/input", new RemoteInputRequest("x"), ApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PostAsync($"/api/v1/remote-sessions/{session.Id}/end", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PostAsJsonAsync($"/api/v1/devices/{deviceA.DeviceId}/remote-sessions", new StartRemoteSessionRequest(null), ApiFixture.Json)).StatusCode);
        Assert.Empty((await HttpAssert.OkAsync<PagedResult<RemoteSessionDto>>(await adminB.GetAsync("/api/v1/remote-sessions"))).Items);

        // Another tenant's computer, and another computer in the same tenant, cannot touch it.
        Assert.Equal(HttpStatusCode.NotFound, (await agentB.GetAsync($"/{AgentRoutes.Remote}/{session.Id}/input?after=0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await agentB.PostAsJsonAsync(Remote(session.Id, "output"), new RemoteOutputRequest { Text = "x" }, ApiFixture.Json)).StatusCode);
        var otherDevice = await fixture.EnrollAsync(await fixture.CreateTokenAsync((await fixture.RegisterTenantAsync())));
        using var otherAgent = fixture.CreateDeviceClient(otherDevice.Certificate);
        Assert.Equal(HttpStatusCode.NotFound, (await otherAgent.GetAsync($"/{AgentRoutes.Remote}/{session.Id}/input?after=0")).StatusCode);
    }

    [Fact]
    public async Task Only_administrators_with_a_valid_identity_can_use_remote_sessions()
    {
        var (_, device, _, admin) = await SetUpAsync();
        using var noToken = fixture.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await noToken.PostAsJsonAsync($"/api/v1/devices/{device.DeviceId}/remote-sessions", new StartRemoteSessionRequest(null), ApiFixture.Json)).StatusCode);
        using var anonymousAgent = fixture.CreateDeviceClient(null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymousAgent.GetAsync($"/{AgentRoutes.Remote}/pending?wait=0")).StatusCode);

        // Input limits.
        var session = await StartAsync(admin, device.DeviceId);
        await HttpAssert.ProblemAsync(
            await admin.PostAsJsonAsync($"/api/v1/remote-sessions/{session.Id}/input", new RemoteInputRequest(""), ApiFixture.Json), HttpStatusCode.BadRequest, "validation_failed");
        await HttpAssert.ProblemAsync(
            await admin.PostAsJsonAsync($"/api/v1/remote-sessions/{session.Id}/input", new RemoteInputRequest(new string('x', RemoteChunk.MaxTextLength + 1)), ApiFixture.Json), HttpStatusCode.BadRequest, "validation_failed");
        await HttpAssert.ProblemAsync(
            await admin.PostAsJsonAsync($"/api/v1/devices/{device.DeviceId}/remote-sessions", new StartRemoteSessionRequest(new string('r', RemoteSession.ReasonMaxLength + 1)), ApiFixture.Json), HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Retired_devices_cannot_be_reached_and_the_audit_survives_device_deletion()
    {
        var (_, device, agent, admin) = await SetUpAsync();
        var session = await StartAsync(admin, device.DeviceId);
        await agent.PostAsJsonAsync(Remote(session.Id, "accept"), new AcceptRemoteRequest { Consent = RemoteConsent.User }, ApiFixture.Json);
        await admin.PostAsync($"/api/v1/remote-sessions/{session.Id}/end", null);

        await admin.PostAsync($"/api/v1/devices/{device.DeviceId}/retire", null);
        await HttpAssert.ProblemAsync(
            await admin.PostAsJsonAsync($"/api/v1/devices/{device.DeviceId}/remote-sessions", new StartRemoteSessionRequest(null), ApiFixture.Json), HttpStatusCode.Conflict, "device_retired");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/devices/{device.DeviceId}")).StatusCode);

        var kept = await HttpAssert.OkAsync<RemoteSessionDto>(await admin.GetAsync($"/api/v1/remote-sessions/{session.Id}"));
        Assert.Equal(RemoteConsent.User, kept.Consent);
        Assert.Null(kept.DeviceName);
    }
}
