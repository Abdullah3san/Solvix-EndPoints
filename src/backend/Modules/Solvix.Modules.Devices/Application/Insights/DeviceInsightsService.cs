namespace Solvix.Modules.Devices.Application.Insights;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts;
using Solvix.Modules.Devices.Application.Agent;
using Solvix.Modules.Devices.Configuration;
using Solvix.Modules.Devices.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Directory;
using Solvix.SharedKernel.Persistence;

public interface IDeviceInsightsService
{
    Task<DeviceOverviewDto> GetOverviewAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<DeviceAlertDto>> GetAlertsAsync(CancellationToken cancellationToken);
}

/// <summary>Read-only analytics over the tenant's devices. Everything is computed from stored data on request.</summary>
internal sealed class DeviceInsightsService(
    ISolvixDbContext db,
    IOrganizationDirectory organization,
    IAgentReleaseCatalog releases,
    IOptions<DevicesOptions> options,
    IClock clock) : IDeviceInsightsService
{
    /// <summary>An active device that has not checked in for this long is reported as not reporting.</summary>
    public const int NotReportingHours = 24;

    /// <summary>The newest device certificate expiring within this many days is reported (agents renew well before).</summary>
    public const int CertificateWarningDays = 7;

    private const int SampleSize = 5;
    private const int MaxOperatingSystems = 8;

    public async Task<DeviceOverviewDto> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var onlineSince = now.AddSeconds(-options.Value.Agent.OfflineAfterSeconds);
        var active = ActiveDevices();

        var platforms = (await active.GroupBy(d => d.Platform).Select(g => new { Key = g.Key, Count = g.Count() }).ToListAsync(cancellationToken))
            .OrderByDescending(p => p.Count)
            .Select(p => new NamedCountDto(p.Key.ToString(), p.Key.ToString(), p.Count))
            .ToList();

        var osRows = await active.GroupBy(d => d.OsName).Select(g => new { Name = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var operatingSystems = osRows
            .Select(r => (Name: string.IsNullOrWhiteSpace(r.Name) ? "Unknown" : r.Name!, r.Count))
            .GroupBy(r => r.Name)
            .Select(g => (Name: g.Key, Count: g.Sum(r => r.Count)))
            .OrderByDescending(r => r.Count).ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();
        var shownOs = operatingSystems.Take(MaxOperatingSystems).Select(r => new NamedCountDto(r.Name, r.Name, r.Count)).ToList();
        if (operatingSystems.Count > MaxOperatingSystems)
        {
            shownOs.Add(new NamedCountDto("Other", "Other", operatingSystems.Skip(MaxOperatingSystems).Sum(r => r.Count)));
        }

        var healthRows = await active.GroupBy(d => d.HealthStatus).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var health = new[] { AgentHealthStatus.Healthy, AgentHealthStatus.Degraded, AgentHealthStatus.Unhealthy }
            .Select(s => new NamedCountDto(s.ToString(), s.ToString(), healthRows.Where(r => r.Status == s).Sum(r => r.Count)))
            .Append(new NamedCountDto("Unknown", "No report yet", healthRows.Where(r => r.Status is null).Sum(r => r.Count)))
            .Where(h => h.Count > 0)
            .ToList();

        var branchRows = await active
            .GroupBy(d => d.BranchId)
            .Select(g => new { BranchId = g.Key, Total = g.Count(), Online = g.Count(d => d.LastSeenAtUtc >= onlineSince) })
            .ToListAsync(cancellationToken);
        var ids = branchRows.Where(r => r.BranchId.HasValue).Select(r => r.BranchId!.Value).ToList();
        var names = ids.Count > 0 ? await organization.FindBranchesAsync(ids, cancellationToken) : null;
        var branches = branchRows
            .Select(r => new BranchFleetDto(
                r.BranchId,
                r.BranchId is { } id ? (names is not null && names.TryGetValue(id, out var b) ? b.Name : "Removed branch") : "No branch",
                r.Total,
                r.Online,
                r.Total - r.Online))
            .OrderBy(b => b.BranchId is null).ThenByDescending(b => b.Total).ThenBy(b => b.Name, StringComparer.Ordinal)
            .ToList();

        return new DeviceOverviewDto(platforms, shownOs, health, branches);
    }

    public async Task<IReadOnlyList<DeviceAlertDto>> GetAlertsAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var active = ActiveDevices();
        var alerts = new List<DeviceAlertDto>();

        async Task AddAsync(string code, AlertSeverity severity, string title, Func<int, string> message, IQueryable<Device> affected)
        {
            var count = await affected.CountAsync(cancellationToken);
            if (count == 0)
            {
                return;
            }

            var sample = await affected.OrderBy(d => d.Hostname).Take(SampleSize).Select(d => new { d.Id, d.Hostname, d.DisplayName }).ToListAsync(cancellationToken);
            alerts.Add(new DeviceAlertDto(code, severity, title, message(count), count, sample.Select(d => new AlertDeviceDto(d.Id, string.IsNullOrWhiteSpace(d.DisplayName) ? d.Hostname : d.DisplayName)).ToList()));
        }

        // Latest non-revoked certificate per device: a device that has already received its renewed
        // certificate must not be flagged for the old one that is still waiting to be retired.
        var latestCertificate = db.Set<DeviceCredential>().AsNoTracking()
            .Where(c => c.RevokedAtUtc == null)
            .GroupBy(c => c.DeviceId)
            .Select(g => new { DeviceId = g.Key, NotAfter = g.Max(c => c.NotAfterUtc) });
        var warnBefore = now.AddDays(CertificateWarningDays);
        IQueryable<Device> WithCertificate(bool expired) =>
            from c in latestCertificate
            join d in active on c.DeviceId equals d.Id
            where expired ? c.NotAfter < now : c.NotAfter >= now && c.NotAfter < warnBefore
            select d;

        await AddAsync(
            "certificate_expired", AlertSeverity.Critical, "Device certificate expired",
            n => $"{n} {(n == 1 ? "device has" : "devices have")} no valid certificate and cannot report. Retire and re-enroll {(n == 1 ? "it" : "them")}.",
            WithCertificate(expired: true));
        await AddAsync("agent_unhealthy", AlertSeverity.Critical, "Agents reporting unhealthy", n => $"{n} {(n == 1 ? "agent reports" : "agents report")} an unhealthy state.", active.Where(d => d.HealthStatus == AgentHealthStatus.Unhealthy));
        await AddAsync(
            "not_reporting", AlertSeverity.Warning, "Computers not reporting",
            n => $"{n} {(n == 1 ? "computer has" : "computers have")} not checked in for more than {NotReportingHours} hours.",
            active.Where(d => d.LastSeenAtUtc == null || d.LastSeenAtUtc < now.AddHours(-NotReportingHours)));
        await AddAsync("agent_degraded", AlertSeverity.Warning, "Agents reporting degraded", n => $"{n} {(n == 1 ? "agent reports" : "agents report")} a degraded state.", active.Where(d => d.HealthStatus == AgentHealthStatus.Degraded));
        await AddAsync(
            "certificate_expiring", AlertSeverity.Warning, "Device certificate expiring",
            n => $"{n} {(n == 1 ? "device's certificate expires" : "devices' certificates expire")} within {CertificateWarningDays} days and has not been renewed. Agents renew automatically while they can reach the server.",
            WithCertificate(expired: false));

        var versions = await active.Select(d => d.AgentVersion).Distinct().ToListAsync(cancellationToken);
        var outdated = versions.Where(releases.IsUpdateAvailable).ToList();
        if (outdated.Count > 0)
        {
            await AddAsync(
                "agent_outdated", AlertSeverity.Info, "Agent updates available",
                n => $"{n} {(n == 1 ? "computer runs" : "computers run")} an agent older than v{releases.LatestVersion}.",
                active.Where(d => outdated.Contains(d.AgentVersion)));
        }

        var pending = await db.Set<StagedComputer>().AsNoTracking().CountAsync(s => !db.Set<Device>().Any(d => d.Hostname.ToUpper() == s.NormalizedHostname), cancellationToken);
        if (pending > 0)
        {
            alerts.Add(new DeviceAlertDto(
                "staged_pending", AlertSeverity.Info, "Agent not installed yet",
                $"{pending} expected {(pending == 1 ? "computer has" : "computers have")} not installed the agent.", pending, []));
        }

        return alerts.OrderByDescending(a => a.Severity).ThenByDescending(a => a.Count).ToList();
    }

    private IQueryable<Device> ActiveDevices() => db.Set<Device>().AsNoTracking().Where(d => d.Status == DeviceStatus.Active);
}
