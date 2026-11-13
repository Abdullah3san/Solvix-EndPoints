namespace Solvix.Modules.EndpointManagement.Application.Agent;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvix.Endpoint.Contracts;
using Solvix.Endpoint.Contracts.Agent;
using Solvix.Endpoint.Contracts.Patching;
using Solvix.Modules.Devices.Application.Enrollment;
using Solvix.Modules.EndpointManagement.Application.Configuration;
using Solvix.Modules.EndpointManagement.Domain.Deployments;
using Solvix.Modules.EndpointManagement.Domain.MaintenanceWindows;
using Solvix.Modules.EndpointManagement.Domain.Patches;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Auditing;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public interface IAgentPatchingService
{
    Task ReportProgressAsync(Guid deviceId, Guid jobId, PatchJobProgress progress, CancellationToken cancellationToken);

    Task CompleteJobAsync(Guid deviceId, Guid jobId, PatchJobResult result, CancellationToken cancellationToken);

    Task AcknowledgeRestartAsync(Guid deviceId, Guid restartId, RestartAck ack, CancellationToken cancellationToken);
}

/// <summary>
/// Agent-facing side of patching: hands out work on heartbeat (honouring rings,
/// start time, deadline and maintenance window), records progress and results,
/// and turns "installed, restart needed" into restart requests. Every lookup is
/// scoped to the calling device, so an agent can never see or report on another
/// device's jobs.
/// </summary>
internal sealed class AgentPatchingService(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    IClock clock,
    IAuditTrail audit,
    IConfigurationEngine configuration,
    IOptions<EndpointManagementOptions> options) : IAgentPatchingService, IAgentWorkProvider
{
    private EndpointManagementOptions Options => options.Value;

    public async Task<AgentWorkResponse> GetWorkAsync(DeviceHeartbeat heartbeat, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);
        var now = clock.UtcNow;
        var deviceId = heartbeat.DeviceId;
        var summary = await db.Set<DeviceUpdateSummary>().FirstOrDefaultAsync(s => s.DeviceId == deviceId, cancellationToken);
        if (summary is null)
        {
            summary = new DeviceUpdateSummary(tenantContext.RequireTenantId(), deviceId);
            db.Set<DeviceUpdateSummary>().Add(summary);
        }

        if (heartbeat.Rebooted)
        {
            await CompleteRestartsAsync(deviceId, now, cancellationToken);

            // Verify installations that were waiting for this restart.
            summary.RequestInventory();
        }

        var patchJobs = await DispatchPatchJobsAsync(deviceId, now, cancellationToken);
        var restarts = await DispatchRestartsAsync(deviceId, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        var document = await configuration.BuildDocumentAsync(deviceId, persist: false, cancellationToken);
        return new AgentWorkResponse(patchJobs, restarts, document.Revision, summary.InventoryRequested, Options.AgentPollIntervalSeconds);
    }

    public async Task ReportProgressAsync(Guid deviceId, Guid jobId, PatchJobProgress progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var job = await FindJobAsync(deviceId, jobId, cancellationToken);
        if (job.Attempt != progress.Attempt || !job.IsInFlight)
        {
            throw new ConflictException($"Attempt {progress.Attempt} of this job is no longer active (current attempt {job.Attempt}, status {job.Status}). Stop working on it.");
        }

        job.ReportProgress(progress.Stage, progress.PercentComplete, progress.Message, clock.UtcNow, Options.JobLease);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task CompleteJobAsync(Guid deviceId, Guid jobId, PatchJobResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        var job = await FindJobAsync(deviceId, jobId, cancellationToken);
        var lateButValid = job.Status == PatchJobStatus.Pending && job.Attempt == result.Attempt;
        if (job.Attempt != result.Attempt || !(job.IsInFlight || lateButValid))
        {
            throw new ConflictException($"Attempt {result.Attempt} of this job is no longer active (current attempt {job.Attempt}, status {job.Status}).");
        }

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

    public async Task AcknowledgeRestartAsync(Guid deviceId, Guid restartId, RestartAck ack, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ack);
        var restart = await db.Set<DeviceRestartRequest>().FirstOrDefaultAsync(r => r.Id == restartId && r.DeviceId == deviceId, cancellationToken)
            ?? throw new NotFoundException("RestartRequest", restartId);
        if (!restart.IsOpen)
        {
            throw new ConflictException($"The restart request is already {restart.Status}.");
        }

        restart.Acknowledge(ack.Status, ack.Message, clock.UtcNow);
        audit.Record($"restart.{ack.Status.ToString().ToLowerInvariant()}", "Device", deviceId, $"Restart {ack.Status}: {restart.Reason}", new { restartId, ack.Message });
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<PatchJobDto>> DispatchPatchJobsAsync(Guid deviceId, DateTimeOffset now, CancellationToken cancellationToken)
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
            return [];
        }

        var windowIds = candidates.Select(c => c.Deployment.MaintenanceWindowId).OfType<Guid>().Distinct().ToList();
        var windows = await db.Set<MaintenanceWindow>().AsNoTracking().Where(w => windowIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, cancellationToken);
        var patchIds = candidates.SelectMany(c => c.Deployment.Patches.Select(p => p.PatchId)).Distinct().ToList();
        var patches = await db.Set<Patch>().AsNoTracking().Where(p => patchIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);

        var result = new List<PatchJobDto>();
        foreach (var c in candidates.OrderBy(c => c.Deployment.ScheduledStartUtc))
        {
            var (job, deployment) = (c.Job, c.Deployment);
            WindowOccurrence? occurrence = null;
            if (deployment.MaintenanceWindowId is { } windowId)
            {
                occurrence = windows.TryGetValue(windowId, out var window) ? window.OccurrenceAt(now) : null;
            }

            if (job.IsInFlight)
            {
                // Resume after an agent restart: same attempt, still within its lease.
                if (job.LeaseExpiresUtc > now)
                {
                    result.Add(ToDto(job, deployment, patches, occurrence));
                }

                continue;
            }

            var ringOpen = deployment.Phase == DeploymentPhase.Broad || job.Ring == DeploymentRing.Pilot;
            var windowOpen = deployment.MaintenanceWindowId is null || occurrence is not null;
            var beforeDeadline = deployment.DeadlineUtc is not { } deadline || now < deadline;
            if (deployment.IsDispatching && ringOpen && windowOpen && beforeDeadline && now >= deployment.ScheduledStartUtc && job.IsEligible(now))
            {
                job.Dispatch(now, Options.JobLease);
                result.Add(ToDto(job, deployment, patches, occurrence));
            }
        }

        return result;
    }

    private async Task<IReadOnlyList<RestartRequestDto>> DispatchRestartsAsync(Guid deviceId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var restarts = await db.Set<DeviceRestartRequest>()
            .Where(r => r.DeviceId == deviceId && (r.Status == RestartRequestStatus.Pending || r.Status == RestartRequestStatus.Dispatched) && r.NotBeforeUtc <= now)
            .OrderBy(r => r.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        if (restarts.Count == 0)
        {
            return [];
        }

        var windowIds = restarts.Select(r => r.MaintenanceWindowId).OfType<Guid>().Distinct().ToList();
        var windows = await db.Set<MaintenanceWindow>().AsNoTracking().Where(w => windowIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, cancellationToken);

        // One restart satisfies every request for the device; send the earliest eligible one.
        foreach (var restart in restarts)
        {
            var occurrence = restart.MaintenanceWindowId is { } wid && windows.TryGetValue(wid, out var w) ? w.OccurrenceAt(now) : null;
            var inWindow = restart.MaintenanceWindowId is null || occurrence is not null;
            var forced = restart.Force && restart.DeadlineUtc is { } deadline && now >= deadline;
            if (!inWindow && !forced)
            {
                continue;
            }

            if (restart.Status == RestartRequestStatus.Pending)
            {
                restart.Dispatch(now);
            }

            return
            [
                new RestartRequestDto(
                    restart.Id,
                    restart.Reason,
                    restart.NotBeforeUtc,
                    forced ? null : occurrence?.EndUtc,
                    restart.GracePeriodSeconds,
                    forced
                        ? $"Your computer will restart in {restart.GracePeriodSeconds / 60} minute(s) to finish required updates (deadline reached). Save your work."
                        : $"Your computer will restart in {restart.GracePeriodSeconds / 60} minute(s) to finish installing updates. Save your work."),
            ];
        }

        return [];
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

    private async Task<PatchDeploymentJob> FindJobAsync(Guid deviceId, Guid jobId, CancellationToken cancellationToken) =>
        await db.Set<PatchDeploymentJob>().FirstOrDefaultAsync(j => j.Id == jobId && j.DeviceId == deviceId, cancellationToken)
        ?? throw new NotFoundException("PatchJob", jobId);

    private static PatchJobDto ToDto(PatchDeploymentJob job, PatchDeployment deployment, Dictionary<Guid, Patch> patches, WindowOccurrence? occurrence)
    {
        DateTimeOffset? installEnds = occurrence?.EndUtc;
        if (deployment.DeadlineUtc is { } deadline && (installEnds is null || deadline < installEnds))
        {
            installEnds = deadline;
        }

        return new PatchJobDto(
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
    }
}
