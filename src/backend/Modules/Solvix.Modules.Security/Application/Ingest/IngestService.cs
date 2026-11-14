namespace Solvix.Modules.Security.Application.Ingest;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Security.Application.Common;
using Solvix.Modules.Security.Domain;
using Solvix.Modules.Security.Evaluation;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public interface IIngestService
{
    Task<ReportReceiptDto> ReportAsync(ManagedEndpoint endpoint, PostureReport report, CancellationToken cancellationToken);

    Task<IReadOnlyList<AgentCommandDto>> GetCommandsAsync(ManagedEndpoint endpoint, CancellationToken cancellationToken);

    Task SubmitResultAsync(ManagedEndpoint endpoint, Guid commandId, CommandResultRequest result, CancellationToken cancellationToken);
}

/// <summary>Tunables for device ingestion (section <c>Solvix:Security</c>).</summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Solvix:Security";

    /// <summary>A device may not report more often than this. Protects the database from a runaway agent.</summary>
    public int MinReportIntervalSeconds { get; set; } = 30;
}

internal sealed class IngestService(
    ISolvixDbContext db,
    ITenantContext tenant,
    IPolicyContext policyContext,
    IPostureProcessor processor,
    IAuditTrail audit,
    IClock clock,
    Microsoft.Extensions.Options.IOptions<SecurityOptions> options) : IIngestService
{
    public const int NextReportAfterSeconds = 3600;
    private const int MaxSnapshotsPerEndpoint = 200;
    private static readonly TimeSpan EventRetention = TimeSpan.FromDays(90);
    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxReportAge = TimeSpan.FromDays(7);

    public async Task<ReportReceiptDto> ReportAsync(ManagedEndpoint endpoint, PostureReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(report);
        var now = clock.UtcNow;

        if (endpoint.LastReportAtUtc is { } last && now - last < TimeSpan.FromSeconds(options.Value.MinReportIntervalSeconds))
        {
            throw new TooManyRequestsException(Math.Max(1, options.Value.MinReportIntervalSeconds - (int)(now - last).TotalSeconds));
        }

        if (report.CollectedAtUtc > now + MaxClockSkew)
        {
            throw new ValidationException("collectedAtUtc", "The collection time is in the future. Check the device clock.");
        }

        if (report.CollectedAtUtc < now - MaxReportAge)
        {
            throw new ValidationException("collectedAtUtc", "The report is more than 7 days old and was rejected.");
        }

        var tenantId = tenant.RequireTenantId();
        var previous = endpoint.LatestSnapshotId is { } previousId
            ? await db.Set<PostureSnapshot>().FirstOrDefaultAsync(s => s.Id == previousId, cancellationToken)
            : null;

        var snapshot = new PostureSnapshot(tenantId, endpoint.Id, report, now);
        db.Set<PostureSnapshot>().Add(snapshot);
        previous?.Compact();
        endpoint.RecordReport(snapshot, report.Hostname, report.OsCaption, report.OsVersion, report.AgentVersion, report.IsDomainJoined);

        var score = await processor.AssessAsync(endpoint, snapshot, report, cancellationToken);
        await ProcessEventsAsync(endpoint, report, now, cancellationToken);

        var pending = await db.Set<RemediationRequest>().CountAsync(r => r.EndpointId == endpoint.Id && r.Status == RemediationStatus.Approved, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await PruneAsync(endpoint.Id, now, cancellationToken);
        return new ReportReceiptDto(snapshot.Id, score.Score, score.Band, pending, NextReportAfterSeconds);
    }

    public async Task<IReadOnlyList<AgentCommandDto>> GetCommandsAsync(ManagedEndpoint endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var now = clock.UtcNow;
        var effective = await policyContext.GetAsync(cancellationToken);
        var requests = await db.Set<RemediationRequest>()
            .Where(r => r.EndpointId == endpoint.Id && (r.Status == RemediationStatus.Approved || r.Status == RemediationStatus.PendingApproval || r.Status == RemediationStatus.Dispatched))
            .OrderBy(r => r.RequestedAtUtc)
            .ToListAsync(cancellationToken);

        var commands = new List<AgentCommandDto>();
        foreach (var request in requests)
        {
            if (request.ExpireIfDue(now))
            {
                audit.Record("remediation.expired", nameof(RemediationRequest), request.Id, $"Remediation '{request.ActionKey}' expired ({request.Status}).");
                continue;
            }

            if (request.Status != RemediationStatus.Approved)
            {
                continue;
            }

            // Approval is not a standing licence: the policy must still allow it at the moment of delivery.
            var allowed = effective.Settings.EnforcementMode != EnforcementMode.DetectOnly
                && effective.Settings.AllowedRemediationActions.Contains(request.ActionKey, StringComparer.Ordinal)
                && RemediationCatalog.TryGet(request.ActionKey, out _);
            if (!allowed)
            {
                request.CancelBySystem("The active policy no longer permits this action.", now);
                audit.Record("remediation.cancelled", nameof(RemediationRequest), request.Id, $"'{request.ActionKey}' was not delivered: the active policy no longer permits it.");
                continue;
            }

            request.MarkDispatched(now);
            audit.Record("remediation.dispatched", nameof(RemediationRequest), request.Id, $"Delivered '{request.ActionKey}' to {endpoint.Hostname}.", new { request.ActionKey, request.Parameter });
            commands.Add(new AgentCommandDto(request.Id, request.ActionKey, request.Parameter, request.ExpiresAtUtc));
        }

        await db.SaveChangesAsync(cancellationToken);
        return commands;
    }

    public async Task SubmitResultAsync(ManagedEndpoint endpoint, Guid commandId, CommandResultRequest result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(result);
        var request = await db.Set<RemediationRequest>().FirstOrDefaultAsync(r => r.Id == commandId && r.EndpointId == endpoint.Id, cancellationToken)
            ?? throw new NotFoundException("Command", commandId);
        request.Complete(result.Succeeded, PostureEvaluator.Sanitize(result.Message), clock.UtcNow);
        audit.Record(
            "remediation.completed", nameof(RemediationRequest), request.Id,
            $"'{request.ActionKey}' on {endpoint.Hostname} {(result.Succeeded ? "succeeded" : "failed")}. The finding stays open until the next report confirms the change.",
            new { request.ActionKey, result.Succeeded });
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ProcessEventsAsync(ManagedEndpoint endpoint, PostureReport report, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var allowed = (report.Events ?? []).Where(EventRules.IsAllowed).Take(PostureReport.MaxEvents).ToList();
        if (allowed.Count == 0)
        {
            return;
        }

        var recordIds = allowed.Select(e => e.RecordId).Distinct().ToList();
        var existing = (await db.Set<SecurityEvent>().AsNoTracking()
                .Where(e => e.EndpointId == endpoint.Id && recordIds.Contains(e.RecordId))
                .Select(e => new { e.Log, e.RecordId })
                .ToListAsync(cancellationToken))
            .Select(e => (e.Log, e.RecordId)).ToHashSet();

        foreach (var e in allowed.Where(e => !existing.Contains((e.Log, e.RecordId))).DistinctBy(e => (e.Log, e.RecordId)))
        {
            db.Set<SecurityEvent>().Add(new SecurityEvent(
                endpoint.TenantId, endpoint.Id, e.Log, e.RecordId, e.EventId, PostureEvaluator.Sanitize(e.Provider), e.TimeCreatedUtc,
                PostureEvaluator.Sanitize(e.Summary), PostureEvaluator.Sanitize(e.TargetGroup)));
        }

        var effective = await policyContext.GetAsync(cancellationToken);
        var candidates = EventRules.Evaluate(allowed, effective.Settings);
        if (candidates.Count == 0)
        {
            return;
        }

        var keys = candidates.Select(c => $"{endpoint.Id:N}:{c.DedupeKey}").ToList();
        var known = (await db.Set<SecurityAlert>().AsNoTracking().Where(a => keys.Contains(a.DedupeKey)).Select(a => a.DedupeKey).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var key = $"{endpoint.Id:N}:{candidate.DedupeKey}";
            if (known.Add(key))
            {
                db.Set<SecurityAlert>().Add(new SecurityAlert(
                    endpoint.TenantId, endpoint.Id, candidate.RuleKey, key, candidate.Severity, candidate.Title, candidate.Evidence, null, now));
            }
        }
    }

    private async Task PruneAsync(Guid endpointId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stale = await db.Set<PostureSnapshot>()
            .Where(s => s.EndpointId == endpointId)
            .OrderByDescending(s => s.ReceivedAtUtc)
            .Skip(MaxSnapshotsPerEndpoint)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);
        if (stale.Count > 0)
        {
            await db.Set<PostureSnapshot>().Where(s => stale.Contains(s.Id)).ExecuteDeleteAsync(cancellationToken);
        }

        var cutoff = now - EventRetention;
        await db.Set<SecurityEvent>().Where(e => e.EndpointId == endpointId && e.TimeCreatedUtc < cutoff).ExecuteDeleteAsync(cancellationToken);
    }
}
