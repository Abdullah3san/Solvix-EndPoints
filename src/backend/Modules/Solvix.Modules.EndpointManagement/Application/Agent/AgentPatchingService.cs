namespace Solvix.Modules.EndpointManagement.Application.Agent;

using System.Globalization;
using System.Text.Json;
using Solvix.Agent.Contracts.RemoteManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvix.Endpoint.Contracts;
using Solvix.Endpoint.Contracts.Agent;
using Solvix.Endpoint.Contracts.Configuration;
using Solvix.Endpoint.Contracts.Patching;
using Solvix.Modules.EndpointManagement.Application.Configuration;
using Solvix.Modules.EndpointManagement.Domain.Configuration;
using Solvix.Modules.EndpointManagement.Domain.Deployments;
using Solvix.Modules.EndpointManagement.Domain.MaintenanceWindows;
using Solvix.Modules.EndpointManagement.Domain.Patches;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Auditing;
using Solvix.SharedKernel.Endpoints;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public interface IAgentPatchingService
{
    /// <summary>
    /// Called by the agent on every poll: records restarts, dispatches due patch jobs, restarts and
    /// configuration enforcement as signed commands, and tells the agent what to do itself.
    /// </summary>
    Task<AgentWorkResponse> GetWorkAsync(Guid deviceId, AgentWorkRequest request, CancellationToken cancellationToken);

    /// <summary>Applies the outcome of a finished <c>patch.install</c>, <c>system.restart</c> or <c>configuration.apply</c> command.</summary>
    Task OnCommandFinishedAsync(RemoteCommandState state, CancellationToken cancellationToken);
}

/// <summary>
/// Agent-facing side of patching and configuration enforcement. The (unprivileged) agent only scans and
/// evaluates; anything that changes the device (installing updates, enforcing or reverting settings,
/// restarting) is sent as a signed command through the remote management broker, which re-authorizes the
/// user it acts for, and is executed by the privileged executor after verifying the signature. Every
/// lookup is scoped to the calling device, so an agent can never see or affect another device's work.
/// </summary>
internal sealed partial class AgentPatchingService(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    IClock clock,
    IAuditTrail audit,
    IConfigurationEngine configuration,
    IRemoteCommandDispatcher commands,
    IOptions<EndpointManagementOptions> options) : IAgentPatchingService
{
    /// <summary>A device that is offline this long when a command is sent gets it on its next poll after that or not at all.</summary>
    private static readonly TimeSpan CommandFetchWindow = TimeSpan.FromHours(1);

    private EndpointManagementOptions Options => options.Value;

    public async Task<AgentWorkResponse> GetWorkAsync(Guid deviceId, AgentWorkRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = clock.UtcNow;
        var summary = await db.Set<DeviceUpdateSummary>().FirstOrDefaultAsync(s => s.DeviceId == deviceId, cancellationToken);
        if (summary is null)
        {
            summary = new DeviceUpdateSummary(tenantContext.RequireTenantId(), deviceId);
            db.Set<DeviceUpdateSummary>().Add(summary);
        }

        var lastBoot = request.LastBootUtc > now.AddMinutes(5) ? now : request.LastBootUtc;
        if (summary.ObserveBoot(lastBoot))
        {
            await CompleteRestartsAsync(deviceId, now, cancellationToken);

            // Verify installations that were waiting for this restart.
            summary.RequestInventory();
        }

        await db.SaveChangesAsync(cancellationToken);
        await ReconcileAsync(deviceId, summary, cancellationToken);

        var document = await configuration.BuildDocumentAsync(deviceId, persist: false, cancellationToken);
        if (!await DispatchPatchJobAsync(deviceId, now, cancellationToken))
        {
            await DispatchRestartAsync(deviceId, now, cancellationToken);
        }

        await DispatchConfigurationApplyAsync(deviceId, summary, document, now, cancellationToken);
        return new AgentWorkResponse(document.Revision, summary.InventoryRequested, Options.AgentPollIntervalSeconds);
    }

    public async Task OnCommandFinishedAsync(RemoteCommandState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.IsFinal)
        {
            return;
        }

        switch (state.CommandType)
        {
            case CommandTypes.PatchInstall:
                await FinishPatchJobAsync(state, cancellationToken);
                break;
            case CommandTypes.Restart:
                await FinishRestartAsync(state, cancellationToken);
                break;
            case CommandTypes.ConfigurationApply:
                await FinishConfigurationApplyAsync(state, cancellationToken);
                break;
            default:
                break;
        }
    }

    /// <summary>Catches up on commands whose completion notification was missed (server restart, observer failure).</summary>
    private async Task ReconcileAsync(Guid deviceId, DeviceUpdateSummary summary, CancellationToken cancellationToken)
    {
        var ids = await db.Set<PatchDeploymentJob>()
            .Where(j => j.DeviceId == deviceId && (j.Status == PatchJobStatus.Dispatched || j.Status == PatchJobStatus.InProgress) && j.CommandExecutionId != null)
            .Select(j => j.CommandExecutionId!.Value)
            .ToListAsync(cancellationToken);
        ids.AddRange(await db.Set<DeviceRestartRequest>()
            .Where(r => r.DeviceId == deviceId && r.Status == RestartRequestStatus.Dispatched && r.CommandExecutionId != null)
            .Select(r => r.CommandExecutionId!.Value)
            .ToListAsync(cancellationToken));
        if (summary.ConfigurationApplyExecutionId is { } applyId)
        {
            ids.Add(applyId);
        }

        if (ids.Count == 0)
        {
            return;
        }

        foreach (var state in await commands.GetStatesAsync(ids, cancellationToken))
        {
            if (state.IsFinal && state.DeviceId == deviceId)
            {
                await OnCommandFinishedAsync(state, cancellationToken);
            }
        }
    }

    private async Task FinishPatchJobAsync(RemoteCommandState state, CancellationToken cancellationToken)
    {
        var job = await db.Set<PatchDeploymentJob>().FirstOrDefaultAsync(j => j.CommandExecutionId == state.ExecutionId && j.DeviceId == state.DeviceId, cancellationToken);
        if (job is null || !job.IsInFlight)
        {
            return;
        }

        var result = Parse<PatchJobResult>(state.Output);
        if (result is not null && result.Attempt == job.Attempt)
        {
            await ApplyJobResultAsync(job, result, cancellationToken);
            return;
        }

        var now = clock.UtcNow;
        var deployment = await db.Set<PatchDeployment>().AsNoTracking().FirstAsync(d => d.Id == job.DeploymentId, cancellationToken);
        switch (state.Phase)
        {
            case RemoteCommandPhase.Expired or RemoteCommandPhase.Cancelled:
                // Never reached the device: try again without counting a failure.
                job.Requeue(state.Phase == RemoteCommandPhase.Expired ? "The device did not fetch the installation command in time." : "The installation command was cancelled.", now.AddMinutes(1));
                break;
            default:
                job.FailAttempt(
                    state.ErrorMessage ?? state.ErrorCode ?? $"The installation command ended as {state.Phase}.",
                    null,
                    null,
                    deployment.MaxAttempts,
                    now,
                    Options.RetryDelay);
                break;
        }

        audit.Record(
            "patch_deployment.device_command_" + state.Phase.ToString().ToLowerInvariant(),
            nameof(PatchDeployment),
            deployment.Id,
            $"Device {state.DeviceId}, attempt {job.Attempt}: command {state.Phase}{(state.ErrorCode is null ? string.Empty : " (" + state.ErrorCode + ")")} → job {job.Status}.",
            new { jobId = job.Id, state.ExecutionId, state.Phase, state.ErrorCode, state.ErrorMessage });
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ApplyJobResultAsync(PatchDeploymentJob job, PatchJobResult result, CancellationToken cancellationToken)
    {
        var deviceId = job.DeviceId;
        var jobId = job.Id;
        var deployment = await db.Set<PatchDeployment>().Include(d => d.Patches).FirstAsync(d => d.Id == job.DeploymentId, cancellationToken);
        var now = clock.UtcNow;
        var resultsJson = JsonSerializer.Serialize(result.Updates, ContractJson.Options);
        await ApplyUpdateResultsAsync(deviceId, deployment, result, now, cancellationToken);

        switch (result.Outcome)
        {
            case PatchJobOutcome.Succeeded:
                job.Succeed(result.RebootRequired, resultsJson, now);
                if (result.RebootRequired && deployment.RestartBehavior != RestartBehavior.Never)
                {
                    await RequestRestartAsync(deviceId, deployment, now, cancellationToken);
                }

                break;
            case PatchJobOutcome.NotApplicable:
                job.MarkNotApplicable("The device reports the updates as installed or not applicable.", now);
                break;
            case PatchJobOutcome.Aborted:
                job.Requeue($"Aborted by the agent: {result.Error ?? "stopped before finishing"}.", now.AddMinutes(1));
                break;
            default:
                job.FailAttempt(
                    result.Error ?? (result.Outcome == PatchJobOutcome.PartiallySucceeded ? "Some updates failed to install." : "Installation failed."),
                    result.HResult ?? result.Updates.FirstOrDefault(u => u.ResultCode is OperationResultCode.Failed or OperationResultCode.Aborted)?.HResult,
                    resultsJson,
                    deployment.MaxAttempts,
                    now,
                    Options.RetryDelay);
                break;
        }

        audit.Record(
            $"patch_deployment.device_{result.Outcome.ToString().ToLowerInvariant()}",
            nameof(PatchDeployment),
            deployment.Id,
            $"Device {deviceId}, attempt {result.Attempt}: {result.Outcome}{(result.RebootRequired ? " (restart required)" : string.Empty)} → job {job.Status}.",
            new { jobId, result.Attempt, result.Outcome, result.RebootRequired, result.Error, result.HResult, updates = result.Updates });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Sends the next due patch job as a signed command; at most one job runs on a device at a time.</summary>
    private async Task<bool> DispatchPatchJobAsync(Guid deviceId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var candidates = await (
                from j in db.Set<PatchDeploymentJob>()
                join d in db.Set<PatchDeployment>().Include(x => x.Patches) on j.DeploymentId equals d.Id
                where j.DeviceId == deviceId
                    && (j.Status == PatchJobStatus.Pending || j.Status == PatchJobStatus.Dispatched || j.Status == PatchJobStatus.InProgress)
                select new { Job = j, Deployment = d })
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0)
        {
            return false;
        }

        if (candidates.Any(c => c.Job.IsInFlight))
        {
            return true;
        }

        var windowIds = candidates.Select(c => c.Deployment.MaintenanceWindowId).OfType<Guid>().Distinct().ToList();
        var windows = await db.Set<MaintenanceWindow>().AsNoTracking().Where(w => windowIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, cancellationToken);
        foreach (var c in candidates.OrderBy(c => c.Deployment.ScheduledStartUtc))
        {
            var (job, deployment) = (c.Job, c.Deployment);
            WindowOccurrence? occurrence = null;
            if (deployment.MaintenanceWindowId is { } windowId)
            {
                occurrence = windows.TryGetValue(windowId, out var window) ? window.OccurrenceAt(now) : null;
            }

            var ringOpen = deployment.Phase == DeploymentPhase.Broad || job.Ring == DeploymentRing.Pilot;
            var windowOpen = deployment.MaintenanceWindowId is null || occurrence is not null;
            var beforeDeadline = deployment.DeadlineUtc is not { } deadline || now < deadline;
            if (!(deployment.IsDispatching && ringOpen && windowOpen && beforeDeadline && now >= deployment.ScheduledStartUtc && job.IsEligible(now)))
            {
                continue;
            }

            var patchIds = deployment.Patches.Select(p => p.PatchId).ToList();
            var patches = await db.Set<Patch>().AsNoTracking().Where(p => patchIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
            DateTimeOffset? installEnds = occurrence?.EndUtc;
            if (deployment.DeadlineUtc is { } end && (installEnds is null || end < installEnds))
            {
                installEnds = end;
            }

            var timeout = installEnds is { } until
                ? TimeSpan.FromMinutes(Math.Clamp((until - now).TotalMinutes + 15, 30, 240))
                : TimeSpan.FromHours(4);
            var executionId = Guid.NewGuid();
            job.Dispatch(now, CommandFetchWindow + timeout, executionId);

            // Record the id first: a crash between the two writes can never send the same work twice.
            await db.SaveChangesAsync(cancellationToken);

            var dto = new PatchJobDto(
                job.Id,
                deployment.Id,
                deployment.Name,
                job.Attempt,
                deployment.Patches.Select(p => patches.GetValueOrDefault(p.PatchId)).OfType<Patch>()
                    .Select(p => new PatchJobUpdate(p.UpdateId, p.RevisionNumber, p.Title)).ToList(),
                job.LeaseExpiresUtc!.Value,
                installEnds,
                deployment.RestartBehavior,
                deployment.DeadlineUtc);
            var state = await commands.DispatchAsync(
                new DelegatedCommandRequest(
                    executionId,
                    deviceId,
                    CommandTypes.PatchInstall,
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["job"] = JsonSerializer.Serialize(dto, ContractJson.Options) },
                    deployment.CreatedByUserId,
                    null,
                    null,
                    $"Patch deployment '{deployment.Name}'",
                    now + CommandFetchWindow,
                    timeout),
                cancellationToken);
            if (state.IsFinal)
            {
                await OnCommandFinishedAsync(state, cancellationToken);
            }

            return true;
        }

        return false;
    }

    private async Task DispatchRestartAsync(Guid deviceId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var restarts = await db.Set<DeviceRestartRequest>()
            .Where(r => r.DeviceId == deviceId && (r.Status == RestartRequestStatus.Pending || r.Status == RestartRequestStatus.Dispatched || r.Status == RestartRequestStatus.Initiated))
            .OrderBy(r => r.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        // One restart satisfies every request for the device; wait while one is on its way.
        if (restarts.Count == 0 || restarts.Any(r => r.Status != RestartRequestStatus.Pending))
        {
            return;
        }

        var windowIds = restarts.Select(r => r.MaintenanceWindowId).OfType<Guid>().Distinct().ToList();
        var windows = await db.Set<MaintenanceWindow>().AsNoTracking().Where(w => windowIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, cancellationToken);
        foreach (var restart in restarts.Where(r => r.NotBeforeUtc <= now))
        {
            var occurrence = restart.MaintenanceWindowId is { } wid && windows.TryGetValue(wid, out var w) ? w.OccurrenceAt(now) : null;
            var inWindow = restart.MaintenanceWindowId is null || occurrence is not null;
            var forced = restart.Force && restart.DeadlineUtc is { } deadline && now >= deadline;
            if (!inWindow && !forced)
            {
                continue;
            }

            var requester = restart.RequestedByUserId;
            if (requester is null && restart.DeploymentId is { } deploymentId)
            {
                requester = await db.Set<PatchDeployment>().AsNoTracking().Where(d => d.Id == deploymentId).Select(d => (Guid?)d.CreatedByUserId).FirstOrDefaultAsync(cancellationToken);
            }

            if (requester is not { } requestedBy)
            {
                continue;
            }

            var delay = Math.Clamp(restart.GracePeriodSeconds, 0, 600);
            var minutes = Math.Max(1, delay / 60).ToString(CultureInfo.InvariantCulture);
            var message = forced
                ? $"Your computer restarts in {minutes} minute(s) to finish required updates. Save your work."
                : $"Your computer restarts in {minutes} minute(s) to finish installing updates. Save your work.";
            var executionId = Guid.NewGuid();
            restart.Dispatch(now, executionId);
            await db.SaveChangesAsync(cancellationToken);
            var state = await commands.DispatchAsync(
                new DelegatedCommandRequest(
                    executionId,
                    deviceId,
                    CommandTypes.Restart,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["delaySeconds"] = delay.ToString(CultureInfo.InvariantCulture),
                        ["message"] = message,
                    },
                    requestedBy,
                    null,
                    null,
                    restart.Reason,
                    now + CommandFetchWindow,
                    TimeSpan.FromMinutes(15)),
                cancellationToken);
            if (state.IsFinal)
            {
                await OnCommandFinishedAsync(state, cancellationToken);
            }

            return;
        }
    }

    private async Task FinishRestartAsync(RemoteCommandState state, CancellationToken cancellationToken)
    {
        var restart = await db.Set<DeviceRestartRequest>().FirstOrDefaultAsync(r => r.CommandExecutionId == state.ExecutionId && r.DeviceId == state.DeviceId, cancellationToken);
        if (restart is null || restart.Status != RestartRequestStatus.Dispatched)
        {
            return;
        }

        var (ack, message) = state.Phase switch
        {
            RemoteCommandPhase.Succeeded => (RestartAckStatus.Initiated, (string?)null),

            // The device was offline or the command was withdrawn: ask again on a later poll.
            RemoteCommandPhase.Expired or RemoteCommandPhase.Cancelled => (RestartAckStatus.Postponed, $"Restart command {state.Phase}."),
            _ => (RestartAckStatus.Failed, state.ErrorMessage ?? state.ErrorCode ?? $"Restart command {state.Phase}."),
        };
        restart.Acknowledge(ack, message, clock.UtcNow);
        audit.Record($"restart.{ack.ToString().ToLowerInvariant()}", "Device", state.DeviceId, $"Restart {ack}: {restart.Reason}", new { restartId = restart.Id, state.ExecutionId, message });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Sends the device's configuration document for enforcement when an enforced setting drifted (or a
    /// setting must be reverted) and the document changed or the last enforcement is old enough.
    /// </summary>
    private async Task DispatchConfigurationApplyAsync(Guid deviceId, DeviceUpdateSummary summary, ConfigurationDocument document, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (summary.ConfigurationApplyExecutionId is { } previous
            && (await commands.GetStatesAsync([previous], cancellationToken)).FirstOrDefault() is { IsFinal: false })
        {
            return;
        }

        var enforceable = document.Settings.Where(s => s.ApplyAllowed && s.Mode == EnforcementMode.Enforce).Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        if (enforceable.Count == 0 && document.RevertKeys.Count == 0)
        {
            return;
        }

        var drifted = enforceable.Count > 0 && await db.Set<DeviceConfigurationState>().AnyAsync(
            s => s.DeviceId == deviceId && enforceable.Contains(s.SettingKey)
                && (s.Status == SettingComplianceStatus.Drifted || s.Status == SettingComplianceStatus.Failed || s.Status == null),
            cancellationToken);
        if (!drifted && document.RevertKeys.Count == 0)
        {
            return;
        }

        var changed = !string.Equals(summary.ConfigurationApplyRevision, document.Revision, StringComparison.Ordinal);
        if (!changed && summary.ConfigurationApplyAtUtc is { } last && now - last < TimeSpan.FromMinutes(Math.Max(0, Options.ConfigurationReapplyMinutes)))
        {
            return;
        }

        // The command acts for the user who published the newest policy version being enforced.
        var versionIds = document.Settings.Where(s => enforceable.Contains(s.Key)).Select(s => s.PolicyVersionId).Distinct().ToList();
        var requester = await db.Set<ConfigurationPolicyVersion>().AsNoTracking()
            .Where(v => versionIds.Contains(v.Id) && v.PublishedByUserId != null)
            .OrderByDescending(v => v.PublishedAtUtc)
            .Select(v => v.PublishedByUserId)
            .FirstOrDefaultAsync(cancellationToken);
        if (requester is null)
        {
            var revertKeys = document.RevertKeys.ToList();
            requester = await db.Set<DeviceConfigurationState>().AsNoTracking()
                .Where(s => s.DeviceId == deviceId && revertKeys.Contains(s.SettingKey) && s.PolicyVersionId != null)
                .Join(db.Set<ConfigurationPolicyVersion>().AsNoTracking(), s => s.PolicyVersionId, v => (Guid?)v.Id, (s, v) => v.PublishedByUserId)
                .FirstOrDefaultAsync(id => id != null, cancellationToken);
        }

        if (requester is not { } requestedBy)
        {
            return;
        }

        // Persist the desired state the agent will report against.
        await configuration.BuildDocumentAsync(deviceId, persist: true, cancellationToken);
        var executionId = Guid.NewGuid();
        summary.RecordConfigurationApply(executionId, document.Revision, now);
        await db.SaveChangesAsync(cancellationToken);
        var state = await commands.DispatchAsync(
            new DelegatedCommandRequest(
                executionId,
                deviceId,
                CommandTypes.ConfigurationApply,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["document"] = JsonSerializer.Serialize(document, ContractJson.Options) },
                requestedBy,
                null,
                null,
                $"Enforce configuration revision {document.Revision}",
                now + CommandFetchWindow,
                TimeSpan.FromMinutes(15)),
            cancellationToken);
        if (state.IsFinal)
        {
            await OnCommandFinishedAsync(state, cancellationToken);
        }
    }

    private async Task FinishConfigurationApplyAsync(RemoteCommandState state, CancellationToken cancellationToken)
    {
        var summary = await db.Set<DeviceUpdateSummary>().FirstOrDefaultAsync(s => s.DeviceId == state.DeviceId && s.ConfigurationApplyExecutionId == state.ExecutionId, cancellationToken);
        if (summary is null)
        {
            return;
        }

        if (Parse<ConfigurationReport>(state.Output) is { } report)
        {
            await configuration.IngestReportAsync(state.DeviceId, report, cancellationToken);
        }
        else if (state.Phase != RemoteCommandPhase.Succeeded)
        {
            audit.Record("configuration.apply_" + state.Phase.ToString().ToLowerInvariant(), "Device", state.DeviceId,
                $"Configuration enforcement {state.Phase}{(state.ErrorMessage is null ? string.Empty : ": " + state.ErrorMessage)}",
                new { state.ExecutionId, state.ErrorCode });
        }

        summary.ClearConfigurationApply();
        await db.SaveChangesAsync(cancellationToken);
    }

    private static T? Parse<T>(string? output)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(output, ContractJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task CompleteRestartsAsync(Guid deviceId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var open = await db.Set<DeviceRestartRequest>()
            .Where(r => r.DeviceId == deviceId && (r.Status == RestartRequestStatus.Pending || r.Status == RestartRequestStatus.Dispatched || r.Status == RestartRequestStatus.Initiated))
            .ToListAsync(cancellationToken);
        foreach (var restart in open)
        {
            restart.Complete(now);
            audit.Record("restart.completed", "Device", deviceId, $"Device restarted ({restart.Reason}).", new { restartId = restart.Id });
        }
    }

    private async Task RequestRestartAsync(Guid deviceId, PatchDeployment deployment, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await db.Set<DeviceRestartRequest>().AnyAsync(
                r => r.DeviceId == deviceId && (r.Status == RestartRequestStatus.Pending || r.Status == RestartRequestStatus.Dispatched || r.Status == RestartRequestStatus.Initiated),
                cancellationToken))
        {
            return;
        }

        db.Set<DeviceRestartRequest>().Add(new DeviceRestartRequest(
            tenantContext.RequireTenantId(),
            deviceId,
            RestartSource.Deployment,
            deployment.Id,
            $"Finish installing updates from '{deployment.Name}'.",
            now,
            deployment.DeadlineUtc,
            deployment.MaintenanceWindowId,
            force: deployment.RestartBehavior == RestartBehavior.ForceAtDeadline,
            deployment.RestartGracePeriodSeconds,
            requestedByUserId: null));
    }

    private async Task ApplyUpdateResultsAsync(Guid deviceId, PatchDeployment deployment, PatchJobResult result, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var allowed = deployment.Patches.Select(p => p.PatchId).ToList();
        var patches = await db.Set<Patch>().AsNoTracking().Where(p => allowed.Contains(p.Id)).ToListAsync(cancellationToken);
        var states = await db.Set<DevicePatchState>().Where(s => s.DeviceId == deviceId && allowed.Contains(s.PatchId)).ToDictionaryAsync(s => s.PatchId, cancellationToken);
        foreach (var update in result.Updates)
        {
            var patch = patches.FirstOrDefault(p => string.Equals(p.UpdateId, update.UpdateId, StringComparison.OrdinalIgnoreCase));
            if (patch is null)
            {
                continue;
            }

            if (!states.TryGetValue(patch.Id, out var state))
            {
                state = new DevicePatchState(tenantContext.RequireTenantId(), deviceId, patch.Id, now);
                db.Set<DevicePatchState>().Add(state);
            }

            switch (update.ResultCode)
            {
                case OperationResultCode.Succeeded or OperationResultCode.SucceededWithErrors:
                    state.MarkInstalled(update.RebootRequired, now);
                    break;
                case OperationResultCode.Failed or OperationResultCode.Aborted:
                    state.RecordFailure(update.HResult, now);
                    break;
                default:
                    break;
            }
        }
    }
}
