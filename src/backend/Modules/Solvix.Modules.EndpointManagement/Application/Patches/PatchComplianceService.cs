namespace Solvix.Modules.EndpointManagement.Application.Patches;

using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Solvix.Endpoint.Contracts.Patching;
using Solvix.Modules.Devices.Domain;
using Solvix.Modules.EndpointManagement.Domain.Patches;
using Solvix.Modules.Organization.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Auditing;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Tenancy;

public interface IPatchComplianceService
{
    Task<PatchComplianceReportDto> GetReportAsync(ComplianceQuery query, CancellationToken cancellationToken);

    Task<string> ExportCsvAsync(ComplianceQuery query, CancellationToken cancellationToken);

    Task<PagedResult<PatchFailureDto>> ListFailuresAsync(int page, int pageSize, CancellationToken cancellationToken);

    Task<DeviceUpdateStatusDto> GetDeviceStatusAsync(Guid deviceId, CancellationToken cancellationToken);

    /// <summary>Asks the agent to rescan on its next heartbeat.</summary>
    Task RequestScanAsync(Guid deviceId, CancellationToken cancellationToken);
}

/// <summary>
/// Patch compliance. A device is <b>Compliant</b> when every approved patch that
/// applies to it is installed, <b>PendingRestart</b> when that is only true after
/// a restart, <b>NonCompliant</b> when an approved patch is missing or failed, and
/// <b>Unknown</b> without a successful scan within the staleness limit (default 7 days).
/// </summary>
internal sealed class PatchComplianceService(ISolvixDbContext db, ITenantContext tenantContext, IClock clock, IAuditTrail audit) : IPatchComplianceService
{
    public const int RepeatedFailureThreshold = 3;

    public async Task<PatchComplianceReportDto> GetReportAsync(ComplianceQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var now = clock.UtcNow;
        var staleDays = Math.Clamp(query.StaleAfterDays, 1, 365);
        var staleBefore = now.AddDays(-staleDays);

        var devicesQuery = db.Set<Device>().AsNoTracking().Where(d => d.Status == DeviceStatus.Active);
        if (query.BranchId is { } branchId)
        {
            devicesQuery = devicesQuery.Where(d => d.BranchId == branchId);
        }

        if (query.DepartmentId is { } departmentId)
        {
            devicesQuery = devicesQuery.Where(d => d.DepartmentId == departmentId);
        }

        if (query.DeviceGroupId is { } groupId)
        {
            devicesQuery = devicesQuery.Where(d => db.Set<DeviceGroupMember>().Any(m => m.DeviceGroupId == groupId && m.DeviceId == d.Id));
        }

        var devices = await devicesQuery.Select(d => new { d.Id, d.Hostname, d.BranchId, d.DepartmentId, d.LastSeenAtUtc }).ToListAsync(cancellationToken);
        var deviceIds = devices.Select(d => d.Id).ToList();
        var summaries = await db.Set<DeviceUpdateSummary>().AsNoTracking().Where(s => deviceIds.Contains(s.DeviceId)).ToDictionaryAsync(s => s.DeviceId, cancellationToken);
        var states = await (
                from st in db.Set<DevicePatchState>().AsNoTracking()
                join p in db.Set<Patch>().AsNoTracking() on st.PatchId equals p.Id
                where deviceIds.Contains(st.DeviceId)
                    && (st.Status == DevicePatchStatus.Missing || st.Status == DevicePatchStatus.Failed || st.Status == DevicePatchStatus.PendingRestart)
                select new { st.DeviceId, st.PatchId, st.Status, p.Title, p.KbArticleIds, p.Severity, p.Classification, p.ApprovalState })
            .ToListAsync(cancellationToken);
        var statesByDevice = states.ToLookup(s => s.DeviceId);

        var rows = devices.Select(d =>
        {
            var own = statesByDevice[d.Id].ToList();
            var summary = summaries.GetValueOrDefault(d.Id);
            var outstanding = own.Where(s => s.Status is DevicePatchStatus.Missing or DevicePatchStatus.Failed).ToList();
            var approvedOutstanding = outstanding.Count(s => s.ApprovalState == PatchApprovalState.Approved);
            var pendingRestart = own.Count(s => s.Status == DevicePatchStatus.PendingRestart);
            var rebootRequired = summary?.RebootRequired == true;
            var status = summary?.LastSuccessfulScanUtc is not { } scanned || scanned < staleBefore
                ? DeviceComplianceStatus.Unknown
                : approvedOutstanding > 0
                    ? DeviceComplianceStatus.NonCompliant
                    : rebootRequired || own.Any(s => s.Status == DevicePatchStatus.PendingRestart && s.ApprovalState == PatchApprovalState.Approved)
                        ? DeviceComplianceStatus.PendingRestart
                        : DeviceComplianceStatus.Compliant;
            return new DevicePatchComplianceDto(
                d.Id,
                d.Hostname,
                d.BranchId,
                d.DepartmentId,
                status,
                approvedOutstanding,
                outstanding.Count(s => s.Severity == MsrcSeverity.Critical || s.Classification == UpdateClassification.CriticalUpdates),
                outstanding.Count(s => s.Classification == UpdateClassification.SecurityUpdates || s.Severity is MsrcSeverity.Critical or MsrcSeverity.Important),
                outstanding.Count,
                own.Count(s => s.Status == DevicePatchStatus.Failed),
                pendingRestart,
                rebootRequired,
                summary?.RebootRequiredSinceUtc,
                summary?.LastSuccessfulScanUtc,
                d.LastSeenAtUtc);
        }).OrderBy(r => r.Status == DeviceComplianceStatus.Compliant).ThenByDescending(r => r.MissingCritical).ThenBy(r => r.Hostname, StringComparer.OrdinalIgnoreCase).ToList();

        var branches = await db.Set<Branch>().AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.Name, cancellationToken);
        var departments = await db.Set<Department>().AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.Name, cancellationToken);
        var groups = await db.Set<DeviceGroup>().AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken);
        var memberships = (await db.Set<DeviceGroupMember>().AsNoTracking().Where(m => deviceIds.Contains(m.DeviceId))
            .Select(m => new { m.DeviceGroupId, m.DeviceId }).ToListAsync(cancellationToken)).ToLookup(m => m.DeviceGroupId, m => m.DeviceId);
        var rowById = rows.ToDictionary(r => r.DeviceId);

        static ComplianceBreakdownDto Breakdown(Guid? id, string name, IReadOnlyCollection<DevicePatchComplianceDto> devices)
        {
            var compliant = devices.Count(d => d.Status == DeviceComplianceStatus.Compliant);
            return new ComplianceBreakdownDto(id, name, devices.Count, compliant, devices.Count == 0 ? 100 : Math.Round(compliant * 100.0 / devices.Count, 1));
        }

        var compliantCount = rows.Count(r => r.Status == DeviceComplianceStatus.Compliant);
        return new PatchComplianceReportDto(
            now,
            staleDays,
            rows.Count,
            compliantCount,
            rows.Count(r => r.Status == DeviceComplianceStatus.PendingRestart),
            rows.Count(r => r.Status == DeviceComplianceStatus.NonCompliant),
            rows.Count(r => r.Status == DeviceComplianceStatus.Unknown),
            rows.Count == 0 ? 100 : Math.Round(compliantCount * 100.0 / rows.Count, 1),
            rows.GroupBy(r => r.BranchId).Select(g => Breakdown(g.Key, g.Key is { } id ? branches.GetValueOrDefault(id, "?") : "(no branch)", g.ToList())).OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            rows.GroupBy(r => r.DepartmentId).Select(g => Breakdown(g.Key, g.Key is { } id ? departments.GetValueOrDefault(id, "?") : "(no department)", g.ToList())).OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            groups.Select(g => Breakdown(g.Key, g.Value, memberships[g.Key].Where(rowById.ContainsKey).Select(id => rowById[id]).ToList())).Where(b => b.Devices > 0).OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            states.Where(s => s.Status is DevicePatchStatus.Missing or DevicePatchStatus.Failed)
                .GroupBy(s => s.PatchId)
                .Select(g => new MissingPatchSummaryDto(g.Key, g.First().Title, g.First().KbArticleIds, g.First().Severity, g.First().Classification, g.First().ApprovalState, g.Count()))
                .OrderByDescending(m => m.Severity).ThenByDescending(m => m.DevicesMissing)
                .Take(25)
                .ToList(),
            rows);
    }

    public async Task<string> ExportCsvAsync(ComplianceQuery query, CancellationToken cancellationToken)
    {
        var report = await GetReportAsync(query, cancellationToken);
        var csv = new StringBuilder();
        csv.AppendLine("device_id,hostname,status,missing_approved,missing_critical,missing_security,missing_total,failed,pending_restart,reboot_required,reboot_required_since_utc,last_successful_scan_utc,last_seen_utc");
        foreach (var r in report.DeviceRows)
        {
            csv.Append(CultureInfo.InvariantCulture, $"{r.DeviceId},{Escape(r.Hostname)},{r.Status},{r.MissingApproved},{r.MissingCritical},{r.MissingSecurity},{r.MissingTotal},{r.Failed},{r.PendingRestart},{(r.RebootRequired ? "true" : "false")},{Iso(r.RebootRequiredSinceUtc)},{Iso(r.LastSuccessfulScanUtc)},{Iso(r.LastSeenAtUtc)}");
            csv.AppendLine();
        }

        return csv.ToString();
    }

    public async Task<PagedResult<PatchFailureDto>> ListFailuresAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var request = new PageRequest(page, pageSize);
        var failures =
            from st in db.Set<DevicePatchState>().AsNoTracking()
            join p in db.Set<Patch>().AsNoTracking() on st.PatchId equals p.Id
            join d in db.Set<Device>().AsNoTracking() on st.DeviceId equals d.Id
            where st.Status == DevicePatchStatus.Failed && d.Status == DeviceStatus.Active
            select new { d.Id, d.Hostname, PatchId = p.Id, p.Title, p.KbArticleIds, st.FailureCount, st.LastFailureHResult, st.LastFailureAtUtc };
        var total = await failures.LongCountAsync(cancellationToken);
        var rows = await failures.OrderByDescending(f => f.LastFailureAtUtc).ThenBy(f => f.Hostname)
            .Skip(request.Skip).Take(request.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<PatchFailureDto>(
            rows.Select(f => new PatchFailureDto(
                f.Id, f.Hostname, f.PatchId, f.Title, f.KbArticleIds, f.FailureCount, f.LastFailureHResult,
                f.LastFailureHResult is { } h ? $"0x{unchecked((uint)h):X8}" : null,
                f.LastFailureAtUtc, f.FailureCount >= RepeatedFailureThreshold)).ToList(),
            request.SafePage,
            request.SafePageSize,
            total);
    }

    public async Task<DeviceUpdateStatusDto> GetDeviceStatusAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var device = await db.Set<Device>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken)
            ?? throw new NotFoundException("Device", deviceId);
        var summary = await db.Set<DeviceUpdateSummary>().AsNoTracking().FirstOrDefaultAsync(s => s.DeviceId == deviceId, cancellationToken);
        var patches = await (
                from st in db.Set<DevicePatchState>().AsNoTracking()
                join p in db.Set<Patch>().AsNoTracking() on st.PatchId equals p.Id
                where st.DeviceId == deviceId
                orderby st.Status, p.Severity descending, p.Title
                select new DevicePatchRowDto(p.Id, p.Title, p.KbArticleIds, p.Classification, p.Severity, p.ApprovalState, st.Status, st.InstalledAtUtc, st.FailureCount, st.LastFailureHResult))
            .ToListAsync(cancellationToken);
        return new DeviceUpdateStatusDto(
            device.Id,
            device.Hostname,
            summary?.LastScanCompletedUtc,
            summary?.LastScanResult,
            summary?.LastScanError,
            summary?.LastSuccessfulScanUtc,
            summary?.ServiceState,
            summary?.AutomaticUpdatesNotificationLevel,
            summary?.UpdateSource,
            summary?.LastSuccessfulInstallUtc,
            summary?.RebootRequired ?? false,
            summary?.RebootReasons ?? [],
            summary?.RebootRequiredSinceUtc,
            summary?.LastBootUtc ?? device.LastBootUtc,
            summary?.InventoryRequested ?? true,
            patches);
    }

    public async Task RequestScanAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var device = await db.Set<Device>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken)
            ?? throw new NotFoundException("Device", deviceId);
        var summary = await db.Set<DeviceUpdateSummary>().FirstOrDefaultAsync(s => s.DeviceId == deviceId, cancellationToken);
        if (summary is null)
        {
            db.Set<DeviceUpdateSummary>().Add(new DeviceUpdateSummary(tenantContext.RequireTenantId(), deviceId));
        }
        else
        {
            summary.RequestInventory();
        }

        audit.Record("patch.scan_requested", nameof(Device), deviceId, $"Update scan requested for '{device.Hostname}'.");
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string Escape(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;

    private static string Iso(DateTimeOffset? value) =>
        value?.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) ?? string.Empty;
}
