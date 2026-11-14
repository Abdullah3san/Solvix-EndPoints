namespace Solvix.Modules.Security.Application.Common;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Solvix.Modules.Security.Domain;
using Solvix.Modules.Security.Evaluation;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Persistence;

/// <summary>
/// Turns an evaluation into persistent state: scores the device, opens/updates/resolves findings, raises alerts for
/// serious new findings and — only when the active policy allows — proposes remediation. It never calls SaveChanges:
/// the caller commits everything (including audit entries) in one transaction.
/// </summary>
public interface IPostureProcessor
{
    Task<ComplianceScore> AssessAsync(ManagedEndpoint endpoint, PostureSnapshot snapshot, PostureReport report, CancellationToken cancellationToken);

    /// <summary>Re-evaluates every active device's latest report against the current policy and advisories.</summary>
    Task<int> ReevaluateAllAsync(CancellationToken cancellationToken);
}

internal sealed partial class PostureProcessor(
    ISolvixDbContext db,
    IPolicyContext policyContext,
    IAuditTrail audit,
    IClock clock,
    ILogger<PostureProcessor> logger) : IPostureProcessor
{
    private const int ReevaluationBatch = 100;
    private const int ReevaluationLimit = 2000;
    private static readonly TimeSpan RemediationProposalCooldown = TimeSpan.FromDays(7);

    public async Task<ComplianceScore> AssessAsync(ManagedEndpoint endpoint, PostureSnapshot snapshot, PostureReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(report);

        var now = clock.UtcNow;
        var effective = await policyContext.GetAsync(cancellationToken);
        var results = PostureEvaluator.Evaluate(report, effective.Settings, effective.Advisories);

        var findings = await db.Set<SecurityFinding>().Where(f => f.EndpointId == endpoint.Id).ToListAsync(cancellationToken);
        var accepted = findings
            .Where(f => f.Status == FindingStatus.Accepted && f.AcceptedUntilUtc > now)
            .Select(f => (f.RuleKey, f.SubjectKey))
            .ToHashSet();

        var score = ComplianceScorer.Score(results, accepted, isStale: false);
        snapshot.SetEvaluation(results, score, effective.PolicyId, effective.Version);

        var (opened, resolved) = Reconcile(endpoint, findings, results, now);
        await RaiseAlertsAsync(endpoint, opened, resolved, now, cancellationToken);
        await ProposeRemediationAsync(endpoint, effective, findings, results, now, cancellationToken);

        var open = findings.Where(f => f.Status == FindingStatus.Open).ToList();
        endpoint.ApplyAssessment(
            score.Score,
            score.Band,
            score.Coverage,
            open.Count(f => f.Severity == Severity.Critical),
            open.Count(f => f.Severity == Severity.High),
            open.Count(f => f.Severity == Severity.Medium),
            open.Count(f => f.Severity == Severity.Low));
        return score;
    }

    public async Task<int> ReevaluateAllAsync(CancellationToken cancellationToken)
    {
        policyContext.Invalidate();
        var processed = 0;
        var skip = 0;
        while (processed < ReevaluationLimit)
        {
            var batch = await db.Set<ManagedEndpoint>()
                .Where(e => e.Status == EndpointStatus.Active && e.LatestSnapshotId != null)
                .OrderBy(e => e.Id)
                .Skip(skip).Take(ReevaluationBatch)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            var snapshotIds = batch.Select(e => e.LatestSnapshotId!.Value).ToList();
            var snapshots = await db.Set<PostureSnapshot>().Where(s => snapshotIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, cancellationToken);
            foreach (var endpoint in batch)
            {
                if (snapshots.TryGetValue(endpoint.LatestSnapshotId!.Value, out var snapshot) && snapshot.Report is { } report)
                {
                    await AssessAsync(endpoint, snapshot, report, cancellationToken);
                    processed++;
                }
            }

            skip += ReevaluationBatch;
        }

        if (processed >= ReevaluationLimit)
        {
            LogReevaluationCapped(logger, ReevaluationLimit);
        }

        return processed;
    }

    private (List<SecurityFinding> Opened, List<SecurityFinding> Resolved) Reconcile(
        ManagedEndpoint endpoint, List<SecurityFinding> findings, IReadOnlyList<CheckResult> results, DateTimeOffset now)
    {
        var opened = new List<SecurityFinding>();
        var resolved = new List<SecurityFinding>();

        var failures = results
            .Where(r => r.Outcome == CheckOutcome.Fail)
            .GroupBy(r => (r.RuleKey, r.SubjectKey))
            .ToDictionary(g => g.Key, g => g.First());
        var unknownRules = results.Where(r => r.Outcome == CheckOutcome.Unknown).Select(r => r.RuleKey).ToHashSet(StringComparer.Ordinal);

        foreach (var (key, failure) in failures)
        {
            var existing = findings.FirstOrDefault(f => f.RuleKey == key.RuleKey && f.SubjectKey == key.SubjectKey);
            if (existing is null)
            {
                var created = new SecurityFinding(
                    endpoint.TenantId, endpoint.Id, key.RuleKey, key.SubjectKey, failure.Severity, failure.Title, failure.Evidence,
                    failure.RemediationActionKey, failure.RemediationParameter, now);
                db.Set<SecurityFinding>().Add(created);
                findings.Add(created);
                opened.Add(created);
            }
            else if (existing.Observe(failure.Severity, failure.Title, failure.Evidence, failure.RemediationActionKey, failure.RemediationParameter, now))
            {
                opened.Add(existing);
            }
        }

        // A finding is only resolved by positive evidence. Unknown (data missing) never resolves anything.
        foreach (var finding in findings.Where(f => f.IsActive && !failures.ContainsKey((f.RuleKey, f.SubjectKey)) && !unknownRules.Contains(f.RuleKey)))
        {
            finding.Resolve(now);
            resolved.Add(finding);
        }

        return (opened, resolved);
    }

    private async Task RaiseAlertsAsync(
        ManagedEndpoint endpoint, List<SecurityFinding> opened, List<SecurityFinding> resolved, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var finding in opened.Where(f => f.Severity >= Severity.High))
        {
            db.Set<SecurityAlert>().Add(new SecurityAlert(
                endpoint.TenantId, endpoint.Id, finding.RuleKey, $"FND:{finding.Id:N}:{now.UtcTicks}", finding.Severity, finding.Title, finding.Evidence, finding.Id, now));
        }

        if (resolved.Count == 0)
        {
            return;
        }

        var resolvedIds = resolved.Select(f => f.Id).ToList();
        var stillOpen = await db.Set<SecurityAlert>()
            .Where(a => a.FindingId != null && resolvedIds.Contains(a.FindingId.Value) && (a.Status == AlertStatus.New || a.Status == AlertStatus.Acknowledged))
            .ToListAsync(cancellationToken);
        foreach (var alert in stillOpen)
        {
            alert.Resolve(null, "The underlying finding was resolved by a later assessment.", now);
        }
    }

    private async Task ProposeRemediationAsync(
        ManagedEndpoint endpoint, EffectivePolicy effective, List<SecurityFinding> findings, IReadOnlyList<CheckResult> results, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var settings = effective.Settings;
        if (settings.EnforcementMode == EnforcementMode.DetectOnly || settings.AllowedRemediationActions.Count == 0)
        {
            return;
        }

        var failing = results.Where(r => r.Outcome == CheckOutcome.Fail && r.RemediationActionKey is not null).ToList();
        if (failing.Count == 0)
        {
            return;
        }

        var since = now - RemediationProposalCooldown;
        var recent = await db.Set<RemediationRequest>()
            .Where(r => r.EndpointId == endpoint.Id && (r.RequestedAtUtc > since
                || r.Status == RemediationStatus.PendingApproval || r.Status == RemediationStatus.Approved || r.Status == RemediationStatus.Dispatched))
            .ToListAsync(cancellationToken);

        foreach (var failure in failing)
        {
            var key = failure.RemediationActionKey!;
            if (!settings.AllowedRemediationActions.Contains(key, StringComparer.Ordinal)
                || !RemediationCatalog.TryGet(key, out var action)
                || recent.Any(r => r.ActionKey == key && r.Parameter == failure.RemediationParameter))
            {
                continue;
            }

            var finding = findings.FirstOrDefault(f => f.RuleKey == failure.RuleKey && f.SubjectKey == failure.SubjectKey);
            var request = RemediationRequest.BySystem(
                endpoint.TenantId, endpoint.Id, finding?.Id, action, failure.RemediationParameter,
                $"Proposed by policy '{effective.Name}': {failure.Title}", now);
            db.Set<RemediationRequest>().Add(request);
            recent.Add(request);

            var policyActor = AuditActor.System($"policy:{effective.Name}");
            audit.Record(
                "remediation.requested", nameof(RemediationRequest), request.Id,
                $"Policy '{effective.Name}' proposed '{action.Title}' for {endpoint.Hostname}.",
                new { endpoint = endpoint.Id, action = key, parameter = failure.RemediationParameter, risk = action.Risk.ToString(), finding = finding?.Id },
                policyActor);

            if (settings.EnforcementMode == EnforcementMode.AutoApproveLowRisk && action.Risk == RemediationRisk.Low && effective.PolicyId is { } policyId)
            {
                request.AutoApprove(policyId, now);
                audit.Record(
                    "remediation.auto_approved", nameof(RemediationRequest), request.Id,
                    $"'{action.Title}' for {endpoint.Hostname} was approved automatically: Low-risk action allowed by policy '{effective.Name}'.",
                    new { policy = policyId, action = key },
                    policyActor);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Re-evaluation stopped after {Limit} devices; remaining devices are updated by their next report.")]
    private static partial void LogReevaluationCapped(ILogger logger, int limit);
}
