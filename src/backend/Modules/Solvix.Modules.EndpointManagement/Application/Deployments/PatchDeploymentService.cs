namespace Solvix.Modules.EndpointManagement.Application.Deployments;

using Microsoft.EntityFrameworkCore;
using Solvix.Endpoint.Contracts.Patching;
using Solvix.Modules.Devices.Application.Groups;
using Solvix.Modules.Devices.Domain;
using Solvix.Modules.EndpointManagement.Application.Targeting;
using Solvix.Modules.EndpointManagement.Domain.Configuration;
using Solvix.Modules.EndpointManagement.Domain.Deployments;
using Solvix.Modules.EndpointManagement.Domain.MaintenanceWindows;
using Solvix.Modules.EndpointManagement.Domain.Patches;
using Solvix.Modules.EndpointManagement.Domain.Targeting;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Auditing;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Tenancy;

public interface IPatchDeploymentService
{
    Task<PagedResult<DeploymentDto>> ListAsync(DeploymentListQuery query, CancellationToken cancellationToken);

    Task<DeploymentDto> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<DeploymentDto> CreateAsync(SaveDeploymentRequest request, CancellationToken cancellationToken);

    Task<DeploymentDto> UpdateAsync(Guid id, SaveDeploymentRequest request, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The explicit authorisation that allows the deployment to change devices.</summary>
    Task<DeploymentDto> ScheduleAsync(Guid id, CancellationToken cancellationToken);

    Task<DeploymentDto> UnscheduleAsync(Guid id, CancellationToken cancellationToken);

    Task<DeploymentDto> PromoteAsync(Guid id, DeploymentActionRequest request, CancellationToken cancellationToken);

    Task<DeploymentDto> PauseAsync(Guid id, DeploymentActionRequest request, CancellationToken cancellationToken);

    Task<DeploymentDto> ResumeAsync(Guid id, DeploymentActionRequest request, CancellationToken cancellationToken);

    Task<DeploymentDto> CancelAsync(Guid id, DeploymentActionRequest request, CancellationToken cancellationToken);

    Task<PagedResult<DeploymentJobDto>> ListJobsAsync(Guid id, PatchJobStatus? status, int page, int pageSize, CancellationToken cancellationToken);
}

internal sealed class PatchDeploymentService(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IClock clock,
    IAuditTrail audit,
    ITargetingService targeting) : IPatchDeploymentService, IDeviceGroupUsageCheck
{
    /// <summary>Classifications that change the OS substantially; deploying them needs an Owner.</summary>
    private static readonly UpdateClassification[] HighImpactClassifications =
        [UpdateClassification.Upgrades, UpdateClassification.FeaturePacks, UpdateClassification.ServicePacks, UpdateClassification.Drivers];

    public async Task<PagedResult<DeploymentDto>> ListAsync(DeploymentListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var deployments = db.Set<PatchDeployment>().AsNoTracking();
        if (query.Status is { } status)
        {
            deployments = deployments.Where(d => d.Status == status);
        }

        var total = await deployments.LongCountAsync(cancellationToken);
        var rows = await deployments.Include(d => d.Patches).Include(d => d.Targets)
            .OrderByDescending(d => d.CreatedAtUtc)
            .Skip(page.Skip).Take(page.SafePageSize)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
        var progress = await ProgressAsync(rows.Select(r => r.Id).ToList(), cancellationToken);
        return new PagedResult<DeploymentDto>(rows.Select(d => ToDto(d, progress.GetValueOrDefault(d.Id) ?? EmptyProgress)).ToList(), page.SafePage, page.SafePageSize, total);
    }

    public async Task<DeploymentDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var deployment = await db.Set<PatchDeployment>().AsNoTracking().Include(d => d.Patches).Include(d => d.Targets).AsSplitQuery()
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken)
            ?? throw new NotFoundException("PatchDeployment", id);
        var progress = await ProgressAsync([id], cancellationToken);
        return ToDto(deployment, progress.GetValueOrDefault(id) ?? EmptyProgress);
    }

    public async Task<DeploymentDto> CreateAsync(SaveDeploymentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var deployment = PatchDeployment.Create(tenantContext.RequireTenantId(), request.Name, currentUser.RequireUserId());
        await ApplyAsync(deployment, request, cancellationToken);
        db.Set<PatchDeployment>().Add(deployment);
        audit.Record("patch_deployment.created", nameof(PatchDeployment), deployment.Id, $"Deployment '{deployment.Name}' drafted ({request.PatchIds.Count} patches).", request);
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(deployment.Id, cancellationToken);
    }

    public async Task<DeploymentDto> UpdateAsync(Guid id, SaveDeploymentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var deployment = await FindAsync(id, cancellationToken);
        await ApplyAsync(deployment, request, cancellationToken);
        audit.Record("patch_deployment.updated", nameof(PatchDeployment), id, $"Deployment '{deployment.Name}' edited.", request);
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var deployment = await FindAsync(id, cancellationToken);
        if (deployment.Status != DeploymentStatus.Draft)
        {
            throw new ConflictException("Only draft deployments can be deleted; cancel the deployment instead.");
        }

        db.Set<PatchDeployment>().Remove(deployment);
        audit.Record("patch_deployment.deleted", nameof(PatchDeployment), id, $"Draft deployment '{deployment.Name}' deleted.");
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<DeploymentDto> ScheduleAsync(Guid id, CancellationToken cancellationToken)
    {
        var deployment = await FindAsync(id, cancellationToken);
        var patchIds = deployment.Patches.Select(p => p.PatchId).ToList();
        var patches = await db.Set<Patch>().AsNoTracking().Where(p => patchIds.Contains(p.Id)).ToListAsync(cancellationToken);
        var unapproved = patches.Where(p => p.ApprovalState != PatchApprovalState.Approved).Select(p => p.Title).ToList();
        if (unapproved.Count > 0)
        {
            throw new ConflictException($"Only approved patches can be deployed. Not approved: {string.Join("; ", unapproved)}.");
        }

        var ownerReasons = new List<string>();
        if (deployment.RestartBehavior == RestartBehavior.ForceAtDeadline)
        {
            ownerReasons.Add("forced restarts at the deadline");
        }

        if (patches.Any(p => HighImpactClassifications.Contains(p.Classification)))
        {
            ownerReasons.Add("upgrades, feature packs, service packs or drivers");
        }

        if (!deployment.HasPilot && deployment.Targets.Any(t => t.TargetType == TargetType.AllDevices && !t.IsExclusion))
        {
            ownerReasons.Add("all devices without a pilot ring");
        }

        if (ownerReasons.Count > 0 && currentUser.Role != TenantRole.Owner)
        {
            throw new ForbiddenException($"Scheduling this deployment requires a workspace Owner ({string.Join(", ", ownerReasons)}).");
        }

        // Fail early if nothing would be targeted.
        var snapshot = await targeting.LoadAsync(cancellationToken);
        var devices = snapshot.Resolve(deployment.Targets.Select(t => t.ToRef()));
        if (devices.Count == 0)
        {
            throw new ConflictException("The targets currently match no active devices.");
        }

        deployment.Authorize(currentUser.RequireUserId(), clock.UtcNow);
        audit.Record(
            "patch_deployment.scheduled",
            nameof(PatchDeployment),
            id,
            $"Deployment '{deployment.Name}' authorised for {devices.Count} device(s), starting {deployment.ScheduledStartUtc:u}.",
            new { devices = devices.Count, patches = patches.Select(p => new { p.Id, p.Title, p.KbArticleIds }), ownerReasons });
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<DeploymentDto> UnscheduleAsync(Guid id, CancellationToken cancellationToken)
    {
        var deployment = await FindAsync(id, cancellationToken);
        deployment.Unschedule();
        audit.Record("patch_deployment.unscheduled", nameof(PatchDeployment), id, $"Deployment '{deployment.Name}' returned to draft.");
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<DeploymentDto> PromoteAsync(Guid id, DeploymentActionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var deployment = await FindAsync(id, cancellationToken);
        if (request.Force)
        {
            if (currentUser.Role != TenantRole.Owner)
            {
                throw new ForbiddenException("Promoting past a failed pilot requires a workspace Owner.");
            }

            if (string.IsNullOrWhiteSpace(request.Reason))
            {
                throw new ValidationException("reason", "Explain why the failed pilot is being overridden.");
            }
        }

        var wasHalted = deployment.Status == DeploymentStatus.Halted;
        deployment.Promote(currentUser.RequireUserId(), clock.UtcNow, request.Force);
        audit.Record(
            wasHalted ? "patch_deployment.pilot_overridden" : "patch_deployment.promoted",
            nameof(PatchDeployment),
            id,
            wasHalted ? $"Failed pilot of '{deployment.Name}' overridden; rolling out to all targets." : $"'{deployment.Name}' promoted from pilot to all targets.",
            new { request.Reason, request.Force });
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<DeploymentDto> PauseAsync(Guid id, DeploymentActionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var deployment = await FindAsync(id, cancellationToken);
        deployment.Pause(request.Reason);
        audit.Record("patch_deployment.paused", nameof(PatchDeployment), id, $"'{deployment.Name}' paused.", new { request.Reason });
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<DeploymentDto> ResumeAsync(Guid id, DeploymentActionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var deployment = await FindAsync(id, cancellationToken);
        deployment.Resume();
        audit.Record("patch_deployment.resumed", nameof(PatchDeployment), id, $"'{deployment.Name}' resumed.", new { request.Reason });
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<DeploymentDto> CancelAsync(Guid id, DeploymentActionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var deployment = await FindAsync(id, cancellationToken);
        var now = clock.UtcNow;
        deployment.Cancel(now, request.Reason);

        // Pending work is cancelled; installations already running on devices finish and report normally.
        var pending = await db.Set<PatchDeploymentJob>().Where(j => j.DeploymentId == id && j.Status == PatchJobStatus.Pending).ToListAsync(cancellationToken);
        foreach (var job in pending)
        {
            job.Cancel(now);
        }

        var restarts = await db.Set<DeviceRestartRequest>()
            .Where(r => r.DeploymentId == id && (r.Status == RestartRequestStatus.Pending || r.Status == RestartRequestStatus.Dispatched))
            .ToListAsync(cancellationToken);
        foreach (var restart in restarts)
        {
            restart.Cancel(now);
        }

        audit.Record("patch_deployment.cancelled", nameof(PatchDeployment), id, $"'{deployment.Name}' cancelled; {pending.Count} pending device(s) and {restarts.Count} restart(s) cancelled.", new { request.Reason });
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<PagedResult<DeploymentJobDto>> ListJobsAsync(Guid id, PatchJobStatus? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        if (!await db.Set<PatchDeployment>().AnyAsync(d => d.Id == id, cancellationToken))
        {
            throw new NotFoundException("PatchDeployment", id);
        }

        var request = new PageRequest(page, pageSize);
        var jobs =
            from j in db.Set<PatchDeploymentJob>().AsNoTracking()
            join d in db.Set<Device>().AsNoTracking() on j.DeviceId equals d.Id
            where j.DeploymentId == id && (status == null || j.Status == status)
            select new { Job = j, d.Hostname };
        var total = await jobs.LongCountAsync(cancellationToken);
        var rows = await jobs.OrderBy(x => x.Job.Ring).ThenBy(x => x.Hostname).Skip(request.Skip).Take(request.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<DeploymentJobDto>(
            rows.Select(x => new DeploymentJobDto(
                x.Job.Id, x.Job.DeviceId, x.Hostname, x.Job.Ring, x.Job.Status, x.Job.Attempt, x.Job.FailureCount, x.Job.Stage, x.Job.PercentComplete,
                x.Job.Message, x.Job.LastError, x.Job.LastHResult is { } h ? $"0x{unchecked((uint)h):X8}" : null, x.Job.RebootRequired,
                x.Job.DispatchedAtUtc, x.Job.LastProgressAtUtc, x.Job.LeaseExpiresUtc, x.Job.NextAttemptAfterUtc, x.Job.CompletedAtUtc)).ToList(),
            request.SafePage,
            request.SafePageSize,
            total);
    }

    public async Task<string?> DescribeUsageAsync(Guid deviceGroupId, CancellationToken cancellationToken)
    {
        var deployment = await db.Set<PatchDeployment>()
            .Where(d => (d.PilotDeviceGroupId == deviceGroupId || d.Targets.Any(t => t.TargetType == TargetType.DeviceGroup && t.TargetId == deviceGroupId))
                && d.Status != DeploymentStatus.Completed && d.Status != DeploymentStatus.CompletedWithFailures && d.Status != DeploymentStatus.Cancelled)
            .Select(d => d.Name)
            .FirstOrDefaultAsync(cancellationToken);
        if (deployment is not null)
        {
            return $"targeted by deployment '{deployment}'.";
        }

        var finishedPilot = await db.Set<PatchDeployment>().AnyAsync(d => d.PilotDeviceGroupId == deviceGroupId, cancellationToken);
        if (finishedPilot)
        {
            return "it is the pilot ring of a past deployment (kept for history).";
        }

        return await db.Set<ConfigurationAssignment>().AnyAsync(a => a.TargetType == TargetType.DeviceGroup && a.TargetId == deviceGroupId, cancellationToken)
            ? "assigned configuration policies target it."
            : null;
    }

    private async Task ApplyAsync(PatchDeployment deployment, SaveDeploymentRequest request, CancellationToken cancellationToken)
    {
        var patchIds = request.PatchIds.Distinct().ToList();
        var found = await db.Set<Patch>().Where(p => patchIds.Contains(p.Id)).Select(p => new { p.Id, p.ApprovalState }).ToListAsync(cancellationToken);
        var unknown = patchIds.Except(found.Select(p => p.Id)).ToList();
        if (unknown.Count > 0)
        {
            throw new ValidationException("patchIds", $"Unknown patch id(s): {string.Join(", ", unknown)}.");
        }

        if (found.Any(p => p.ApprovalState == PatchApprovalState.Declined))
        {
            throw new ValidationException("patchIds", "Declined patches cannot be deployed.");
        }

        var targets = request.Targets.Select(t => t.ToRef()).ToList();
        await targeting.ValidateAsync(targets, "targets", cancellationToken);
        if (request.MaintenanceWindowId is { } windowId && !await db.Set<MaintenanceWindow>().AnyAsync(w => w.Id == windowId, cancellationToken))
        {
            throw new ValidationException("maintenanceWindowId", "Maintenance window not found.");
        }

        if (request.PilotDeviceGroupId is { } groupId && !await db.Set<DeviceGroup>().AnyAsync(g => g.Id == groupId, cancellationToken))
        {
            throw new ValidationException("pilotDeviceGroupId", "Device group not found.");
        }

        deployment.Configure(
            request.Name, request.Description, request.ScheduledStartUtc, request.DeadlineUtc, request.MaintenanceWindowId,
            request.RestartBehavior, request.RestartGracePeriodSeconds, request.PilotDeviceGroupId, request.PilotPercentage,
            request.PilotSuccessThresholdPercent, request.PilotSoakMinutes, request.AutoPromote, request.FailureThresholdPercent, request.MaxAttempts);
        deployment.SetPatches(patchIds);
        deployment.SetTargets(targets);
    }

    private async Task<PatchDeployment> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Set<PatchDeployment>().Include(d => d.Patches).Include(d => d.Targets).AsSplitQuery()
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken)
        ?? throw new NotFoundException("PatchDeployment", id);

    private static readonly DeploymentProgressDto EmptyProgress = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, null);

    private async Task<Dictionary<Guid, DeploymentProgressDto>> ProgressAsync(List<Guid> deploymentIds, CancellationToken cancellationToken)
    {
        var rows = await db.Set<PatchDeploymentJob>().AsNoTracking()
            .Where(j => deploymentIds.Contains(j.DeploymentId))
            .GroupBy(j => new { j.DeploymentId, j.Ring, j.Status })
            .Select(g => new { g.Key.DeploymentId, g.Key.Ring, g.Key.Status, Count = g.Count() })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.DeploymentId).ToDictionary(g => g.Key, g => Summarize(g.Select(r => (r.Ring, r.Status, r.Count)).ToList()));
    }

    internal static DeploymentProgressDto Summarize(IReadOnlyList<(DeploymentRing Ring, PatchJobStatus Status, int Count)> rows)
    {
        int Sum(Func<(DeploymentRing Ring, PatchJobStatus Status, int Count), bool> predicate) => rows.Where(predicate).Sum(r => r.Count);
        var succeeded = Sum(r => r.Status == PatchJobStatus.Succeeded);
        var pendingRestart = Sum(r => r.Status == PatchJobStatus.PendingRestart);
        var failed = Sum(r => r.Status == PatchJobStatus.Failed);
        var timedOut = Sum(r => r.Status == PatchJobStatus.TimedOut);
        var pilot = Sum(r => r.Ring == DeploymentRing.Pilot);
        var pilotSuccess = Sum(r => r.Ring == DeploymentRing.Pilot && r.Status is PatchJobStatus.Succeeded or PatchJobStatus.PendingRestart);
        var pilotFailed = Sum(r => r.Ring == DeploymentRing.Pilot && r.Status is PatchJobStatus.Failed or PatchJobStatus.TimedOut);
        var settled = succeeded + pendingRestart + failed + timedOut;
        return new DeploymentProgressDto(
            Sum(_ => true),
            Sum(r => r.Status == PatchJobStatus.Pending),
            Sum(r => r.Status is PatchJobStatus.Dispatched or PatchJobStatus.InProgress),
            pendingRestart,
            succeeded,
            failed,
            Sum(r => r.Status == PatchJobStatus.NotApplicable),
            timedOut,
            Sum(r => r.Status == PatchJobStatus.Cancelled),
            pilot,
            pilotSuccess + pilotFailed,
            pilotSuccess + pilotFailed == 0 ? null : Math.Round(pilotSuccess * 100.0 / (pilotSuccess + pilotFailed), 1),
            settled == 0 ? null : Math.Round((failed + timedOut) * 100.0 / settled, 1));
    }

    private static DeploymentDto ToDto(PatchDeployment d, DeploymentProgressDto progress) => new(
        d.Id, d.Name, d.Description, d.Status, d.Phase, d.ScheduledStartUtc, d.DeadlineUtc, d.MaintenanceWindowId, d.RestartBehavior,
        d.RestartGracePeriodSeconds, d.PilotDeviceGroupId, d.PilotPercentage, d.PilotSuccessThresholdPercent, d.PilotSoakMinutes, d.AutoPromote,
        d.FailureThresholdPercent, d.MaxAttempts, d.Patches.Select(p => p.PatchId).ToList(),
        d.Targets.Select(t => new TargetDto(t.TargetType, t.TargetId, t.IsExclusion)).ToList(),
        d.CreatedByUserId, d.AuthorizedByUserId, d.AuthorizedAtUtc, d.StartedAtUtc, d.PilotCompletedAtUtc, d.PromotedAtUtc, d.CompletedAtUtc,
        d.HaltReason, progress, d.CreatedAtUtc);
}
