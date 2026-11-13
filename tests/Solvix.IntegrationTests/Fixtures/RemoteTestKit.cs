namespace Solvix.IntegrationTests.Fixtures;

using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Solvix.Agent.Contracts;
using Solvix.Agent.Contracts.RemoteManagement;
using Solvix.Modules.RemoteManagement.CommandBroker;
using Solvix.SharedKernel.Authorization;

public static class RemoteTestKit
{
    /// <summary>A tenant with one enrolled device and a client acting as its agent.</summary>
    public static async Task<RemoteSetup> SetUpAsync(this ApiFixture fixture, string? hostname = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var tenant = await fixture.RegisterTenantAsync();
        var token = await fixture.CreateTokenAsync(tenant);
        var device = await fixture.EnrollAsync(token, DeviceTestKit.Identity(hostname: hostname ?? $"PC-{Guid.NewGuid().ToString("N")[..6]}"));
        return new RemoteSetup(tenant, device, fixture.CreateClient(tenant.Token), fixture.CreateDeviceClient(device.Certificate));
    }

    public static Task<HttpResponseMessage> OperationAsync(this HttpClient client, Guid deviceId, string operation, OperationRequest? body = null) =>
        client.PostAsJsonAsync($"/api/v1/remote/devices/{deviceId}/operations/{operation}", body ?? new OperationRequest(), ApiFixture.Json);

    public static async Task<CommandExecutionDto> RequestAsync(this HttpClient client, Guid deviceId, string operation, OperationRequest? body = null) =>
        await HttpAssert.OkAsync<CommandExecutionDto>(await client.OperationAsync(deviceId, operation, body), HttpStatusCode.Accepted);

    public static async Task<CommandExecutionDto> GetCommandAsync(this HttpClient client, Guid executionId) =>
        await HttpAssert.OkAsync<CommandExecutionDto>(await client.GetAsync($"/api/v1/remote/commands/{executionId}"));

    public static async Task<IReadOnlyList<SignedCommand>> FetchAsync(this HttpClient agent, int waitSeconds = 0) =>
        (await HttpAssert.OkAsync<AgentCommandBatch>(await agent.GetAsync($"/{AgentRoutes.Commands}?waitSeconds={waitSeconds}"))).Commands;

    public static Task<HttpResponseMessage> AckAsync(this HttpClient agent, Guid executionId, bool accepted = true, string? code = null) =>
        agent.PostAsJsonAsync("/" + AgentRoutes.CommandAcknowledgement(executionId), new CommandAcknowledgement { Accepted = accepted, RejectionCode = code }, ApiFixture.Json);

    public static Task<HttpResponseMessage> ResultAsync(this HttpClient agent, Guid executionId, CommandOutcome outcome = CommandOutcome.Succeeded, string? output = "{}") =>
        agent.PostAsJsonAsync(
            "/" + AgentRoutes.CommandResult(executionId),
            new CommandResultReport { Outcome = outcome, ExitCode = 0, Output = output, StartedAtUtc = DateTimeOffset.UtcNow, CompletedAtUtc = DateTimeOffset.UtcNow },
            ApiFixture.Json);

    /// <summary>Verifies a delivered command with the key set the API publishes and returns its envelope.</summary>
    public static async Task<CommandEnvelope> VerifyAsync(this HttpClient agent, SignedCommand command)
    {
        var keys = await HttpAssert.OkAsync<CommandKeySet>(await agent.GetAsync("/" + AgentRoutes.CommandKeys));
        var key = keys.Keys.Single(k => k.KeyId == command.KeyId);
        var bytes = CommandSignature.Verify(command, Convert.FromBase64String(key.PublicKey));
        Assert.NotNull(bytes);
        return CommandSignature.Deserialize(bytes)!;
    }

    public static CommandEnvelope Peek(SignedCommand command) => CommandSignature.Deserialize(Base64Url.DecodeFromChars(command.Envelope))!;

    public static async Task<CommandSweepResult> SweepCommandsAsync(this ApiFixture fixture, DateTimeOffset now)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CommandSweeper>().SweepAsync(now, CancellationToken.None);
    }

    /// <summary>A technician holding exactly <paramref name="permissions"/>.</summary>
    public static async Task<(MemberSession Member, HttpClient Client)> TechnicianAsync(this ApiFixture fixture, TenantSession tenant, params string[] permissions)
    {
        var member = await fixture.AddMemberWithPermissionsAsync(tenant, permissions.Length == 0 ? [Permissions.Remote.View] : permissions);
        return (member, fixture.CreateClient(member.Token));
    }
}

public sealed record RemoteSetup(TenantSession Tenant, EnrolledDevice Device, HttpClient Owner, HttpClient Agent) : IDisposable
{
    public Guid DeviceId => Device.Enrollment.DeviceId;

    public string Hostname => Device.Identity.Hostname;

    public void Dispose()
    {
        Owner.Dispose();
        Agent.Dispose();
    }
}
