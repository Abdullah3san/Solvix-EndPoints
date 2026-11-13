namespace Solvix.Modules.EndpointManagement.Application.Deployments;

using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvix.Modules.EndpointManagement.Application.Targeting;
using Solvix.Modules.EndpointManagement.Domain.Deployments;
using Solvix.Modules.EndpointManagement.Domain.Patches;
using Solvix.Modules.EndpointManagement.Domain.Targeting;
using Solvix.SharedKernel.Auditing;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public interface IDeploymentOrchestrator
{
    /// <summary>Advances every deployment and restart request of the current tenant to <paramref name="now"/>.</summary>
    Task TickAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>
/// The deployment state machine, run periodically per tenant by the background
/// worker (and directly by tests with an explicit clock). Each tick:
/// <list type="number">
/// <item>starts scheduled deployments whose start time passed (creating per-device jobs and the pilot ring);</item>
/// <item>treats jobs whose lease expired as interrupted (retry with back-off, or fail);</item>
/// <item>times out undispatched jobs after the deadline;</item>
/// <item>evaluates the pilot gate and the broad-phase failure threshold;</item>
/// <item>completes deployments whose devices have all settled;</item>
/// <item>re-queues or fails restarts that were never confirmed.</item>
/// </list>
/// </summary>
internal sealed class DeploymentOrchestrator(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    ITargetingService targeting,
    IAuditTrail audit,
    IOptions<EndpointManagementOptions> options) : IDeploymentOrchestrator
{
    private static readonly DeploymentStatus[] LiveStatuses =
        [DeploymentStatus.InProgress, DeploymentStatus.AwaitingPromotion, DeploymentStatus.Halted, DeploymentStatus.Paused];

    private EndpointManagementOptions Options => options.Value;

    public async Task TickAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await StartDueDeploymentsAsync(now, cancellationToken);

        var live = await db.Set<PatchDeployment>().Where(d => LiveStatuses.Contains(d.Status)).ToListAsync(cancellationToken);
        foreach (var deployment in live)
        {
            var jobs = await db.Set<PatchDeploymentJob>().Where(j => j.DeploymentId == deployment.Id).ToListAsync(cancellationToken);
            ExpireLeases(deployment, jobs, now);
            ApplyDeadline(deployment, jobs, now);
            EvaluatePhase(deployment, jobs, now);
            TryComplete(deployment, jobs, now);
        }

        await SweepRestartsAsync(now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task StartDueDeploymentsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var due = await db.Set<PatchDeployment>().Include(d => d.Patches).Include(d => d.Targets).AsSplitQuery()
            .Where(d => d.Status == DeploymentStatus.Scheduled && d.ScheduledStartUtc <= now)
            .ToListAsync(cancellationToken);
        if (due.Count == 0)
        {
            return;
        }

        var snapshot = await targeting.LoadAsync(cancellationToken);
        var tenantId = tenantContext.RequireTenantId();
        foreach (var deployment in due)
        {
            var devices = snapshot.Resolve(deployment.Targets.Select(t => t.ToRef()));
            var pilotIds = SelectPilot(deployment, devices, snapshot);
            var patchIds = deployment.Patches.Select(p => p.PatchId).ToList();
            var deviceIds = devices.Select(d => d.Id).ToList();
            var known = (await db.Set<DevicePatchState>().AsNoTracking()
                    .Where(s => deviceIds.Contains(s.DeviceId) && patchIds.Contains(s.PatchId))
                    .Select(s => new { s.DeviceId, s.PatchId, s.Status })
                    .ToListAsync(cancellationToken))
                .ToLookup(s => s.DeviceId);

            foreach (var device in devices)
            {
                var job = new PatchDeploymentJob(tenantId, deployment.Id, device.Id, pilotIds.Contains(device.Id) ? DeploymentRing.Pilot : DeploymentRing.Broad);
                var states = known[device.Id].ToDictionary(s => s.PatchId, s => s.Status);
                if (patchIds.All(p => states.TryGetValue(p, out var s) && s is DevicePatchStatus.Installed or DevicePatchStatus.PendingRestart or DevicePatchStatus.NotApplicable))
                {
                    job.MarkNotApplicable("Already installed or not applicable at start.", now);
                }

                db.Set<PatchDeploymentJob>().Add(job);
            }

            var withPilot = deployment.HasPilot;
            deployment.Start(now, withPilot);
            audit.Record(
                "patch_deployment.started",
                nameof(PatchDeployment),
                deployment.Id,
                withPilot
                    ? $"'{deployment.Name}' started: pilot ring of {pilotIds.Count} device(s), {devices.Count} device(s) in total."
                    : $"'{deployment.Name}' started on {devices.Count} device(s) without a pilot ring.",
                new { devices = devices.Count, pilot = pilotIds.Count });

            if (withPilot && pilotIds.Count == 0)
            {
                deployment.Halt("The pilot ring contains none of the targeted devices.");
                audit.Record("patch_deployment.halted", nameof(PatchDeployment), deployment.Id, $"'{deployment.Name}' halted: the pilot ring is empty.");
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Pilot group members among the targets, or a deterministic sample (stable across ticks and restarts).</summary>
    internal static HashSet<Guid> SelectPilot(PatchDeployment deployment, IReadOnlyList<DeviceTargetInfo> devices, TargetingSnapshot snapshot)
    {
        if (deployment.PilotDeviceGroupId is { } groupId)
        {
            return devices.Where(d => snapshot.Matches(d, new TargetRef(TargetType.DeviceGroup, groupId, false))).Select(d => d.Id).ToHashSet();
        }

        if (deployment.PilotPercentage is { } pct && pct > 0 && devices.Count > 0)
        {
            var count = Math.Max(1, (int)Math.Ceiling(devices.Count * pct / 100.0));
            return devices
                .OrderBy(d => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{deployment.Id:N}:{d.Id:N}"))), StringComparer.Ordinal)
                .Take(count)
                .Select(d => d.Id)
                .ToHashSet();
        }

        return [];
    }

    private void ExpireLeases(PatchDeployment deployment, List<PatchDeploymentJob> jobs, DateTimeOffset now)
    {
        foreach (var job in jobs.Where(j => j.IsInFlight && j.LeaseExpiresUtc < now))
        {
            job.FailAttempt(
                $"Interrupted: no result or progress from the device before the lease expired (attempt {job.Attempt}).",
                null,
                null,
                deployment.MaxAttempts,
                now,
                Options.RetryDelay);
            audit.Record(
                "patch_deployment.device_interrupted",
                nameof(PatchDeployment),
                deployment.Id,
                $"Device {job.DeviceId}: attempt {job.Attempt} interrupted; {(job.Status == PatchJobStatus.Failed ? "no attempts left" : "retry scheduled")}.",
                new { jobId = job.Id, job.DeviceId, job.Attempt, job.FailureCount });
        }
    }

    private void ApplyDeadline(PatchDeployment deployment, List<PatchDeploymentJob> jobs, DateTimeOffset now)
    {
        if (deployment.DeadlineUtc is not { } deadline || now < deadline)
        {
            return;
        }

        var timedOut = 0;
        foreach (var job in jobs.Where(j => j.Status == PatchJobStatus.Pending))
        {
            job.TimeOut(now);
            timedOut++;
        }

        if (timedOut > 0)
        {
            audit.Record("patch_deployment.deadline_passed", nameof(PatchDeployment), deployment.Id, $"Deadline of '{deployment.Name}' passed; {timedOut} device(s) timed out.");
        }
    }

    private void EvaluatePhase(PatchDeployment deployment, List<PatchDeploymentJob> jobs, DateTimeOffset now)
    {
        if (deployment.Phase == DeploymentPhase.Pilot && deployment.Status == DeploymentStatus.InProgress)
        {
            var pilot = jobs.Where(j => j.Ring == DeploymentRing.Pilot).ToList();
            if (pilot.Count == 0 || !pilot.All(j => j.IsSettled))
            {
                return;
            }

            var evaluable = pilot.Count(j => j.CountsAsSuccess || j.CountsAsFailure);
            var successRate = evaluable == 0 ? 100.0 : pilot.Count(j => j.CountsAsSuccess) * 100.0 / evaluable;
            if (successRate < deployment.PilotSuccessThresholdPercent)
            {
                deployment.Halt($"Pilot success rate {successRate:0.#}% is below the required {deployment.PilotSuccessThresholdPercent}%.");
                audit.Record("patch_deployment.halted", nameof(PatchDeployment), deployment.Id, $"'{deployment.Name}' halted after the pilot: {deployment.HaltReason}", new { successRate });
                return;
            }

            var firstPass = deployment.PilotCompletedAtUtc is null;
            deployment.PilotPassed(now);
            if (firstPass)
            {
                audit.Record("patch_deployment.pilot_passed", nameof(PatchDeployment), deployment.Id, $"Pilot of '{deployment.Name}' passed ({successRate:0.#}%).", new { successRate });
            }

            if (deployment.AutoPromote && deployment.SoakElapsed(now))
            {
                deployment.Promote(null, now, force: false);
                audit.Record("patch_deployment.promoted", nameof(PatchDeployment), deployment.Id, $"'{deployment.Name}' promoted automatically after a {deployment.PilotSoakMinutes}-minute soak.");
            }

            return;
        }

        if (deployment.Phase == DeploymentPhase.Broad && deployment.Status == DeploymentStatus.InProgress)
        {
            var settled = jobs.Count(j => j.CountsAsSuccess || j.CountsAsFailure);
            var failures = jobs.Count(j => j.CountsAsFailure);
            var allSettled = jobs.All(j => j.IsSettled);
            if (failures == 0 || (settled < Options.MinimumSampleForHalt && !allSettled))
            {
                return;
            }

            var failureRate = failures * 100.0 / settled;
            if (failureRate >= deployment.FailureThresholdPercent && !allSettled)
            {
                deployment.Halt($"Failure rate {failureRate:0.#}% reached the {deployment.FailureThresholdPercent}% threshold ({failures} of {settled} finished devices).");
                audit.Record("patch_deployment.halted", nameof(PatchDeployment), deployment.Id, $"'{deployment.Name}' halted: {deployment.HaltReason}", new { failureRate, failures, settled });
            }
        }
    }

    private void TryComplete(PatchDeployment deployment, List<PatchDeploymentJob> jobs, DateTimeOffset now)
    {
        if (deployment.Status != DeploymentStatus.InProgress || deployment.Phase != DeploymentPhase.Broad || !jobs.All(j => j.IsSettled))
        {
            return;
        }

        var waitingForRestart = jobs.Any(j => j.Status == PatchJobStatus.PendingRestart)
            && deployment.RestartBehavior != Solvix.Endpoint.Contracts.Patching.RestartBehavior.Never
            && (deployment.DeadlineUtc is null || now < deployment.DeadlineUtc);
        if (waitingForRestart)
        {
            return;
        }

        var withFailures = jobs.Any(j => j.CountsAsFailure);
        deployment.Complete(withFailures, now);
        audit.Record(
            "patch_deployment.completed",
            nameof(PatchDeployment),
            deployment.Id,
            $"'{deployment.Name}' {(withFailures ? "completed with failures" : "completed")}: {jobs.Count(j => j.CountsAsSuccess)} succeeded, {jobs.Count(j => j.CountsAsFailure)} failed, {jobs.Count(j => j.Status == PatchJobStatus.NotApplicable)} not applicable.");
    }

    private async Task SweepRestartsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var ackDeadline = now.AddMinutes(-Options.RestartAckTimeoutMinutes);
        var completionDeadline = now.AddMinutes(-Options.RestartCompletionTimeoutMinutes);
        var stale = await db.Set<DeviceRestartRequest>()
            .Where(r => (r.Status == RestartRequestStatus.Dispatched && r.DispatchedAtUtc < ackDeadline)
                || (r.Status == RestartRequestStatus.Initiated && r.InitiatedAtUtc < completionDeadline))
            .ToListAsync(cancellationToken);
        foreach (var restart in stale)
        {
            if (restart.Status == RestartRequestStatus.Dispatched)
            {
                restart.Acknowledge(Solvix.Endpoint.Contracts.Patching.RestartAckStatus.Postponed, "No acknowledgement from the agent; re-queued.", now);
            }
            else
            {
                restart.Acknowledge(Solvix.Endpoint.Contracts.Patching.RestartAckStatus.Failed, "The device did not come back with a new boot time.", now);
                audit.Record("restart.failed", "Device", restart.DeviceId, "Restart was initiated but the device never reported a new boot.", new { restartId = restart.Id });
            }
        }
    }
}
