namespace Solvix.Agent.Core.Software;

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts;
using Solvix.Agent.Contracts.RemoteManagement;
using Solvix.Agent.Contracts.Software;
using Solvix.Agent.Core.Configuration;
using Solvix.Agent.Core.RemoteManagement;
using Solvix.Agent.Core.State;
using Solvix.Agent.Core.Updates;

/// <summary>
/// Software install, uninstall and detection in the unprivileged agent service. The agent does
/// everything that needs no rights — detection, download, checksum and signature checks — and asks
/// the privileged executor only to run the installer. Installations are idempotent: an application
/// that is already present at the required version is reported as such without downloading or
/// running anything, which also makes a retry after an interrupted attempt safe.
/// </summary>
public sealed partial class SoftwareCommandHandler(
    string commandType,
    IInstalledApplicationSource applications,
    PackageDownloader downloader,
    IPackageSignatureVerifier verifier,
    ISoftwareExecutor executor,
    CommandKeyStore keys,
    IAgentStateStore state,
    IOptions<AgentOptions> options,
    ILogger<SoftwareCommandHandler> logger) : ICommandHandler, ISignedCommandHandler
{
    public static readonly string[] Types = [CommandTypes.SoftwareInstall, CommandTypes.SoftwareUninstall, CommandTypes.SoftwareDetect];

    /// <summary>Windows Installer: another installation is in progress.</summary>
    public const int MsiBusy = 1618;

    /// <summary>Windows Installer: the product is not installed (uninstall).</summary>
    public const int MsiUnknownProduct = 1605;

    public string CommandType { get; } = commandType;

    public TimeSpan MaxDuration(CommandEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var timeout = int.TryParse(envelope.Parameters.GetValueOrDefault("timeoutSeconds"), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? Math.Clamp(seconds, SoftwareOperation.MinTimeoutSeconds, SoftwareOperation.MaxTimeoutSeconds)
            : SoftwareOperation.MinTimeoutSeconds;

        // Download and detection on top of the installer's own timeout.
        return TimeSpan.FromSeconds(timeout) + TimeSpan.FromMinutes(30);
    }

    public Task<CommandHandlerResult> ExecuteAsync(CommandEnvelope envelope, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Software commands are executed with their signed bytes.");

    public async Task<CommandHandlerResult> ExecuteAsync(SignedCommandContext command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var envelope = command.Envelope;
        SoftwareOperation operation;
        try
        {
            operation = SoftwareOperation.Parse(envelope.CommandType, envelope.Parameters);
        }
        catch (FormatException ex)
        {
            return Failure(SoftwareErrorCodes.InvalidParameters, ex.Message, new SoftwareOperationResult { Outcome = SoftwareOutcome.Failed, FailedPhase = "verify" });
        }

        var before = DetectionEvaluator.Evaluate(operation.Detection!, applications.GetInstalledApplications());
        if (envelope.CommandType == CommandTypes.SoftwareDetect)
        {
            return Success(new SoftwareOperationResult
            {
                Outcome = before.Installed ? SoftwareOutcome.Detected : SoftwareOutcome.NotDetected,
                DetectionSatisfied = before.Satisfied,
                DetectedVersion = before.InstalledVersion,
            });
        }

        var install = envelope.CommandType == CommandTypes.SoftwareInstall;
        if (install && before.Satisfied)
        {
            return Success(new SoftwareOperationResult { Outcome = SoftwareOutcome.AlreadyInstalled, DetectionSatisfied = true, DetectedVersion = before.InstalledVersion });
        }

        if (!install && !before.Installed)
        {
            return Success(new SoftwareOperationResult { Outcome = SoftwareOutcome.NotInstalled });
        }

        if (PackageTrust.CheckType(operation, options.Value.Software) is { } typeProblem)
        {
            return Failure(typeProblem.Code, typeProblem.Message, new SoftwareOperationResult { Outcome = SoftwareOutcome.Failed, FailedPhase = "verify" });
        }

        string? staged = null;
        try
        {
            var signature = "NotApplicable";
            if (operation.RequiresPackage)
            {
                var download = await downloader.DownloadAsync(envelope.ExecutionId, operation, cancellationToken);
                if (!download.Succeeded)
                {
                    return Failure(download.ErrorCode!, download.ErrorMessage!, new SoftwareOperationResult { Outcome = SoftwareOutcome.Failed, FailedPhase = "download" });
                }

                staged = download.Path;
                if (PackageTrust.CheckSignature(staged!, operation, options.Value.Software, verifier) is { } signatureProblem)
                {
                    return Failure(signatureProblem.Code, signatureProblem.Message,
                        new SoftwareOperationResult { Outcome = SoftwareOutcome.Failed, FailedPhase = "verify", PackageVerified = true, Signature = signatureProblem.Message });
                }

                signature = operation.RequireSignature ? "Valid" : "Unsigned";
            }

            var current = state.Load();
            var response = await executor.ExecuteAsync(
                new ExecutorRequest
                {
                    Command = command.Signed,
                    PackagePath = staged,
                    Keys = keys.Export(),
                    TenantId = current.TenantId ?? Guid.Empty,
                    DeviceId = current.DeviceId ?? Guid.Empty,
                },
                cancellationToken);
            var verified = operation.RequiresPackage;
            if (response.RefusalCode is { } refused)
            {
                LogExecutorRefused(logger, envelope.ExecutionId, refused);
                return Failure(refused, response.RefusalMessage ?? "The software executor refused the command.",
                    new SoftwareOperationResult { Outcome = SoftwareOutcome.Failed, FailedPhase = "install", PackageVerified = verified, Signature = signature });
            }

            var log = Tail(response.Log);
            if (response.Cancelled)
            {
                return new CommandHandlerResult(CommandOutcome.Cancelled, Serialize(new SoftwareOperationResult { Outcome = SoftwareOutcome.Failed, FailedPhase = "install", LogExcerpt = log }),
                    null, SoftwareErrorCodes.Cancelled, "The installation was cancelled.");
            }

            if (response.TimedOut)
            {
                return Failure(SoftwareErrorCodes.InstallerTimeout, $"The installer did not finish within {operation.TimeoutSeconds} seconds and was stopped.",
                    new SoftwareOperationResult { Outcome = SoftwareOutcome.Failed, FailedPhase = "install", PackageVerified = verified, Signature = signature, LogExcerpt = log });
            }

            var exit = response.ExitCode ?? -1;
            if (!install && operation.InstallerType == InstallerType.Msi && exit == MsiUnknownProduct)
            {
                // Windows Installer does not know the product: whatever Add/Remove Programs still shows is a stale entry.
                return Success(new SoftwareOperationResult { Outcome = SoftwareOutcome.NotInstalled, InstallerExitCode = exit, LogExcerpt = log }, exit);
            }

            var reboot = operation.RebootExitCodes.Contains(exit);
            var success = operation.SuccessExitCodes.Contains(exit) || reboot;
            if (!success)
            {
                var code = operation.InstallerType == InstallerType.Msi && exit == MsiBusy ? SoftwareErrorCodes.AnotherInstallInProgress : SoftwareErrorCodes.InstallerFailed;
                return Failure(code, Describe(operation.InstallerType, exit),
                    new SoftwareOperationResult { Outcome = SoftwareOutcome.Failed, InstallerExitCode = exit, FailedPhase = "install", PackageVerified = verified, Signature = signature, LogExcerpt = log },
                    exit);
            }

            // Verify the outcome independently of the installer's exit code.
            var after = DetectionEvaluator.Evaluate(operation.Detection!, applications.GetInstalledApplications());
            if (install && !after.Satisfied)
            {
                return Failure(SoftwareErrorCodes.NotDetectedAfterInstall,
                    $"The installer reported success (exit {exit.ToString(CultureInfo.InvariantCulture)}) but the application was not detected{(after.Installed ? $" at the required version (found {after.InstalledVersion})" : string.Empty)}.",
                    new SoftwareOperationResult { Outcome = SoftwareOutcome.Failed, InstallerExitCode = exit, RebootRequired = reboot, DetectedVersion = after.InstalledVersion, FailedPhase = "detect", PackageVerified = verified, Signature = signature, LogExcerpt = log },
                    exit);
            }

            if (!install && after.Installed && !reboot)
            {
                return Failure(SoftwareErrorCodes.StillInstalled, "The uninstaller reported success but the application is still installed.",
                    new SoftwareOperationResult { Outcome = SoftwareOutcome.Failed, InstallerExitCode = exit, DetectedVersion = after.InstalledVersion, FailedPhase = "detect", PackageVerified = verified, Signature = signature, LogExcerpt = log },
                    exit);
            }

            return Success(
                new SoftwareOperationResult
                {
                    Outcome = install ? SoftwareOutcome.Installed : SoftwareOutcome.Uninstalled,
                    InstallerExitCode = exit,
                    RebootRequired = reboot,
                    DetectionSatisfied = after.Satisfied,
                    DetectedVersion = after.InstalledVersion,
                    PackageVerified = verified,
                    Signature = signature,
                    LogExcerpt = log,
                },
                exit);
        }
        finally
        {
            downloader.Discard(staged);
        }
    }

    /// <summary>Plain-language meaning of common Windows Installer exit codes.</summary>
    public static string Describe(InstallerType type, int exitCode)
    {
        var meaning = type != InstallerType.Msi ? null : exitCode switch
        {
            1602 => "the installation was cancelled",
            1603 => "a fatal error occurred during installation",
            1618 => "another installation is already in progress",
            1619 => "the package could not be opened (it may be corrupt)",
            1620 => "the package is not a valid Windows Installer package",
            1625 => "the installation is forbidden by system policy",
            1633 => "the package is not supported on this platform",
            1638 => "another version of this product is already installed",
            _ => null,
        };
        return $"The installer exited with code {exitCode.ToString(CultureInfo.InvariantCulture)}{(meaning is null ? string.Empty : $": {meaning}")}.";
    }

    private static string? Tail(string? log) =>
        log is null ? null : log.Length <= SoftwareOperationResult.MaxLogLength ? log : log[^SoftwareOperationResult.MaxLogLength..];

    private static string Serialize(SoftwareOperationResult result) => JsonSerializer.Serialize(result, AgentJsonContext.Default.SoftwareOperationResult);

    private static CommandHandlerResult Success(SoftwareOperationResult result, int? exitCode = null) =>
        new(CommandOutcome.Succeeded, Serialize(result), exitCode);

    private static CommandHandlerResult Failure(string code, string message, SoftwareOperationResult result, int? exitCode = null) =>
        new(CommandOutcome.Failed, Serialize(result), exitCode, code, message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The software executor refused command {ExecutionId}: {Code}")]
    private static partial void LogExecutorRefused(ILogger logger, Guid executionId, string code);
}
