namespace Solvix.Agent.Core.Runtime;

using System.Reflection;
using Microsoft.Extensions.Logging;
using Solvix.Agent.Core.Api;
using Solvix.Agent.Core.Configuration;
using Solvix.Agent.Core.Patching;
using Solvix.Endpoint.Contracts.Agent;
using Solvix.Endpoint.Contracts.Patching;

/// <summary>
/// One iteration of the agent: heartbeat, then the work the server returned
/// (Windows Update scan, patch jobs, restarts, configuration evaluation).
/// The Windows service host calls <see cref="RunOnceAsync"/> in a loop with the
/// returned delay; tests call it directly.
/// </summary>
public sealed partial class AgentRuntime(
    IAgentApi api,
    AgentStateStore state,
    IDeviceInfoProvider deviceInfo,
    IWindowsUpdateClient windowsUpdate,
    IRebootManager reboot,
    PatchJobRunner patchRunner,
    ConfigurationEvaluator configuration,
    AgentOptions options,
    TimeProvider time,
    ILogger<AgentRuntime> logger)
{
    public static string AgentVersion { get; } =
        typeof(AgentRuntime).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public async Task EnsureEnrolledAsync(CancellationToken cancellationToken)
    {
        if (state.IsEnrolled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(options.EnrollmentToken))
        {
            throw new InvalidOperationException("The agent is not enrolled and no enrolment token is configured (Solvix:Agent:EnrollmentToken).");
        }

        var response = await api.EnrollAsync(
            new EnrollDeviceRequest(
                options.EnrollmentToken,
                deviceInfo.Hostname,
                deviceInfo.HardwareId,
                deviceInfo.DnsDomain,
                deviceInfo.OsName,
                deviceInfo.OsVersion,
                deviceInfo.OsBuild,
                AgentVersion),
            cancellationToken).ConfigureAwait(false);
        state.SaveEnrollment(response.DeviceId, response.TenantId, response.DeviceCredential);
        LogEnrolled(logger, response.DeviceId);
    }

    /// <summary>Runs one cycle and returns how long to wait before the next one.</summary>
    public async Task<TimeSpan> RunOnceAsync(CancellationToken cancellationToken)
    {
        await EnsureEnrolledAsync(cancellationToken).ConfigureAwait(false);

        // Jobs run sequentially inside a cycle, so a journal found here belongs to an attempt that was
        // interrupted (crash, power loss, restart) or whose result could not be delivered yet.
        await patchRunner.RecoverAsync(cancellationToken).ConfigureAwait(false);

        var work = await api.HeartbeatAsync(
            new AgentHeartbeatRequest(
                deviceInfo.Hostname,
                deviceInfo.DnsDomain,
                deviceInfo.OsName,
                deviceInfo.OsVersion,
                deviceInfo.OsBuild,
                AgentVersion,
                reboot.LastBootUtc),
            cancellationToken).ConfigureAwait(false);

        var now = time.GetUtcNow();
        if (work.InventoryRequested || state.LastScanUtc is not { } lastScan || now - lastScan >= TimeSpan.FromHours(options.ScanIntervalHours))
        {
            await ScanAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var job in work.PatchJobs)
        {
            await patchRunner.RunAsync(job, cancellationToken).ConfigureAwait(false);
        }

        if (work.PatchJobs.Count > 0)
        {
            // Report the new state right away so compliance reflects the installation.
            await ScanAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var restart in work.Restarts)
        {
            await HandleRestartAsync(restart, cancellationToken).ConfigureAwait(false);
        }

        var dueForEvaluation = state.LastConfigurationEvaluationUtc is not { } lastEval
            || now - lastEval >= TimeSpan.FromMinutes(options.ConfigurationEvaluationMinutes);
        if (work.ConfigurationRevision != state.ConfigurationRevision || dueForEvaluation)
        {
            await EvaluateConfigurationAsync(force: dueForEvaluation, cancellationToken).ConfigureAwait(false);
        }

        return TimeSpan.FromSeconds(Math.Max(options.MinimumPollSeconds, work.PollIntervalSeconds));
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        var report = await windowsUpdate.ScanAsync(cancellationToken).ConfigureAwait(false);
        await api.UploadInventoryAsync(report, cancellationToken).ConfigureAwait(false);
        state.RecordScan(report.ScanCompletedUtc);
        LogScanned(logger, report.Updates.Count(u => !u.IsInstalled && !u.IsHidden), report.ScanResult);
    }

    private async Task HandleRestartAsync(RestartRequestDto restart, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (restart.WindowEndsUtc is { } end && now.AddSeconds(restart.GracePeriodSeconds) > end)
        {
            await api.AcknowledgeRestartAsync(restart.RestartId, new RestartAck(RestartAckStatus.Postponed, "Not enough time left in the maintenance window."), cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            reboot.ScheduleRestart(restart.GracePeriodSeconds, restart.UserMessage);
            await api.AcknowledgeRestartAsync(restart.RestartId, new RestartAck(RestartAckStatus.Initiated, null), cancellationToken).ConfigureAwait(false);
            LogRestart(logger, restart.RestartId, restart.GracePeriodSeconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not AgentConflictException)
        {
            await api.AcknowledgeRestartAsync(restart.RestartId, new RestartAck(RestartAckStatus.Failed, ex.Message), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EvaluateConfigurationAsync(bool force, CancellationToken cancellationToken)
    {
        // A forced re-evaluation ignores the cached revision so drift is detected even when nothing changed server-side.
        var document = await api.GetConfigurationAsync(force ? null : state.ConfigurationRevision, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return;
        }

        var report = configuration.Evaluate(document);
        await api.ReportConfigurationAsync(report, cancellationToken).ConfigureAwait(false);
        state.RecordConfiguration(document.Revision, time.GetUtcNow());
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Enrolled as device {DeviceId}")]
    private static partial void LogEnrolled(ILogger logger, Guid deviceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Windows Update scan finished: {Missing} missing update(s), result {Result}")]
    private static partial void LogScanned(ILogger logger, int missing, OperationResultCode result);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Restart {RestartId} initiated with a {Grace}s grace period")]
    private static partial void LogRestart(ILogger logger, Guid restartId, int grace);
}
