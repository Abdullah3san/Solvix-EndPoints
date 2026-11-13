namespace Solvix.Agent.Tests;

using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts.RemoteManagement;
using Solvix.Agent.Core.Configuration;
using Solvix.Agent.Core.RemoteManagement;
using Solvix.Agent.Core.RemoteManagement.Privileged;
using Solvix.Agent.Core.Security;
using Solvix.Agent.Core.State;

/// <summary>The privileged Remote Executor: independent verification, the local pipe and the full chain from the agent.</summary>
public sealed class RemoteExecutorTests : IDisposable
{
    private readonly ECDsa _serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly FakePower _power = new();
    private readonly AgentHarness _agent;
    private readonly string _pipe = "solvix-executor-test-" + Guid.NewGuid().ToString("N");

    public RemoteExecutorTests()
    {
        _agent = new AgentHarness(o => o.RemoteManagement.ExecutorPipeName = _pipe);
        _agent.Get<IAgentStateStore>().Save(new AgentState { DeviceId = _agent.Api.DeviceId, TenantId = _agent.Api.TenantId, KeyName = "k" });
        Assert.True(_agent.Get<CommandKeyStore>().PinIfEmpty(KeySet));
    }

    private CommandKeySet KeySet => new() { Keys = [new CommandSigningKey { KeyId = "cmd-test", PublicKey = Convert.ToBase64String(_serverKey.ExportSubjectPublicKeyInfo()) }] };

    private PrivilegedExecutor NewExecutor() => new(
        _power, _agent.Get<CommandKeyStore>(), _agent.Get<IAgentStateStore>(), _agent.Get<IOptions<AgentOptions>>(), _agent.Time, NullLogger<PrivilegedExecutor>.Instance);

    public void Dispose()
    {
        _agent.Dispose();
        _serverKey.Dispose();
    }

    [Fact]
    public async Task Restart_shutdown_logoff_and_lock_run_through_the_power_operations()
    {
        var executor = NewExecutor();
        var restart = await executor.ExecuteAsync(Sign(Envelope(CommandTypes.Restart, ("delaySeconds", "30"), ("message", "Updates"))), default);
        Assert.Equal(CommandOutcome.Succeeded, restart.Outcome);
        Assert.Equal(("restart", TimeSpan.FromSeconds(30), "Updates"), _power.Calls[0]);

        Assert.Equal(CommandOutcome.Succeeded, (await executor.ExecuteAsync(Sign(Envelope(CommandTypes.Shutdown)), default)).Outcome);
        Assert.Equal(CommandOutcome.Succeeded, (await executor.ExecuteAsync(Sign(Envelope(CommandTypes.Logoff)), default)).Outcome);
        Assert.Equal(CommandOutcome.Succeeded, (await executor.ExecuteAsync(Sign(Envelope(CommandTypes.Lock)), default)).Outcome);
        Assert.Equal(["restart", "shutdown", "logoff", "lock"], _power.Calls.Select(c => c.Operation));
    }

    [Fact]
    public async Task The_executor_refuses_what_the_agent_would_refuse_without_trusting_the_agent()
    {
        var executor = NewExecutor();
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Equal(CommandRejectionCodes.InvalidSignature, (await executor.ExecuteAsync(CommandSignature.Sign(Envelope(CommandTypes.Restart), attacker), default)).RejectionCode);
        Assert.Equal(CommandRejectionCodes.WrongTenant, (await executor.ExecuteAsync(Sign(Envelope(CommandTypes.Restart) with { TenantId = Guid.NewGuid() }), default)).RejectionCode);
        Assert.Equal(CommandRejectionCodes.WrongDevice, (await executor.ExecuteAsync(Sign(Envelope(CommandTypes.Restart) with { DeviceId = Guid.NewGuid() }), default)).RejectionCode);
        var now = _agent.Time.GetUtcNow();
        Assert.Equal(CommandRejectionCodes.Expired, (await executor.ExecuteAsync(Sign(Envelope(CommandTypes.Restart) with { CreatedAtUtc = now.AddHours(-2), ExpiresAtUtc = now.AddMinutes(-5) }), default)).RejectionCode);
        Assert.Equal(CommandRejectionCodes.NotApproved, (await executor.ExecuteAsync(Sign(Envelope(CommandTypes.Restart) with { ApprovalStatus = "Pending" }), default)).RejectionCode);

        // Only the four privileged types, never cancel or read-only commands.
        Assert.Equal(CommandRejectionCodes.NotAllowed, (await executor.ExecuteAsync(Sign(Envelope(CommandTypes.Cancel)), default)).RejectionCode);
        Assert.Equal(CommandRejectionCodes.NotAllowed, (await executor.ExecuteAsync(Sign(Envelope(CommandTypes.Uptime)), default)).RejectionCode);
        Assert.Empty(_power.Calls);
    }

    [Fact]
    public async Task The_executor_has_its_own_replay_protection_that_survives_a_restart()
    {
        var signed = Sign(Envelope(CommandTypes.Restart));
        Assert.Equal(CommandOutcome.Succeeded, (await NewExecutor().ExecuteAsync(signed, default)).Outcome);

        // The agent's replay store is irrelevant: a new executor instance reads its persisted store.
        Assert.Equal(CommandRejectionCodes.Replayed, (await NewExecutor().ExecuteAsync(signed, default)).RejectionCode);
        Assert.Single(_power.Calls);
    }

    [Fact]
    public async Task Tampering_with_the_agents_identity_or_keys_after_first_use_does_not_redirect_the_executor()
    {
        var executor = NewExecutor();
        Assert.Equal(CommandOutcome.Succeeded, (await executor.ExecuteAsync(Sign(Envelope(CommandTypes.Lock)), default)).Outcome);

        // A compromised agent rewrites its own state and key files...
        var otherTenant = Guid.NewGuid();
        var otherDevice = Guid.NewGuid();
        _agent.Get<IAgentStateStore>().Save(new AgentState { DeviceId = otherDevice, TenantId = otherTenant, KeyName = "k" });
        var attackerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.Delete(Path.Combine(_agent.DataDirectory, "command-keys.json"));
        var tampered = new CommandKeyStore(_agent.Get<IOptions<AgentOptions>>());
        tampered.PinIfEmpty(new CommandKeySet { Keys = [new CommandSigningKey { KeyId = "cmd-test", PublicKey = Convert.ToBase64String(attackerKey.ExportSubjectPublicKeyInfo()) }] });

        // ...and the executor still only accepts the original identity and key.
        var fresh = NewExecutor();
        Assert.Equal(CommandRejectionCodes.InvalidSignature, (await fresh.ExecuteAsync(CommandSignature.Sign(Envelope(CommandTypes.Lock), attackerKey), default)).RejectionCode);
        Assert.Equal(CommandRejectionCodes.WrongTenant, (await fresh.ExecuteAsync(Sign(Envelope(CommandTypes.Lock) with { TenantId = otherTenant, DeviceId = otherDevice }), default)).RejectionCode);
        Assert.Equal(CommandOutcome.Succeeded, (await fresh.ExecuteAsync(Sign(Envelope(CommandTypes.Lock)), default)).Outcome);
    }

    [Fact]
    public async Task The_executor_revalidates_parameters_itself()
    {
        var executor = NewExecutor();
        foreach (var parameters in new[] { new[] { ("delaySeconds", "99999") }, new[] { ("delaySeconds", "-1") }, new[] { ("message", "bad\u0007") }, new[] { ("command", "calc.exe") } })
        {
            var result = await executor.ExecuteAsync(Sign(Envelope(CommandTypes.Restart, parameters)), default);
            Assert.Equal(CommandOutcome.Failed, result.Outcome);
            Assert.Equal("invalid_parameter", result.ErrorCode);
        }

        Assert.Equal("invalid_parameter", (await executor.ExecuteAsync(Sign(Envelope(CommandTypes.Lock, ("delaySeconds", "5"))), default)).ErrorCode);
        Assert.Empty(_power.Calls);
    }

    [Fact]
    public async Task Failures_of_the_operating_system_are_reported_not_thrown()
    {
        _power.Failure = new PowerOperationException("no_active_session", "Nobody is signed in at the console.");
        var result = await NewExecutor().ExecuteAsync(Sign(Envelope(CommandTypes.Lock)), default);
        Assert.Equal(CommandOutcome.Failed, result.Outcome);
        Assert.Equal("no_active_session", result.ErrorCode);
    }

    [Fact]
    public async Task Agent_to_executor_over_the_pipe_end_to_end_and_unavailable_executor_fails_cleanly()
    {
        var host = new PipePrivilegedOperationHost(_agent.Get<IOptions<AgentOptions>>());
        var envelope = Envelope(CommandTypes.Restart, ("delaySeconds", "10"));
        var context = new SignedCommandContext(Sign(envelope), envelope);

        // No executor running: a clean, specific failure.
        var unavailable = await host.ExecuteAsync(context, default);
        Assert.Equal(UnavailablePrivilegedOperationHost.ErrorCode, unavailable.ErrorCode);

        using var server = new PrivilegedExecutorServer(NewExecutor(), new CurrentUserPipeFactory(), _agent.Get<IOptions<AgentOptions>>(), NullLogger<PrivilegedExecutorServer>.Instance);
        await server.StartAsync(default);
        try
        {
            var ok = await host.ExecuteAsync(context, default);
            Assert.Equal(CommandOutcome.Succeeded, ok.Outcome);
            Assert.Equal(("restart", TimeSpan.FromSeconds(10), null), _power.Calls.Single());

            // Same bytes again: refused by the executor's own replay protection.
            var replay = await host.ExecuteAsync(context, default);
            Assert.Equal("executor_rejected_" + CommandRejectionCodes.Replayed, replay.ErrorCode);
            Assert.Single(_power.Calls);

            // A forged command through the pipe is refused too.
            using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var forged = Envelope(CommandTypes.Shutdown);
            var refused = await host.ExecuteAsync(new SignedCommandContext(CommandSignature.Sign(forged, attacker), forged), default);
            Assert.Equal("executor_rejected_" + CommandRejectionCodes.InvalidSignature, refused.ErrorCode);
            Assert.Single(_power.Calls);
        }
        finally
        {
            await server.StopAsync(default);
        }
    }

    [Fact]
    public async Task The_full_chain_agent_processor_to_executor_to_power_operation()
    {
        var client = new FakeCommandClient(_serverKey);
        var registry = _agent.Get<CommandHandlerRegistry>();
        var processor = new CommandProcessor(
            client, _agent.Get<CommandVerifier>(), _agent.Get<CommandKeyStore>(), _agent.Get<CommandReplayStore>(), registry,
            new PipePrivilegedOperationHost(_agent.Get<IOptions<AgentOptions>>()), _agent.Get<IAgentStateStore>(),
            _agent.Get<DeviceCredentialStore>(), _agent.Get<IOptions<AgentOptions>>(), _agent.Time, NullLogger<CommandProcessor>.Instance);
        using var server = new PrivilegedExecutorServer(NewExecutor(), new CurrentUserPipeFactory(), _agent.Get<IOptions<AgentOptions>>(), NullLogger<PrivilegedExecutorServer>.Instance);
        await server.StartAsync(default);
        try
        {
            await processor.ProcessAsync(Sign(Envelope(CommandTypes.Lock)), default);
            var result = await client.WaitForResultAsync();
            Assert.Equal(CommandOutcome.Succeeded, result.Outcome);
            Assert.Equal("lock", Assert.Single(_power.Calls).Operation);
        }
        finally
        {
            await server.StopAsync(default);
        }
    }

    private CommandEnvelope Envelope(string type, params (string Key, string Value)[] parameters)
    {
        var now = _agent.Time.GetUtcNow();
        return new CommandEnvelope
        {
            ExecutionId = Guid.CreateVersion7(),
            TenantId = _agent.Api.TenantId,
            DeviceId = _agent.Api.DeviceId,
            RequestedBy = Guid.NewGuid(),
            CommandType = type,
            Parameters = parameters.ToDictionary(p => p.Key, p => p.Value),
            Sequence = 1,
            Nonce = CommandSignature.NewNonce(),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
            Authorization = CommandAuthorization.Authorized,
            ApprovalStatus = CommandApprovalStatus.NotRequired,
            AuditId = Guid.NewGuid(),
            KeyId = "cmd-test",
        };
    }

    private SignedCommand Sign(CommandEnvelope envelope) => CommandSignature.Sign(envelope, _serverKey);

    private sealed class FakePower : IPowerOperations
    {
        public List<(string Operation, TimeSpan Delay, string? Message)> Calls { get; } = [];

        public PowerOperationException? Failure { get; set; }

        public Task RestartAsync(TimeSpan delay, string? message, CancellationToken cancellationToken) => Record("restart", delay, message);

        public Task ShutdownAsync(TimeSpan delay, string? message, CancellationToken cancellationToken) => Record("shutdown", delay, message);

        public Task LogOffAsync(CancellationToken cancellationToken) => Record("logoff", TimeSpan.Zero, null);

        public Task LockAsync(CancellationToken cancellationToken) => Record("lock", TimeSpan.Zero, null);

        private Task Record(string operation, TimeSpan delay, string? message)
        {
            if (Failure is { } failure)
            {
                throw failure;
            }

            Calls.Add((operation, delay, message));
            return Task.CompletedTask;
        }
    }
}
