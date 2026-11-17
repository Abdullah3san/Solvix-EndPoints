namespace Solvix.Modules.EndpointManagement.Application.Patches;

using Microsoft.EntityFrameworkCore;
using Solvix.Endpoint.Contracts.Agent;
using Solvix.Endpoint.Contracts.Patching;
using Solvix.Modules.EndpointManagement.Domain.Deployments;
using Solvix.Modules.EndpointManagement.Domain.Patches;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Auditing;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public interface IPatchInventoryIngestion
{
    Task IngestAsync(Guid deviceId, PatchInventoryReport report, CancellationToken cancellationToken);
}

/// <summary>
/// Turns a Windows Update Agent scan into catalogue entries, per-device patch
/// states, update health and failure history, then reconciles deployment jobs
/// against the fresh truth from the device (confirms installs after a restart,
/// detects installs that did not survive it, skips already-installed patches).
/// </summary>
internal sealed class PatchInventoryIngestion(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    IClock clock,
    IAuditTrail audit) : IPatchInventoryIngestion
{
    public const int MaxUpdatesPerReport = 5000;

    public async Task IngestAsync(Guid deviceId, PatchInventoryReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        var tenantId = tenantContext.RequireTenantId();
        var now = clock.UtcNow;

        var summary = await db.Set<DeviceUpdateSummary>().FirstOrDefaultAsync(s => s.DeviceId == deviceId, cancellationToken);
        if (summary is null)
        {
            summary = new DeviceUpdateSummary(tenantId, deviceId);
            db.Set<DeviceUpdateSummary>().Add(summary);
        }

        summary.RecordScan(
            report.ScanStartedUtc,
            report.ScanCompletedUtc,
            report.ScanResult,
            report.ScanHResult,
            report.ScanError,
            report.Service.WindowsUpdateServiceState,
            report.Service.AutomaticUpdatesNotificationLevel,
            report.Service.UpdateSource,
            report.Service.LastSuccessfulInstallUtc);
        summary.RecordReboot(report.Reboot.RebootRequired, report.Reboot.Reasons, report.Reboot.LastBootUtc, now);

        // A failed scan says nothing reliable about patch states; keep the previous picture.
        if (report.ScanResult is not (OperationResultCode.Succeeded or OperationResultCode.SucceededWithErrors))
        {
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var updates = report.Updates
            .Where(u => !string.IsNullOrWhiteSpace(u.UpdateId))
            .Take(MaxUpdatesPerReport)
            .GroupBy(u => (Id: u.UpdateId.Trim().ToLowerInvariant(), u.RevisionNumber))
            .Select(g => g.First())
            .ToList();
        var ids = updates.Select(u => u.UpdateId.Trim().ToLowerInvariant()).Distinct().ToList();
        var patches = (await db.Set<Patch>().Where(p => ids.Contains(p.UpdateId)).ToListAsync(cancellationToken))
            .ToDictionary(p => (p.UpdateId, p.RevisionNumber));

        var rules = await db.Set<PatchAutoApprovalRule>().Where(r => r.IsEnabled).OrderBy(r => r.Name).ToListAsync(cancellationToken);
        foreach (var update in updates)
        {
            var key = (update.UpdateId.Trim().ToLowerInvariant(), update.RevisionNumber);
            if (patches.TryGetValue(key, out var patch))
            {
                patch.Refresh(update, now);
                continue;
            }

            patch = Patch.Discover(tenantId, update, now);
            db.Set<Patch>().Add(patch);
            patches[key] = patch;
            if (rules.FirstOrDefault(r => r.Matches(patch)) is { } rule)
            {
                patch.AutoApprove(rule.Id, rule.Name, now);
                audit.Record("patch.auto_approved", nameof(Patch), patch.Id, $"'{patch.Title}' auto-approved by rule '{rule.Name}'.", new { patch.UpdateId, patch.Classification, patch.Severity });
            }
        }

        var history = report.History
            .Where(h => !string.IsNullOrWhiteSpace(h.UpdateId) && h.Operation == UpdateOperation.Installation)
            .GroupBy(h => h.UpdateId.Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.OrderBy(h => h.DateUtc).ToList());

        var states = await db.Set<DevicePatchState>().Where(s => s.DeviceId == deviceId).ToDictionaryAsync(s => s.PatchId, cancellationToken);
        var seen = new HashSet<Guid>();
        foreach (var update in updates)
        {
            var patch = patches[(update.UpdateId.Trim().ToLowerInvariant(), update.RevisionNumber)];
            seen.Add(patch.Id);
            if (!states.TryGetValue(patch.Id, out var state))
            {
                state = new DevicePatchState(tenantId, deviceId, patch.Id, now);
                db.Set<DevicePatchState>().Add(state);
                states[patch.Id] = state;
            }

            var entries = history.GetValueOrDefault(patch.UpdateId) ?? [];
            var lastSuccess = entries.LastOrDefault(h => h.ResultCode is OperationResultCode.Succeeded or OperationResultCode.SucceededWithErrors);
            var status = update switch
            {
                { IsHidden: true } => DevicePatchStatus.Hidden,
                { IsRebootRequired: true } => DevicePatchStatus.PendingRestart,
                { IsInstalled: true } => DevicePatchStatus.Installed,
                _ => DevicePatchStatus.Missing,
            };
            state.Observe(status, update.IsDownloaded, now, lastSuccess?.DateUtc);

            if (!update.IsInstalled)
            {
                foreach (var failure in entries.Where(h => h.ResultCode is OperationResultCode.Failed or OperationResultCode.Aborted
                             && (lastSuccess is null || h.DateUtc > lastSuccess.DateUtc)))
                {
                    state.RecordFailure(failure.HResult, failure.DateUtc);
                }
            }
        }

        // Outstanding patches the device no longer reports were installed or superseded.
        foreach (var state in states.Values.Where(s => !seen.Contains(s.PatchId)
                     && s.Status is DevicePatchStatus.Missing or DevicePatchStatus.Failed or DevicePatchStatus.PendingRestart))
        {
            if (state.Status == DevicePatchStatus.PendingRestart && !report.Reboot.RebootRequired)
            {
                state.MarkInstalled(pendingRestart: false, now);
            }
            else if (state.Status != DevicePatchStatus.PendingRestart)
            {
                state.Observe(DevicePatchStatus.NotApplicable, false, now);
            }
        }

        await ReconcileJobsAsync(deviceId, states, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ReconcileJobsAsync(Guid deviceId, Dictionary<Guid, DevicePatchState> states, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var jobs = await db.Set<PatchDeploymentJob>()
            .Where(j => j.DeviceId == deviceId && (j.Status == PatchJobStatus.PendingRestart || j.Status == PatchJobStatus.Pending))
            .ToListAsync(cancellationToken);
        if (jobs.Count == 0)
        {
            return;
        }

        var deploymentIds = jobs.Select(j => j.DeploymentId).Distinct().ToList();
        var jobPatches = (await db.Set<PatchDeploymentPatch>().AsNoTracking()
                .Where(p => deploymentIds.Contains(p.DeploymentId))
                .Select(p => new { p.DeploymentId, p.PatchId })
                .ToListAsync(cancellationToken))
            .ToLookup(p => p.DeploymentId, p => p.PatchId);

        foreach (var job in jobs)
        {
            var statuses = jobPatches[job.DeploymentId]
                .Select(id => states.TryGetValue(id, out var s) ? s.Status : (DevicePatchStatus?)null)
                .ToList();
            if (statuses.Count == 0)
            {
                continue;
            }

            if (job.Status == PatchJobStatus.PendingRestart)
            {
                if (statuses.All(s => s is DevicePatchStatus.Installed or DevicePatchStatus.NotApplicable or DevicePatchStatus.Hidden))
                {
                    job.ConfirmRestart(now);
                }
                else if (statuses.Any(s => s is DevicePatchStatus.Missing or DevicePatchStatus.Failed))
                {
                    job.FailAfterRestart("After the restart, at least one patch is still not installed.", now);
                    audit.Record("patch_deployment.device_failed", nameof(PatchDeployment), job.DeploymentId, $"Device {deviceId}: installation did not survive the restart.", new { jobId = job.Id, deviceId });
                }
            }
            else if (statuses.All(s => s is DevicePatchStatus.Installed or DevicePatchStatus.PendingRestart or DevicePatchStatus.NotApplicable))
            {
                job.MarkNotApplicable("Already installed or no longer applicable.", now);
            }
        }
    }
}
