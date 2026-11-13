namespace Solvix.Agent.Tests;

using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Solvix.Agent.Contracts.RemoteManagement;
using Solvix.Agent.Core.Communication;
using Solvix.Agent.Core.RemoteManagement;
using Solvix.Agent.Core.State;

/// <summary>Command verification and execution on the device (security tests 3–7 and cancellation).</summary>
public sealed class RemoteCommandTests : IDisposable
{
    private readonly ECDsa _serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly FakeCommandClient _client;
    private readonly AgentHarness _agent;

    public RemoteCommandTests()
    {
        _client = new FakeCommandClient(_serverKey);
        _agent = new AgentHarness();
        Store.Save(new AgentState { DeviceId = _agent.Api.DeviceId, TenantId = _agent.Api.TenantId, KeyName = "k" });
        Assert.True(Keys.PinIfEmpty(_client.KeySet));
    }

    private CommandKeyStore Keys => _agent.Get<CommandKeyStore>();

    private IAgentStateStore Store => _agent.Get<IAgentStateStore>();

    private CommandVerifier Verifier => _agent.Get<CommandVerifier>();

    private CommandProcessor Processor => new(
        _client, Verifier, Keys, _agent.Get<CommandReplayStore>(), _agent.Get<CommandHandlerRegistry>(),
        _agent.Get<IPrivilegedOperationHost>(), Store, _agent.Get<Solvix.Agent.Core.Security.DeviceCredentialStore>(),
        _agent.Get<Microsoft.Extensions.Options.IOptions<Solvix.Agent.Core.Configuration.AgentOptions>>(), _agent.Time,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<CommandProcessor>.Instance);

    public void Dispose()
    {
        _agent.Dispose();
        _serverKey.Dispose();
    }

    [Fact]
    public void A_correctly_signed_command_for_this_device_is_accepted()
    {
        var result = Verifier.Verify(Sign(Envelope()));
        Assert.True(result.Accepted, result.Detail);
    }

    [Fact]
    public void A_command_for_another_tenant_or_device_is_rejected()
    {
        Assert.Equal(CommandRejectionCodes.WrongTenant, Verifier.Verify(Sign(Envelope() with { TenantId = Guid.NewGuid() })).RejectionCode);
        Assert.Equal(CommandRejectionCodes.WrongDevice, Verifier.Verify(Sign(Envelope() with { DeviceId = Guid.NewGuid() })).RejectionCode);
    }

    [Fact]
    public void Expired_and_implausible_commands_are_rejected()
    {
        var now = _agent.Time.GetUtcNow();
        Assert.Equal(CommandRejectionCodes.Expired, Verifier.Verify(Sign(Envelope() with { CreatedAtUtc = now.AddHours(-2), ExpiresAtUtc = now.AddMinutes(-3) })).RejectionCode);

        // Within the tolerated clock skew it is still accepted.
        Assert.True(Verifier.Verify(Sign(Envelope() with { CreatedAtUtc = now.AddHours(-1), ExpiresAtUtc = now.AddMinutes(-1) })).Accepted);

        Assert.Equal(CommandRejectionCodes.Malformed, Verifier.Verify(Sign(Envelope() with { CreatedAtUtc = now.AddHours(1), ExpiresAtUtc = now.AddHours(2) })).RejectionCode);
        Assert.Equal(CommandRejectionCodes.Malformed, Verifier.Verify(Sign(Envelope() with { ExpiresAtUtc = now.AddDays(30) })).RejectionCode);
    }

    [Fact]
    public void Tampered_forged_and_unknown_key_commands_are_rejected()
    {
        var signed = Sign(Envelope());

        // Change one byte of the envelope (e.g. the command type) without re-signing.
        var bytes = Base64Url.DecodeFromChars(signed.Envelope);
        var tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(CommandTypes.Uptime, CommandTypes.Restart, StringComparison.Ordinal));
        Assert.Equal(CommandRejectionCodes.InvalidSignature, Verifier.Verify(signed with { Envelope = Base64Url.EncodeToString(tampered) }).RejectionCode);

        // Signed by an attacker's key that claims the trusted key id.
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Equal(CommandRejectionCodes.InvalidSignature, Verifier.Verify(CommandSignature.Sign(Envelope(), attacker)).RejectionCode);

        // Signed by a key that was never pinned.
        Assert.Equal(CommandRejectionCodes.UnknownKey, Verifier.Verify(CommandSignature.Sign(Envelope() with { KeyId = "cmd-attacker" }, attacker)).RejectionCode);

        // Garbage.
        Assert.Equal(CommandRejectionCodes.InvalidSignature, Verifier.Verify(signed with { Signature = "AAAA" }).RejectionCode);

        // A pinned key set cannot be replaced by the server (or someone impersonating it).
        Assert.False(Keys.PinIfEmpty(new CommandKeySet { Keys = [new CommandSigningKey { KeyId = "cmd-attacker", PublicKey = Convert.ToBase64String(attacker.ExportSubjectPublicKeyInfo()) }] }));
    }

    [Fact]
    public void Unauthorized_unapproved_self_approved_and_unknown_commands_are_rejected()
    {
        var e = Envelope();
        Assert.Equal(CommandRejectionCodes.NotAuthorized, Verifier.Verify(Sign(e with { Authorization = "Pending" })).RejectionCode);
        Assert.Equal(CommandRejectionCodes.NotApproved, Verifier.Verify(Sign(e with { ApprovalStatus = "Pending" })).RejectionCode);
        Assert.Equal(CommandRejectionCodes.NotApproved, Verifier.Verify(Sign(e with { ApprovalStatus = CommandApprovalStatus.Approved })).RejectionCode);
        Assert.Equal(CommandRejectionCodes.NotApproved, Verifier.Verify(Sign(e with { ApprovalStatus = CommandApprovalStatus.Approved, ApprovalId = Guid.NewGuid(), ApprovedBy = e.RequestedBy })).RejectionCode);
        Assert.True(Verifier.Verify(Sign(e with { ApprovalStatus = CommandApprovalStatus.Approved, ApprovalId = Guid.NewGuid(), ApprovedBy = Guid.NewGuid() })).Accepted);
        Assert.Equal(CommandRejectionCodes.NotAllowed, Verifier.Verify(Sign(e with { CommandType = "shell.exec" })).RejectionCode);
        Assert.Equal(CommandRejectionCodes.UnsupportedVersion, Verifier.Verify(Sign(e with { V = 2 })).RejectionCode);
    }

    [Fact]
    public void Locally_disabled_commands_are_refused_whatever_the_server_says()
    {
        using var agent = new AgentHarness(o => o.RemoteManagement.DisabledCommands = [CommandTypes.Shutdown]);
        agent.Get<IAgentStateStore>().Save(new AgentState { DeviceId = agent.Api.DeviceId, TenantId = agent.Api.TenantId, KeyName = "k" });
        agent.Get<CommandKeyStore>().PinIfEmpty(_client.KeySet);
        var shutdown = Envelope() with { DeviceId = agent.Api.DeviceId, TenantId = agent.Api.TenantId, CommandType = CommandTypes.Shutdown };
        Assert.Equal(CommandRejectionCodes.NotAllowed, agent.Get<CommandVerifier>().Verify(Sign(shutdown)).RejectionCode);
    }

    [Fact]
    public async Task A_command_runs_once_and_a_replay_is_refused_even_after_a_restart()
    {
        var signed = Sign(Envelope());
        await Processor.ProcessAsync(signed, CancellationToken.None);
        await Processor.DrainAsync();
        var result = await _client.WaitForResultAsync();
        Assert.Equal(CommandOutcome.Succeeded, result.Outcome);
        Assert.Contains("uptimeSeconds", result.Output, StringComparison.Ordinal);

        // Same bytes delivered again, by a new processor instance and a fresh store reading the persisted file.
        var freshStore = new CommandReplayStore(_agent.Get<Microsoft.Extensions.Options.IOptions<Solvix.Agent.Core.Configuration.AgentOptions>>(), _agent.Time);
        Assert.True(freshStore.HasSeen(RemoteEnvelope(signed).ExecutionId, RemoteEnvelope(signed).Nonce));
        Assert.Equal(CommandRejectionCodes.Replayed, Verifier.Verify(signed).RejectionCode);
        await Processor.ProcessAsync(signed, CancellationToken.None);
        Assert.Single(_client.Results);
        Assert.Equal(CommandRejectionCodes.Replayed, _client.Acks.Last().Ack.RejectionCode);

        // A different execution that reuses the nonce is a replay as well.
        Assert.Equal(CommandRejectionCodes.Replayed, Verifier.Verify(Sign(Envelope() with { Nonce = RemoteEnvelope(signed).Nonce })).RejectionCode);
    }

    [Fact]
    public async Task A_command_the_server_no_longer_accepts_is_never_executed()
    {
        _client.AckFailure = new AgentApiException(AgentApiFailure.Rejected, HttpStatusCode.Conflict, "command_not_executable", "409");
        await Processor.ProcessAsync(Sign(Envelope()), CancellationToken.None);
        await Processor.DrainAsync();
        Assert.Empty(_client.Results);
    }

    [Fact]
    public async Task Rejected_commands_are_reported_with_their_reason_and_not_run()
    {
        await Processor.ProcessAsync(Sign(Envelope() with { DeviceId = Guid.NewGuid() }), CancellationToken.None);
        var ack = Assert.Single(_client.Acks);
        Assert.False(ack.Ack.Accepted);
        Assert.Equal(CommandRejectionCodes.WrongDevice, ack.Ack.RejectionCode);
        Assert.Empty(_client.Results);
    }

    [Fact]
    public async Task Privileged_operations_fail_cleanly_without_the_executor_service()
    {
        var restart = Envelope() with
        {
            CommandType = CommandTypes.Restart,
            ApprovalStatus = CommandApprovalStatus.Approved,
            ApprovalId = Guid.NewGuid(),
            ApprovedBy = Guid.NewGuid(),
        };
        await Processor.ProcessAsync(Sign(restart), CancellationToken.None);
        var result = await _client.WaitForResultAsync();
        Assert.Equal(CommandOutcome.Failed, result.Outcome);
        Assert.Equal(UnavailablePrivilegedOperationHost.ErrorCode, result.ErrorCode);
    }

    [Fact]
    public async Task A_signed_cancel_stops_a_running_command()
    {
        var blocking = new BlockingHandler();
        var registry = new CommandHandlerRegistry([blocking]);
        var custom = new CommandProcessor(
            _client, new CommandVerifier(Keys, _agent.Get<CommandReplayStore>(), Store, registry, _agent.Get<Microsoft.Extensions.Options.IOptions<Solvix.Agent.Core.Configuration.AgentOptions>>(), _agent.Time),
            Keys, _agent.Get<CommandReplayStore>(), registry, _agent.Get<IPrivilegedOperationHost>(), Store,
            _agent.Get<Solvix.Agent.Core.Security.DeviceCredentialStore>(),
            _agent.Get<Microsoft.Extensions.Options.IOptions<Solvix.Agent.Core.Configuration.AgentOptions>>(), _agent.Time,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CommandProcessor>.Instance);

        var running = Envelope() with { CommandType = CommandTypes.Processes };
        await custom.ProcessAsync(Sign(running), CancellationToken.None);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(running.ExecutionId, custom.Running);

        var cancel = Envelope() with { CommandType = CommandTypes.Cancel, Parameters = new Dictionary<string, string> { ["executionId"] = running.ExecutionId.ToString("D") } };
        await custom.ProcessAsync(Sign(cancel), CancellationToken.None);
        await custom.DrainAsync();

        Assert.Contains(_client.Results, r => r.ExecutionId == cancel.ExecutionId && r.Report.Output == "{\"cancelled\":true}");
        Assert.Contains(_client.Results, r => r.ExecutionId == running.ExecutionId && r.Report.Outcome == CommandOutcome.Cancelled);
    }

    private static CommandEnvelope RemoteEnvelope(SignedCommand signed) => CommandSignature.Deserialize(Base64Url.DecodeFromChars(signed.Envelope))!;

    private CommandEnvelope Envelope()
    {
        var now = _agent.Time.GetUtcNow();
        return new CommandEnvelope
        {
            ExecutionId = Guid.CreateVersion7(),
            TenantId = _agent.Api.TenantId,
            DeviceId = _agent.Api.DeviceId,
            RequestedBy = Guid.NewGuid(),
            CommandType = CommandTypes.Uptime,
            Sequence = 1,
            Nonce = CommandSignature.NewNonce(),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
            Authorization = CommandAuthorization.Authorized,
            ApprovalStatus = CommandApprovalStatus.NotRequired,
            AuditId = Guid.NewGuid(),
            KeyId = _client.KeyId,
        };
    }

    private SignedCommand Sign(CommandEnvelope envelope) => CommandSignature.Sign(envelope, _serverKey);

    private sealed class BlockingHandler : ICommandHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string CommandType => CommandTypes.Processes;

        public async Task<CommandHandlerResult> ExecuteAsync(CommandEnvelope envelope, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return CommandHandlerResult.Fail("unreachable", "unreachable");
        }
    }
}

public sealed class FakeCommandClient(ECDsa key) : IAgentCommandClient
{
    private readonly TaskCompletionSource<CommandResultReport> _firstResult = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string KeyId { get; } = "cmd-test";

    public CommandKeySet KeySet => new() { Keys = [new CommandSigningKey { KeyId = KeyId, PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) }] };

    public Queue<SignedCommand> Pending { get; } = new();

    public List<(Guid ExecutionId, CommandAcknowledgement Ack)> Acks { get; } = [];

    public List<(Guid ExecutionId, CommandResultReport Report)> Results { get; } = [];

    public AgentApiException? AckFailure { get; set; }

    public Task<AgentCommandBatch> FetchCommandsAsync(int waitSeconds, CancellationToken cancellationToken)
    {
        var batch = Pending.ToList();
        Pending.Clear();
        return Task.FromResult(new AgentCommandBatch { Commands = batch });
    }

    public Task AcknowledgeCommandAsync(Guid executionId, CommandAcknowledgement acknowledgement, CancellationToken cancellationToken)
    {
        lock (Acks)
        {
            Acks.Add((executionId, acknowledgement));
        }

        return AckFailure is { } failure && acknowledgement.Accepted ? Task.FromException(failure) : Task.CompletedTask;
    }

    public Task ReportCommandResultAsync(Guid executionId, CommandResultReport report, CancellationToken cancellationToken)
    {
        lock (Results)
        {
            Results.Add((executionId, report));
        }

        _firstResult.TrySetResult(report);
        return Task.CompletedTask;
    }

    public Task<CommandKeySet> GetCommandKeysAsync(CancellationToken cancellationToken) => Task.FromResult(KeySet);

    public Task<CommandResultReport> WaitForResultAsync() => _firstResult.Task.WaitAsync(TimeSpan.FromSeconds(10));
}
