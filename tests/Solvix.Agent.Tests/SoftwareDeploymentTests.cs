namespace Solvix.Agent.Tests;

using System.Buffers.Text;
using System.IO.Pipes;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts;
using Solvix.Agent.Contracts.RemoteManagement;
using Solvix.Agent.Contracts.Software;
using Solvix.Agent.Core.Communication;
using Solvix.Agent.Core.Configuration;
using Solvix.Agent.Core.RemoteManagement;
using Solvix.Agent.Core.Software;
using Solvix.Agent.Core.State;
using Solvix.Agent.Core.Updates;

/// <summary>
/// Software deployment on the device: the unprivileged handler (detection, download, verification,
/// exit codes) and the privileged executor (independent verification, fixed command lines), plus the
/// real process runner and the named pipe between the two.
/// </summary>
public sealed class SoftwareDeploymentTests : IDisposable
{
    private const string ProductCode = "{0A1B2C3D-1111-2222-3333-444455556666}";
    private static readonly byte[] PackageBytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("solvix-package-content-", 4096)));

    private readonly ECDsa _serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly FakeCommandClient _commandClient;
    private readonly AgentHarness _agent;
    private readonly FakeApplications _apps = new();
    private readonly FakePackageClient _packages = new(PackageBytes);
    private readonly FakeExecutor _executor = new();
    private readonly FakeSignatureVerifier _signatures = new();

    public SoftwareDeploymentTests()
    {
        _commandClient = new FakeCommandClient(_serverKey);
        _agent = new AgentHarness();
        _agent.Get<IAgentStateStore>().Save(new AgentState { DeviceId = _agent.Api.DeviceId, TenantId = _agent.Api.TenantId, KeyName = "k" });
        Assert.True(_agent.Get<CommandKeyStore>().PinIfEmpty(_commandClient.KeySet));

        // A successful installation makes the application appear.
        _executor.OnRun = request => _apps.Items.Add(new DetectedApplication("Contoso Notes", "5.2.0", "Contoso Ltd", ProductCode));
    }

    private static string Sha256 => Convert.ToHexStringLower(SHA256.HashData(PackageBytes));

    public void Dispose()
    {
        _agent.Dispose();
        _serverKey.Dispose();
    }

    [Fact]
    public async Task An_msi_is_downloaded_verified_installed_and_confirmed_by_detection()
    {
        var result = await RunAsync(Operation());

        Assert.Equal(CommandOutcome.Succeeded, result.Outcome);
        var output = Output(result);
        Assert.Equal(SoftwareOutcome.Installed, output.Outcome);
        Assert.Equal("5.2.0", output.DetectedVersion);
        Assert.True(output.PackageVerified);
        Assert.False(output.RebootRequired);

        var request = Assert.Single(_executor.Requests);
        Assert.Equal(PackageBytes, request.StagedContent);
        Assert.Equal(_agent.Api.DeviceId, request.Request.DeviceId);
        Assert.Equal(_commandClient.KeyId, Assert.Single(request.Request.Keys.Keys).KeyId);

        // The staged copy is removed afterwards.
        Assert.False(File.Exists(request.Request.PackagePath));
        Assert.Equal([0L], _packages.Offsets);
    }

    [Fact]
    public async Task An_application_that_is_already_installed_is_not_downloaded_or_installed_again()
    {
        _apps.Items.Add(new DetectedApplication("Contoso Notes", "5.2.1", "Contoso Ltd", ProductCode));

        var result = await RunAsync(Operation());

        Assert.Equal(CommandOutcome.Succeeded, result.Outcome);
        Assert.Equal(SoftwareOutcome.AlreadyInstalled, Output(result).Outcome);
        Assert.Empty(_packages.Offsets);
        Assert.Empty(_executor.Requests);
    }

    [Fact]
    public async Task An_older_version_is_upgraded()
    {
        _apps.Items.Add(new DetectedApplication("Contoso Notes", "4.9", "Contoso Ltd", ProductCode));
        _executor.OnRun = _ =>
        {
            _apps.Items.Clear();
            _apps.Items.Add(new DetectedApplication("Contoso Notes", "5.2.0", "Contoso Ltd", ProductCode));
        };

        var result = await RunAsync(Operation());
        Assert.Equal(SoftwareOutcome.Installed, Output(result).Outcome);
        Assert.Single(_executor.Requests);
    }

    [Fact]
    public async Task A_corrupted_package_is_refused_before_anything_runs()
    {
        var corrupted = (byte[])PackageBytes.Clone();
        corrupted[100] ^= 0xFF;
        _packages.Content = corrupted;

        var result = await RunAsync(Operation());

        Assert.Equal(CommandOutcome.Failed, result.Outcome);
        Assert.Equal(SoftwareErrorCodes.HashMismatch, result.ErrorCode);
        Assert.Equal("download", Output(result).FailedPhase);
        Assert.Empty(_executor.Requests);
        Assert.Empty(Directory.GetFiles(Staging));
    }

    [Fact]
    public async Task An_interrupted_download_is_resumed_where_it_stopped()
    {
        _packages.FailAfterBytes = PackageBytes.Length / 3;

        var result = await RunAsync(Operation());

        Assert.Equal(CommandOutcome.Succeeded, result.Outcome);
        Assert.Equal([0L, PackageBytes.Length / 3], _packages.Offsets);
        Assert.Equal(PackageBytes, Assert.Single(_executor.Requests).StagedContent);
    }

    [Fact]
    public async Task A_download_interrupted_by_an_agent_restart_is_resumed_by_the_next_attempt()
    {
        // The first attempt dies with the agent after a third of the file (nothing retried in-process).
        _packages.FailAfterBytes = PackageBytes.Length / 3;
        _packages.FailTimes = 10;
        var first = await RunAsync(Operation(), downloadAttempts: 1);
        Assert.Equal(SoftwareErrorCodes.DownloadFailed, first.ErrorCode);
        Assert.Single(Directory.GetFiles(Staging, "*.part"));

        // A later attempt (new command, same package) continues from the partial file.
        _packages.FailTimes = 0;
        var second = await RunAsync(Operation());
        Assert.Equal(CommandOutcome.Succeeded, second.Outcome);
        Assert.Equal(PackageBytes.Length / 3, _packages.Offsets.Last());
    }

    [Fact]
    public async Task A_package_the_server_no_longer_provides_fails_without_retrying_locally()
    {
        _packages.Refuse = HttpStatusCode.Conflict;
        var result = await RunAsync(Operation());
        Assert.Equal(SoftwareErrorCodes.PackageUnavailable, result.ErrorCode);
        Assert.Single(_packages.Offsets);
    }

    [Fact]
    public async Task Without_the_executor_service_the_failure_is_reported_as_retryable()
    {
        _executor.Response = ExecutorResponse.Refuse(SoftwareErrorCodes.ExecutorUnavailable, "not installed");
        var result = await RunAsync(Operation());
        Assert.Equal(SoftwareErrorCodes.ExecutorUnavailable, result.ErrorCode);
        Assert.Contains(SoftwareErrorCodes.ExecutorUnavailable, SoftwareErrorCodes.Retryable);
    }

    [Theory]
    [InlineData(3010, true, null)]
    [InlineData(1641, true, null)]
    [InlineData(1618, false, SoftwareErrorCodes.AnotherInstallInProgress)]
    [InlineData(1603, false, SoftwareErrorCodes.InstallerFailed)]
    public async Task Installer_exit_codes_are_interpreted(int exitCode, bool succeeds, string? errorCode)
    {
        _executor.Response = new ExecutorResponse { Executed = true, ExitCode = exitCode, Log = "MSI (s) log line" };
        var result = await RunAsync(Operation());

        Assert.Equal(succeeds ? CommandOutcome.Succeeded : CommandOutcome.Failed, result.Outcome);
        Assert.Equal(errorCode, result.ErrorCode);
        Assert.Equal(exitCode, result.ExitCode);
        var output = Output(result);
        Assert.Equal(exitCode, output.InstallerExitCode);
        Assert.Equal(succeeds, output.RebootRequired);
        Assert.Equal("MSI (s) log line", output.LogExcerpt);
        if (exitCode == 1603)
        {
            Assert.Contains("fatal error", result.ErrorMessage, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_reported_success_that_detection_cannot_confirm_is_a_failure()
    {
        _executor.OnRun = _ => { };
        var result = await RunAsync(Operation());
        Assert.Equal(SoftwareErrorCodes.NotDetectedAfterInstall, result.ErrorCode);
        Assert.Equal("detect", Output(result).FailedPhase);
    }

    [Fact]
    public async Task A_timed_out_or_cancelled_installer_is_reported_as_such()
    {
        _executor.Response = new ExecutorResponse { Executed = true, TimedOut = true };
        Assert.Equal(SoftwareErrorCodes.InstallerTimeout, (await RunAsync(Operation())).ErrorCode);

        _executor.Response = new ExecutorResponse { Executed = true, Cancelled = true };
        Assert.Equal(CommandOutcome.Cancelled, (await RunAsync(Operation())).Outcome);
    }

    [Fact]
    public async Task Uninstall_by_product_code_runs_only_when_the_product_is_present()
    {
        var uninstall = Operation() with { RequiresPackage = false, Sha256 = null, SizeBytes = 0, Arguments = [], RequireSignature = false, ExpectedSignerThumbprint = null };

        var absent = await RunAsync(uninstall, CommandTypes.SoftwareUninstall);
        Assert.Equal(SoftwareOutcome.NotInstalled, Output(absent).Outcome);
        Assert.Empty(_executor.Requests);

        _apps.Items.Add(new DetectedApplication("Contoso Notes", "5.2.0", "Contoso Ltd", ProductCode));
        _executor.OnRun = _ => _apps.Items.Clear();
        var removed = await RunAsync(uninstall, CommandTypes.SoftwareUninstall);
        Assert.Equal(SoftwareOutcome.Uninstalled, Output(removed).Outcome);
        Assert.Null(Assert.Single(_executor.Requests).Request.PackagePath);

        // msiexec 1605 (unknown product) means it is already gone.
        _apps.Items.Add(new DetectedApplication("Contoso Notes", "5.2.0", "Contoso Ltd", ProductCode));
        _executor.Response = new ExecutorResponse { Executed = true, ExitCode = 1605 };
        Assert.Equal(CommandOutcome.Succeeded, (await RunAsync(uninstall, CommandTypes.SoftwareUninstall)).Outcome);
    }

    [Fact]
    public async Task Detection_reports_the_installed_version_without_running_anything()
    {
        _apps.Items.Add(new DetectedApplication("Contoso Notes", "5.1.9", "Contoso Ltd", ProductCode));
        var result = Output(await RunAsync(Operation() with { RequiresPackage = false, Sha256 = null, SizeBytes = 0, RequireSignature = false, ExpectedSignerThumbprint = null }, CommandTypes.SoftwareDetect));
        Assert.Equal(SoftwareOutcome.Detected, result.Outcome);
        Assert.Equal("5.1.9", result.DetectedVersion);
        Assert.False(result.DetectionSatisfied); // 5.2.0 required
        Assert.Empty(_executor.Requests);
    }

    [Fact]
    public async Task Signatures_and_local_policy_are_enforced_on_the_device()
    {
        // Signed at upload: the device requires a valid signature from the same signer.
        _signatures.Problem = "Signer 'CN=Mallory' is not a trusted publisher.";
        var signed = await RunAsync(Operation());
        Assert.Equal(SoftwareErrorCodes.SignatureInvalid, signed.ErrorCode);
        Assert.Equal([Thumbprint], _signatures.Pins.Last());
        Assert.Empty(_executor.Requests);

        // Unsigned packages can be refused by local policy.
        var unsigned = Operation() with { RequireSignature = false, ExpectedSignerThumbprint = null };
        Assert.Equal(SoftwareErrorCodes.UnsignedNotAllowed, (await RunAsync(unsigned, configure: o => o.Software.AllowUnsignedPackages = false)).ErrorCode);

        // Installer types and the whole feature can be switched off locally.
        Assert.Equal(SoftwareErrorCodes.InstallerTypeNotAllowed, (await RunAsync(Operation(), configure: o => o.Software.AllowedInstallerTypes = ["Exe"])).ErrorCode);
        Assert.Equal(SoftwareErrorCodes.DisabledLocally, (await RunAsync(Operation(), configure: o => o.Software.Enabled = false)).ErrorCode);
    }

    [Fact]
    public async Task Parameters_that_do_not_validate_are_refused()
    {
        var parameters = new Dictionary<string, string>(Operation().ToParameters()) { ["arguments"] = "[\"/qn\"]" };
        var result = await RunRawAsync(CommandTypes.SoftwareInstall, parameters);
        Assert.Equal(SoftwareErrorCodes.InvalidParameters, result.ErrorCode);
        Assert.Empty(_executor.Requests);
    }

    [Fact]
    public async Task The_command_processor_runs_software_commands_with_their_signed_bytes_and_a_longer_limit()
    {
        var handler = Handler();
        var registry = new CommandHandlerRegistry([handler, .. _agent.Get<IEnumerable<ICommandHandler>>().Where(h => !SoftwareCommandHandler.Types.Contains(h.CommandType))]);
        var processor = new CommandProcessor(
            _commandClient, new CommandVerifier(_agent.Get<CommandKeyStore>(), _agent.Get<CommandReplayStore>(), _agent.Get<IAgentStateStore>(), registry, _agent.Get<IOptions<AgentOptions>>(), _agent.Time),
            _agent.Get<CommandKeyStore>(), _agent.Get<CommandReplayStore>(), registry, _agent.Get<IPrivilegedOperationHost>(), _agent.Get<IAgentStateStore>(),
            _agent.Get<Core.Security.DeviceCredentialStore>(), _agent.Get<IOptions<AgentOptions>>(), _agent.Time, NullLogger<CommandProcessor>.Instance);
        var signed = Sign(Envelope(CommandTypes.SoftwareInstall, Operation().ToParameters()));
        await processor.ProcessAsync(signed, CancellationToken.None);
        await processor.DrainAsync();

        var report = await _commandClient.WaitForResultAsync();
        Assert.Equal(CommandOutcome.Succeeded, report.Outcome);
        Assert.Equal(signed, Assert.Single(_executor.Requests).Request.Command);
        Assert.True(handler.MaxDuration(Peek(signed)) > CommandProcessor.MaxExecutionTime);
    }

    // --- privileged executor -------------------------------------------------------------------

    [Fact]
    public async Task The_executor_verifies_the_command_itself_copies_the_package_and_runs_a_fixed_command_line()
    {
        using var setup = new ExecutorSetup(this);
        var signed = Sign(Envelope(CommandTypes.SoftwareInstall, Operation().ToParameters()));

        var response = await setup.Executor.ExecuteAsync(setup.Request(signed, setup.Stage(PackageBytes)), CancellationToken.None);

        Assert.True(response.Executed, response.RefusalMessage);
        Assert.Equal(0, response.ExitCode);
        var command = Assert.Single(setup.Runner.Commands);
        Assert.EndsWith("msiexec.exe", command.FileName, StringComparison.Ordinal);
        Assert.Equal(["/i", command.Arguments[1], "/qn", "/norestart", "/l*v", command.Arguments[5], "REBOOT=ReallySuppress", "INSTALLDIR=C:\\Apps\\Notes", "ALLUSERS=1"], command.Arguments);

        // It ran the executor's own copy, inside the executor's directory, never the agent's staged file.
        Assert.StartsWith(setup.Directory, command.Arguments[1], StringComparison.Ordinal);
        Assert.Equal(PackageBytes, setup.Runner.PackageContent);
        Assert.False(Directory.Exists(Path.GetDirectoryName(command.Arguments[1])));
    }

    [Fact]
    public async Task The_executor_refuses_tampered_replayed_foreign_and_non_software_commands()
    {
        using var setup = new ExecutorSetup(this);
        var signed = Sign(Envelope(CommandTypes.SoftwareInstall, Operation().ToParameters()));

        // A compromised agent changes the arguments but cannot re-sign.
        var json = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(signed.Envelope)).Replace("ALLUSERS=1", "ALLUSERS=2", StringComparison.Ordinal);
        var tampered = signed with { Envelope = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json)) };
        Assert.Equal(CommandRejectionCodes.InvalidSignature, (await setup.Executor.ExecuteAsync(setup.Request(tampered, setup.Stage(PackageBytes)), CancellationToken.None)).RefusalCode);

        // The genuine command runs once; replaying it is refused.
        Assert.True((await setup.Executor.ExecuteAsync(setup.Request(signed, setup.Stage(PackageBytes)), CancellationToken.None)).Executed);
        Assert.Equal(CommandRejectionCodes.Replayed, (await setup.Executor.ExecuteAsync(setup.Request(signed, setup.Stage(PackageBytes)), CancellationToken.None)).RefusalCode);

        // A validly signed command for another device, or one that is not a software command, is refused.
        var foreign = Sign(Envelope(CommandTypes.SoftwareInstall, Operation().ToParameters()) with { DeviceId = Guid.NewGuid() });
        Assert.Equal(CommandRejectionCodes.WrongDevice, (await setup.Executor.ExecuteAsync(setup.Request(foreign, setup.Stage(PackageBytes)), CancellationToken.None)).RefusalCode);
        var restart = Sign(Envelope(CommandTypes.Restart, new Dictionary<string, string>()));
        Assert.Equal(CommandRejectionCodes.NotAllowed, (await setup.Executor.ExecuteAsync(setup.Request(restart, null), CancellationToken.None)).RefusalCode);

        // The identity and keys pinned on first use are never replaced by what a later request claims.
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var attackerKeys = new CommandKeySet { Keys = [new CommandSigningKey { KeyId = "cmd-attacker", PublicKey = Convert.ToBase64String(attacker.ExportSubjectPublicKeyInfo()) }] };
        var forged = CommandSignature.Sign(Envelope(CommandTypes.SoftwareInstall, Operation().ToParameters()) with { KeyId = "cmd-attacker" }, attacker);
        var forgedRequest = setup.Request(forged, setup.Stage(PackageBytes)) with { Keys = attackerKeys, DeviceId = Guid.NewGuid() };
        Assert.Equal(CommandRejectionCodes.UnknownKey, (await setup.Executor.ExecuteAsync(forgedRequest, CancellationToken.None)).RefusalCode);
        Assert.Single(setup.Runner.Commands);
    }

    [Fact]
    public async Task The_executor_refuses_a_package_swapped_after_the_agent_checked_it()
    {
        using var setup = new ExecutorSetup(this);
        var swapped = (byte[])PackageBytes.Clone();
        swapped[^1] ^= 0x01; // same size, different content
        var signed = Sign(Envelope(CommandTypes.SoftwareInstall, Operation().ToParameters()));

        var response = await setup.Executor.ExecuteAsync(setup.Request(signed, setup.Stage(swapped)), CancellationToken.None);

        Assert.Equal(SoftwareErrorCodes.HashMismatch, response.RefusalCode);
        Assert.Empty(setup.Runner.Commands);
    }

    [Fact]
    public void Installer_command_lines_are_fixed_by_the_executor()
    {
        var msiInstall = InstallerCommandLine.Build(CommandTypes.SoftwareInstall, Operation(), "/w/package.msi", "/w", "/sys");
        Assert.Equal(Path.Combine("/sys", "msiexec.exe"), msiInstall.FileName);
        Assert.Equal("/i", msiInstall.Arguments[0]);

        var msiRemove = InstallerCommandLine.Build(CommandTypes.SoftwareUninstall, Operation() with { Arguments = [] }, null, "/w", "/sys");
        Assert.Equal(["/x", ProductCode, "/qn", "/norestart", "/l*v", Path.Combine("/w", "installer.log"), "REBOOT=ReallySuppress"], msiRemove.Arguments);

        var exe = InstallerCommandLine.Build(CommandTypes.SoftwareInstall, Operation() with { InstallerType = InstallerType.Exe, Arguments = ["/VERYSILENT", "/NORESTART"] }, "/w/setup.exe", "/w", "/sys");
        Assert.Equal("/w/setup.exe", exe.FileName);
        Assert.Equal(["/VERYSILENT", "/NORESTART"], exe.Arguments);
        Assert.Null(exe.LogPath);
    }

    [Fact]
    public async Task The_process_runner_reports_exit_codes_and_stops_installers_that_run_too_long()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runner = new ProcessInstallerRunner();
        var work = Path.GetTempPath();
        var exit = await runner.RunAsync(new InstallerCommand("/bin/sh", ["-c", "exit 3"], work, null), TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(new InstallerRunResult(3, false, false), exit);

        var started = DateTime.UtcNow;
        var slow = await runner.RunAsync(new InstallerCommand("/bin/sh", ["-c", "sleep 30"], work, null), TimeSpan.FromMilliseconds(500), CancellationToken.None);
        Assert.True(slow.TimedOut);
        Assert.Null(slow.ExitCode);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));

        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var cancelled = await runner.RunAsync(new InstallerCommand("/bin/sh", ["-c", "sleep 30"], work, null), TimeSpan.FromSeconds(30), cancel.Token);
        Assert.True(cancelled.Cancelled);
        Assert.False(cancelled.TimedOut);

        // Arguments are passed as data, never interpreted by a shell.
        var marker = Path.Combine(work, "solvix-" + Guid.NewGuid().ToString("N"));
        await runner.RunAsync(new InstallerCommand("/bin/echo", [$"; touch {marker}"], work, null), TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task The_agent_reaches_the_executor_over_its_named_pipe_and_disconnecting_stops_the_installer()
    {
        using var setup = new ExecutorSetup(this);
        var options = Options.Create(new AgentOptions { Software = new SoftwareAgentOptions { ExecutorPipeName = "solvix-test-" + Guid.NewGuid().ToString("N")[..12], ExecutorConnectTimeoutSeconds = 2 } });
        var client = new PipeSoftwareExecutorClient(options);

        // Not running: a retryable refusal, not an exception.
        var unavailable = await client.ExecuteAsync(setup.Request(Sign(Envelope(CommandTypes.SoftwareInstall, Operation().ToParameters())), null), CancellationToken.None);
        Assert.Equal(SoftwareErrorCodes.ExecutorUnavailable, unavailable.RefusalCode);

        var host = new SoftwareExecutorHost(setup.Executor, new DefaultExecutorPipeFactory(), options, NullLogger<SoftwareExecutorHost>.Instance);
        await host.StartAsync(CancellationToken.None);
        try
        {
            var response = await client.ExecuteAsync(setup.Request(Sign(Envelope(CommandTypes.SoftwareInstall, Operation().ToParameters())), setup.Stage(PackageBytes)), CancellationToken.None);
            Assert.True(response.Executed, response.RefusalMessage);
            Assert.Single(setup.Runner.Commands);

            // The agent gives up (its own timeout or a cancelled deployment): the executor stops the installer.
            setup.Runner.Block = true;
            using var cancel = new CancellationTokenSource();
            var pending = client.ExecuteAsync(setup.Request(Sign(Envelope(CommandTypes.SoftwareInstall, Operation().ToParameters())), setup.Stage(PackageBytes)), cancel.Token);
            await setup.Runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.True(await setup.Runner.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    // --- helpers ---------------------------------------------------------------------------------

    private const string Thumbprint = "A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4E5F60718293A4B5C6D7E8F90";

    private string Staging => Path.Combine(_agent.DataDirectory, "software", "staging");

    private static SoftwareOperation Operation() => new()
    {
        DeploymentId = Guid.CreateVersion7(),
        TargetId = Guid.CreateVersion7(),
        PackageId = Guid.CreateVersion7(),
        PackageName = "Contoso Notes",
        PackageVersion = "5.2.0",
        InstallerType = InstallerType.Msi,
        RequiresPackage = true,
        Sha256 = Sha256,
        SizeBytes = PackageBytes.Length,
        Arguments = ["INSTALLDIR=C:\\Apps\\Notes", "ALLUSERS=1"],
        Detection = new DetectionRule { Kind = DetectionKind.MsiProductCode, ProductCode = ProductCode, Version = "5.2.0", VersionOperator = VersionOperator.AtLeast },
        ExpectedSignerThumbprint = Thumbprint,
        RequireSignature = true,
        TimeoutSeconds = 3600,
    };

    private SoftwareCommandHandler Handler(string type = CommandTypes.SoftwareInstall, int downloadAttempts = 3, Action<AgentOptions>? configure = null)
    {
        var options = _agent.Get<IOptions<AgentOptions>>().Value;
        var copy = new AgentOptions
        {
            DataDirectory = options.DataDirectory,
            Software = new SoftwareAgentOptions { DownloadAttempts = downloadAttempts },
        };
        configure?.Invoke(copy);
        var wrapped = Options.Create(copy);
        return new SoftwareCommandHandler(
            type, _apps, new PackageDownloader(_packages, wrapped, new InstantTime(), NullLogger<PackageDownloader>.Instance), _signatures, _executor,
            _agent.Get<CommandKeyStore>(), _agent.Get<IAgentStateStore>(), wrapped, NullLogger<SoftwareCommandHandler>.Instance);
    }

    private Task<CommandHandlerResult> RunAsync(SoftwareOperation operation, string type = CommandTypes.SoftwareInstall, int downloadAttempts = 3, Action<AgentOptions>? configure = null) =>
        RunRawAsync(type, operation.ToParameters(), downloadAttempts, configure);

    private async Task<CommandHandlerResult> RunRawAsync(string type, IReadOnlyDictionary<string, string> parameters, int downloadAttempts = 3, Action<AgentOptions>? configure = null)
    {
        _executor.Requests.Clear();
        var signed = Sign(Envelope(type, parameters));
        return await Handler(type, downloadAttempts, configure).ExecuteAsync(new SignedCommandContext(signed, Peek(signed)), CancellationToken.None);
    }

    private static SoftwareOperationResult Output(CommandHandlerResult result) =>
        JsonSerializer.Deserialize(result.Output!, AgentJsonContext.Default.SoftwareOperationResult)!;

    private CommandEnvelope Envelope(string type, IReadOnlyDictionary<string, string> parameters)
    {
        var now = _agent.Time.GetUtcNow();
        return new CommandEnvelope
        {
            ExecutionId = Guid.CreateVersion7(),
            TenantId = _agent.Api.TenantId,
            DeviceId = _agent.Api.DeviceId,
            RequestedBy = Guid.NewGuid(),
            CommandType = type,
            Parameters = parameters,
            Sequence = 1,
            Nonce = CommandSignature.NewNonce(),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
            Authorization = CommandAuthorization.Authorized,
            ApprovalStatus = CommandApprovalStatus.Approved,
            ApprovalId = Guid.NewGuid(),
            ApprovedBy = Guid.NewGuid(),
            AuditId = Guid.NewGuid(),
            KeyId = _commandClient.KeyId,
        };
    }

    private SignedCommand Sign(CommandEnvelope envelope) => CommandSignature.Sign(envelope, _serverKey);

    private static CommandEnvelope Peek(SignedCommand signed) => CommandSignature.Deserialize(Base64Url.DecodeFromChars(signed.Envelope))!;

    /// <summary>A privileged executor with its own directory, a fake process runner and an accepting signature verifier.</summary>
    private sealed class ExecutorSetup : IDisposable
    {
        private readonly SoftwareDeploymentTests _test;

        public ExecutorSetup(SoftwareDeploymentTests test)
        {
            _test = test;
            Directory = Path.Combine(Path.GetTempPath(), "solvix-executor-test-" + Guid.NewGuid().ToString("N"));
            var options = Options.Create(new AgentOptions { Software = new SoftwareAgentOptions { ExecutorDataDirectory = Directory } });
            Executor = new SoftwareExecutor(options, new FakeSignatureVerifier(), Runner, test._agent.Time, NullLogger<SoftwareExecutor>.Instance);
        }

        public string Directory { get; }

        public RecordingRunner Runner { get; } = new();

        public SoftwareExecutor Executor { get; }

        public string Stage(byte[] content)
        {
            var path = Path.Combine(_test.Staging, Guid.NewGuid().ToString("N") + ".pkg");
            System.IO.Directory.CreateDirectory(_test.Staging);
            File.WriteAllBytes(path, content);
            return path;
        }

        public ExecutorRequest Request(SignedCommand command, string? package) => new()
        {
            Command = command,
            PackagePath = package,
            Keys = _test._commandClient.KeySet,
            TenantId = _test._agent.Api.TenantId,
            DeviceId = _test._agent.Api.DeviceId,
        };

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, true);
            }
        }
    }

    private sealed class RecordingRunner : IInstallerProcessRunner
    {
        public List<InstallerCommand> Commands { get; } = [];

        public byte[]? PackageContent { get; private set; }

        public bool Block { get; set; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<InstallerRunResult> RunAsync(InstallerCommand command, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            var package = command.Arguments.FirstOrDefault(a => a.EndsWith("package.msi", StringComparison.Ordinal));
            PackageContent = package is null ? null : await File.ReadAllBytesAsync(package, CancellationToken.None);
            if (!Block)
            {
                return new InstallerRunResult(0, false, false);
            }

            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Stopped.TrySetResult(true);
            }

            return new InstallerRunResult(null, false, true);
        }
    }

    private sealed class FakeApplications : IInstalledApplicationSource
    {
        public List<DetectedApplication> Items { get; } = [];

        public IReadOnlyList<DetectedApplication> GetInstalledApplications() => [.. Items];
    }

    private sealed class FakeSignatureVerifier : IPackageSignatureVerifier
    {
        public string? Problem { get; set; }

        public List<IReadOnlyCollection<string>> Pins { get; } = [];

        public string? Verify(string packagePath, IReadOnlyCollection<string> trustedPublisherThumbprints)
        {
            Pins.Add(trustedPublisherThumbprints);
            return Problem;
        }
    }

    private sealed record ExecutedRequest(ExecutorRequest Request, byte[]? StagedContent);

    private sealed class FakeExecutor : ISoftwareExecutor
    {
        public List<ExecutedRequest> Requests { get; } = [];

        public ExecutorResponse Response { get; set; } = new() { Executed = true, ExitCode = 0 };

        public Action<ExecutorRequest> OnRun { get; set; } = _ => { };

        public Task<ExecutorResponse> ExecuteAsync(ExecutorRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(new ExecutedRequest(request, request.PackagePath is null ? null : File.ReadAllBytes(request.PackagePath)));
            if (Response.RefusalCode is null && Response.ExitCode is 0 or 3010 or 1641)
            {
                OnRun(request);
            }

            return Task.FromResult(Response);
        }
    }

    /// <summary>Serves the package with Range support and can fail mid-transfer (interrupted download).</summary>
    private sealed class FakePackageClient(byte[] content) : ISoftwarePackageClient
    {
        public byte[] Content { get; set; } = content;

        public List<long> Offsets { get; } = [];

        public int? FailAfterBytes { get; set; }

        public int FailTimes { get; set; } = 1;

        public HttpStatusCode? Refuse { get; set; }

        public async Task DownloadSoftwarePackageAsync(Guid executionId, long offset, Stream destination, long maxBytes, CancellationToken cancellationToken)
        {
            Offsets.Add(offset);
            if (Refuse is { } status)
            {
                throw new AgentApiException(AgentApiException.Classify(status), status, "package_unavailable", "refused");
            }

            var remaining = Content.AsMemory((int)offset);
            if (FailAfterBytes is { } limit && FailTimes > 0 && offset < limit)
            {
                FailTimes--;
                await destination.WriteAsync(remaining[..(limit - (int)offset)], cancellationToken);
                await destination.FlushAsync(cancellationToken);
                throw new IOException("Connection reset by peer.");
            }

            await destination.WriteAsync(remaining, cancellationToken);
        }
    }

    private sealed class InstantTime : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            System.CreateTimer(callback, state, TimeSpan.Zero, period);
    }
}
