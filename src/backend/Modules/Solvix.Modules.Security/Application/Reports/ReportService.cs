namespace Solvix.Modules.Security.Application.Reports;

using System.Text;
using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Security.Application.Common;
using Solvix.Modules.Security.Domain;
using Solvix.Modules.Security.Evaluation;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public sealed record CsvExport(string FileName, byte[] Content);

public interface IReportService
{
    Task<SecuritySummaryDto> SummaryAsync(CancellationToken cancellationToken);

    Task<CsvExport> DevicesCsvAsync(CancellationToken cancellationToken);

    Task<CsvExport> FindingsCsvAsync(bool includeResolved, CancellationToken cancellationToken);

    RulesCatalogDto Rules();
}

internal sealed class ReportService(
    ISolvixDbContext db,
    ITenantContext tenant,
    IClock clock,
    IPolicyContext policyContext,
    IAuditTrail audit) : IReportService
{
    private const int TrendDays = 30;
    private const int MaxExportRows = 50_000;

    public async Task<SecuritySummaryDto> SummaryAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var effective = await policyContext.GetAsync(cancellationToken);
        var staleBefore = now - TimeSpan.FromDays(effective.Settings.StaleAfterDays);

        var endpoints = await db.Set<ManagedEndpoint>().AsNoTracking()
            .Where(e => e.Status == EndpointStatus.Active)
            .Select(e => new { e.Band, e.Score, e.LastReportAtUtc })
            .ToListAsync(cancellationToken);
        var bands = Enum.GetValues<ComplianceBand>().ToDictionary(b => b, _ => 0);
        var scores = new List<int>();
        var stale = 0;
        foreach (var e in endpoints)
        {
            if (e.LastReportAtUtc is not { } last || last < staleBefore)
            {
                bands[ComplianceBand.Unknown]++;
                stale += e.LastReportAtUtc is null ? 0 : 1;
                continue;
            }

            bands[e.Band]++;
            if (e.Score is { } score)
            {
                scores.Add(score);
            }
        }

        var grouped = await db.Set<SecurityFinding>().AsNoTracking()
            .Where(f => f.Status == FindingStatus.Open)
            .GroupBy(f => new { f.RuleKey, f.Severity })
            .Select(g => new { g.Key.RuleKey, g.Key.Severity, Findings = g.Count(), Devices = g.Select(x => x.EndpointId).Distinct().Count() })
            .ToListAsync(cancellationToken);

        var bySeverity = Enum.GetValues<Severity>().ToDictionary(s => s, _ => 0);
        var byCategory = Enum.GetValues<RuleCategory>().ToDictionary(c => c, _ => 0);
        foreach (var row in grouped)
        {
            bySeverity[row.Severity] += row.Findings;
            if (RuleCatalog.TryGet(row.RuleKey, out var rule))
            {
                byCategory[rule.Category] += row.Findings;
            }
        }

        var topRules = grouped
            .GroupBy(r => r.RuleKey)
            .Select(g => new { Key = g.Key, Severity = g.Max(x => x.Severity), Devices = g.Max(x => x.Devices) })
            .OrderByDescending(r => r.Severity).ThenByDescending(r => r.Devices)
            .Take(10)
            .Select(r =>
            {
                var known = RuleCatalog.TryGet(r.Key, out var rule);
                return new TopRuleDto(r.Key, known ? rule.Title : r.Key, r.Severity, known ? rule.Category : RuleCategory.Configuration, r.Devices);
            })
            .ToList();

        var openAlerts = await db.Set<SecurityAlert>().CountAsync(a => a.Status == AlertStatus.New || a.Status == AlertStatus.Acknowledged, cancellationToken);
        var pendingRemediation = await db.Set<RemediationRequest>().CountAsync(r => r.Status == RemediationStatus.PendingApproval, cancellationToken);
        var pendingPolicies = await db.Set<SecurityPolicy>().CountAsync(p => p.Status == PolicyStatus.PendingApproval, cancellationToken);

        var tenantId = tenant.RequireTenantId();
        var since = now.AddDays(-TrendDays);
        var trendRows = await db.Database.SqlQuery<TrendRow>(
            $"""
            SELECT date_trunc('day', received_at_utc AT TIME ZONE 'UTC') AS "Day", AVG(score)::float8 AS "Average", COUNT(DISTINCT endpoint_id)::int AS "Devices"
            FROM security_snapshots
            WHERE tenant_id = {tenantId} AND received_at_utc >= {since}
            GROUP BY 1 ORDER BY 1
            """).ToListAsync(cancellationToken);

        return new SecuritySummaryDto(
            now,
            endpoints.Count,
            endpoints.Count - bands[ComplianceBand.Unknown],
            stale,
            scores.Count == 0 ? null : Math.Round(scores.Average(), 1),
            bands,
            bySeverity,
            byCategory,
            topRules,
            openAlerts,
            pendingRemediation,
            pendingPolicies,
            effective.IsBuiltIn ? effective.Name : $"{effective.Name} (v{effective.Version})",
            effective.Settings.EnforcementMode != EnforcementMode.DetectOnly,
            trendRows.Select(r => new TrendPointDto(new DateTimeOffset(DateTime.SpecifyKind(r.Day, DateTimeKind.Utc)), Math.Round(r.Average, 1), r.Devices)).ToList());
    }

    public async Task<CsvExport> DevicesCsvAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var effective = await policyContext.GetAsync(cancellationToken);
        var rows = await db.Set<ManagedEndpoint>().AsNoTracking().OrderBy(e => e.Hostname).Take(MaxExportRows).ToListAsync(cancellationToken);
        var csv = new StringBuilder();
        Csv.Row(csv, "Hostname", "Status", "Compliance", "Score", "Coverage", "Critical", "High", "Medium", "Low", "LastReportUtc", "OS", "Agent");
        foreach (var e in rows)
        {
            var summary = EndpointSummaryDto.From(e, now, effective.Settings.StaleAfterDays);
            Csv.Row(
                csv, Csv.Cell(e.Hostname), e.Status.ToString(), summary.Band.ToString(), Csv.Cell(e.Score),
                e.Coverage is { } c ? c.ToString("P0", System.Globalization.CultureInfo.InvariantCulture) : string.Empty,
                Csv.Cell(e.OpenCritical), Csv.Cell(e.OpenHigh), Csv.Cell(e.OpenMedium), Csv.Cell(e.OpenLow),
                Csv.Cell(e.LastReportAtUtc), Csv.Cell(e.OsCaption), Csv.Cell(e.AgentVersion));
        }

        audit.Record("report.exported", "Report", null, $"Exported the device compliance report ({rows.Count} devices).", new { report = "devices", rows = rows.Count });
        await db.SaveChangesAsync(cancellationToken);
        return new CsvExport($"solvix-devices-{now:yyyyMMdd}.csv", Csv.ToBytes(csv));
    }

    public async Task<CsvExport> FindingsCsvAsync(bool includeResolved, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var query = db.Set<SecurityFinding>().AsNoTracking().AsQueryable();
        if (!includeResolved)
        {
            query = query.Where(f => f.Status != FindingStatus.Resolved);
        }

        var rows = await query.OrderByDescending(f => f.LastDetectedAtUtc).Take(MaxExportRows).ToListAsync(cancellationToken);
        var hostnames = await db.Set<ManagedEndpoint>().AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.Hostname, cancellationToken);
        var csv = new StringBuilder();
        Csv.Row(csv, "Hostname", "Severity", "Category", "Rule", "Finding", "Evidence", "Status", "FirstDetectedUtc", "LastDetectedUtc", "ResolvedUtc", "Recurrences", "AcceptedUntilUtc");
        foreach (var f in rows.OrderByDescending(f => f.Severity))
        {
            Csv.Row(
                csv, Csv.Cell(hostnames.GetValueOrDefault(f.EndpointId)), f.Severity.ToString(),
                RuleCatalog.TryGet(f.RuleKey, out var rule) ? rule.Category.ToString() : string.Empty, f.RuleKey, Csv.Cell(f.Title), Csv.Cell(f.Evidence),
                f.Status.ToString(), Csv.Cell(f.FirstDetectedAtUtc), Csv.Cell(f.LastDetectedAtUtc), Csv.Cell(f.ResolvedAtUtc),
                Csv.Cell(f.RecurrenceCount), Csv.Cell(f.AcceptedUntilUtc));
        }

        audit.Record("report.exported", "Report", null, $"Exported the findings report ({rows.Count} findings).", new { report = "findings", rows = rows.Count, includeResolved });
        await db.SaveChangesAsync(cancellationToken);
        return new CsvExport($"solvix-findings-{now:yyyyMMdd}.csv", Csv.ToBytes(csv));
    }

    public RulesCatalogDto Rules() => new(
        RuleCatalog.All.Select(RuleDto.From).ToList(),
        SeverityRubric.All.Select(s => new SeverityDto(s.Severity, s.Meaning, s.Example, s.ScorePenalty)).ToList(),
        RemediationCatalog.All.Select(RemediationActionDto.From).ToList(),
        new ScoringDto(
            "Score = 100 − sum of penalties (Critical 40, High 20, Medium 10, Low 4), floored at 0. Repeats of one rule count at most twice its weight. Unknown and not-applicable checks never cost points.",
            ComplianceScorer.CompliantThreshold,
            ComplianceScorer.NonCompliantThreshold,
            ComplianceScorer.MinimumCoverageForCompliant,
            "NonCompliant: score below 70 or any Critical failure. AtRisk: score below 90, any High failure, or less than 90% of checks assessable. Compliant otherwise. Unknown: no recent report."),
        ProductScope);

    public const string ProductScope =
        "Solvix reads the security configuration of Windows devices through supported Windows interfaces, evaluates it against your policy and reports the result. "
        + "It is a posture-visibility and policy-management tool. It is not antivirus or endpoint detection and response (EDR) software: it contains no malware-detection engine, "
        + "no behavioural analysis and no real-time blocking. Malware findings shown here are what Microsoft Defender itself reported. A compliance score describes configuration, not whether a device is free of compromise.";

    private sealed class TrendRow
    {
        public DateTime Day { get; set; }

        public double Average { get; set; }

        public int Devices { get; set; }
    }
}
