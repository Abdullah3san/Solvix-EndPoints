namespace Solvix.Agent.Core.Software;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts.RemoteManagement;
using Solvix.Agent.Contracts.Software;
using Solvix.Agent.Core.Configuration;
using Solvix.Agent.Core.RemoteManagement;
using Solvix.Agent.Core.State;
using Solvix.Agent.Core.Updates;

/// <summary>A fully determined process to start: executable, individual arguments, working directory.</summary>
public sealed record InstallerCommand(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, string? LogPath);

public sealed record InstallerRunResult(int? ExitCode, bool TimedOut, bool Cancelled);

/// <summary>Starts an installer process and waits for it, stopping the whole process tree on timeout or cancellation.</summary>
public interface IInstallerProcessRunner
{
    Task<InstallerRunResult> RunAsync(InstallerCommand command, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class ProcessInstallerRunner : IInstallerProcessRunner
{
    public async Task<InstallerRunResult> RunAsync(InstallerCommand command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // No shell: the executable is a full path and every argument is passed as its own argument.
        var start = new ProcessStartInfo(command.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = command.WorkingDirectory,
        };
        foreach (var argument in command.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("The installer could not be started.");
        using var timer = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, cancellationToken);
        try
        {
            await process.WaitForExitAsync(linked.Token);
            return new InstallerRunResult(process.ExitCode, false, false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Exited in the meantime.
            }

            await process.WaitForExitAsync(CancellationToken.None);
            return new InstallerRunResult(null, timer.IsCancellationRequested && !cancellationToken.IsCancellationRequested, cancellationToken.IsCancellationRequested);
        }
    }
}

/// <summary>The fixed command lines. Nothing from the server chooses the executable or a switch, only MSI properties / EXE arguments that passed validation.</summary>
public static class InstallerCommandLine
{
    public static InstallerCommand Build(string commandType, SoftwareOperation operation, string? packagePath, string workDirectory, string systemDirectory)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var log = Path.Combine(workDirectory, "installer.log");
        if (operation.InstallerType == InstallerType.Msi)
        {
            var msiexec = Path.Combine(systemDirectory, "msiexec.exe");
            List<string> args = commandType == CommandTypes.SoftwareInstall
                ? ["/i", packagePath ?? throw new ArgumentException("An MSI installation needs the package.", nameof(packagePath))]
                : ["/x", DetectionRule.NormalizeProductCode(operation.Detection?.ProductCode) ?? throw new ArgumentException("An MSI removal needs the product code.", nameof(operation))];
            args.AddRange(["/qn", "/norestart", "/l*v", log, "REBOOT=ReallySuppress"]);
            args.AddRange(operation.Arguments);
            return new InstallerCommand(msiexec, args, workDirectory, log);
        }

        return new InstallerCommand(packagePath ?? throw new ArgumentException("An EXE operation needs the package.", nameof(packagePath)), [.. operation.Arguments], workDirectory, null);
    }
}

/// <summary>
/// Identity the executor binds commands to, pinned on first use in the executor's own directory
/// (SYSTEM and Administrators only) and never replaced afterwards, so a compromised agent cannot
/// re-point the executor at another tenant or device.
/// </summary>
public sealed class PinnedIdentityStore(string directory) : IAgentStateStore
{
    private readonly object _gate = new();

    private string FilePath => Path.Combine(directory, "identity.json");

    public AgentState Load()
    {
        lock (_gate)
        {
            if (!File.Exists(FilePath))
            {
                return new AgentState();
            }

            var pinned = JsonSerializer.Deserialize(File.ReadAllText(FilePath), SoftwareJsonContext.Default.PinnedIdentity);
            return pinned is null ? new AgentState() : new AgentState { TenantId = pinned.TenantId, DeviceId = pinned.DeviceId, KeyName = "pinned" };
        }
    }

    public void Save(AgentState state) => throw new NotSupportedException("The executor identity is pinned once and never changed.");

    public bool PinIfEmpty(Guid tenantId, Guid deviceId)
    {
        lock (_gate)
        {
            if (File.Exists(FilePath) || tenantId == Guid.Empty || deviceId == Guid.Empty)
            {
                return false;
            }

            Directory.CreateDirectory(directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new PinnedIdentity { TenantId = tenantId, DeviceId = deviceId }, SoftwareJsonContext.Default.PinnedIdentity));
            return true;
        }
    }
}

/// <summary>
/// The privileged half of software deployment (runs as LocalSystem in the Solvix Software Executor
/// service, without network access). It trusts nothing it is given: it verifies the signed command
/// with its own pinned keys and identity and its own replay store, accepts only software install and
/// uninstall commands, copies the package into its own directory, verifies checksum and signature of
/// that copy (so the unprivileged agent cannot swap the file after its own checks), and runs a
/// command line it builds itself. One installation runs at a time.
/// </summary>
public sealed partial class SoftwareExecutor
{
    private readonly SemaphoreSlim _single = new(1, 1);
    private readonly string _directory;
    private readonly CommandKeyStore _keys;
    private readonly PinnedIdentityStore _identity;
    private readonly CommandReplayStore _replays;
    private readonly CommandVerifier _verifier;
    private readonly IPackageSignatureVerifier _signatures;
    private readonly IInstallerProcessRunner _runner;
    private readonly IOptions<AgentOptions> _agentOptions;
    private readonly ILogger<SoftwareExecutor> _logger;

    public SoftwareExecutor(
        IOptions<AgentOptions> agentOptions,
        IPackageSignatureVerifier signatures,
        IInstallerProcessRunner runner,
        TimeProvider time,
        ILogger<SoftwareExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(agentOptions);
        _agentOptions = agentOptions;
        _directory = agentOptions.Value.Software.ResolveExecutorDirectory();

        // Keys, identity and replay records live in the executor's own directory, not the agent's.
        var own = Options.Create(new AgentOptions
        {
            DataDirectory = _directory,
            RemoteManagement = agentOptions.Value.RemoteManagement,
            Software = agentOptions.Value.Software,
        });
        _keys = new CommandKeyStore(own);
        _identity = new PinnedIdentityStore(_directory);
        _replays = new CommandReplayStore(own, time);
        var registry = new CommandHandlerRegistry([new Marker(CommandTypes.SoftwareInstall), new Marker(CommandTypes.SoftwareUninstall)]);
        _verifier = new CommandVerifier(_keys, _replays, _identity, registry, own, time);
        _signatures = signatures;
        _runner = runner;
        _logger = logger;
    }

    public async Task<ExecutorResponse> ExecuteAsync(ExecutorRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _identity.PinIfEmpty(request.TenantId, request.DeviceId);
        _keys.PinIfEmpty(request.Keys);

        var verification = _verifier.Verify(request.Command);
        if (!verification.Accepted)
        {
            return ExecutorResponse.Refuse(verification.RejectionCode!, verification.Detail!);
        }

        var envelope = verification.Envelope!;
        if (envelope.CommandType is not (CommandTypes.SoftwareInstall or CommandTypes.SoftwareUninstall))
        {
            return ExecutorResponse.Refuse(CommandRejectionCodes.NotAllowed, "The executor only installs and removes software.");
        }

        SoftwareOperation operation;
        try
        {
            operation = SoftwareOperation.Parse(envelope.CommandType, envelope.Parameters);
        }
        catch (FormatException ex)
        {
            return ExecutorResponse.Refuse(SoftwareErrorCodes.InvalidParameters, ex.Message);
        }

        if (PackageTrust.CheckType(operation, _agentOptions.Value.Software) is { } typeProblem)
        {
            return ExecutorResponse.Refuse(typeProblem.Code, typeProblem.Message);
        }

        await _single.WaitAsync(cancellationToken);
        var work = Path.Combine(_directory, "work", envelope.ExecutionId.ToString("N"));
        try
        {
            if (!_replays.TryRecord(envelope))
            {
                return ExecutorResponse.Refuse(CommandRejectionCodes.Replayed, "This command was already executed.");
            }

            Directory.CreateDirectory(work);
            string? package = null;
            if (operation.RequiresPackage)
            {
                package = Path.Combine(work, operation.InstallerType == InstallerType.Msi ? "package.msi" : "setup.exe");
                if (await CopyVerifiedAsync(request.PackagePath, package, operation, cancellationToken) is { } copyProblem)
                {
                    return copyProblem;
                }

                if (PackageTrust.CheckSignature(package, operation, _agentOptions.Value.Software, _signatures) is { } signatureProblem)
                {
                    return ExecutorResponse.Refuse(signatureProblem.Code, signatureProblem.Message);
                }
            }

            var command = InstallerCommandLine.Build(envelope.CommandType, operation, package, work, Environment.SystemDirectory);
            LogStarting(_logger, envelope.ExecutionId, envelope.CommandType, operation.PackageName);
            var result = await _runner.RunAsync(command, TimeSpan.FromSeconds(operation.TimeoutSeconds), cancellationToken);
            LogFinished(_logger, envelope.ExecutionId, result.ExitCode, result.TimedOut, result.Cancelled);
            return new ExecutorResponse
            {
                Executed = true,
                ExitCode = result.ExitCode,
                TimedOut = result.TimedOut,
                Cancelled = result.Cancelled,
                Log = ReadLogTail(command.LogPath),
            };
        }
        finally
        {
            _single.Release();
            TryDelete(work);
        }
    }

    private static async Task<ExecutorResponse?> CopyVerifiedAsync(string? source, string destination, SoftwareOperation operation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(source) || !Path.IsPathFullyQualified(source) || !File.Exists(source))
        {
            return ExecutorResponse.Refuse(SoftwareErrorCodes.PackageUnavailable, "The staged package was not found.");
        }

        if (new FileInfo(source).Length != operation.SizeBytes)
        {
            return ExecutorResponse.Refuse(SoftwareErrorCodes.HashMismatch, "The staged package has the wrong size.");
        }

        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > operation.SizeBytes)
                {
                    return ExecutorResponse.Refuse(SoftwareErrorCodes.HashMismatch, "The staged package changed while it was copied.");
                }

                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), operation.Sha256, StringComparison.Ordinal))
            {
                return ExecutorResponse.Refuse(SoftwareErrorCodes.HashMismatch, "The staged package does not match the signed checksum.");
            }
        }

        return null;
    }

    /// <summary>Last part of the installer log (MSI verbose logs are UTF-16).</summary>
    internal static string? ReadLogTail(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        var bytes = File.ReadAllBytes(path);
        var text = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE ? Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2) : Encoding.UTF8.GetString(bytes);
        return text.Length <= SoftwareOperationResult.MaxLogLength ? text : text[^SoftwareOperationResult.MaxLogLength..];
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A process of the installer may still hold a file; removed with the next cleanup.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Executing {CommandType} for '{Package}' ({ExecutionId})")]
    private static partial void LogStarting(ILogger logger, Guid executionId, string commandType, string package);

    [LoggerMessage(Level = LogLevel.Information, Message = "Execution {ExecutionId} finished: exit {ExitCode}, timed out {TimedOut}, cancelled {Cancelled}")]
    private static partial void LogFinished(ILogger logger, Guid executionId, int? exitCode, bool timedOut, bool cancelled);

    /// <summary>Makes the verifier accept exactly the software types; never executed.</summary>
    private sealed class Marker(string type) : ICommandHandler
    {
        public string CommandType => type;

        public Task<CommandHandlerResult> ExecuteAsync(CommandEnvelope envelope, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
