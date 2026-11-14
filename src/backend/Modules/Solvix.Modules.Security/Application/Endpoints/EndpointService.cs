namespace Solvix.Modules.Security.Application.Endpoints;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Security.Application.Common;
using Solvix.Modules.Security.Domain;
using Solvix.Modules.Security.Evaluation;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Tenancy;

public interface IEndpointService
{
    Task<PagedResult<EndpointSummaryDto>> ListAsync(EndpointListQuery query, CancellationToken cancellationToken);

    Task<EndpointDetailDto> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<EndpointHistoryPointDto>> HistoryAsync(Guid id, CancellationToken cancellationToken);

    Task<PagedResult<SoftwareItemDto>> SoftwareAsync(Guid id, SoftwareListQuery query, CancellationToken cancellationToken);

    Task<EnrollmentDto> EnrollAsync(EnrollEndpointRequest request, CancellationToken cancellationToken);

    Task<EnrollmentDto> RotateKeyAsync(Guid id, CancellationToken cancellationToken);

    Task<EndpointSummaryDto> RevokeAsync(Guid id, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed class EndpointService(
    ISolvixDbContext db,
    ITenantContext tenant,
    ICurrentUser user,
    IClock clock,
    IPolicyContext policyContext,
    IAuditTrail audit) : IEndpointService
{
    private const int MaxHistory = 90;
    public const int MaxEndpointsPerTenant = 20000;

    public async Task<PagedResult<EndpointSummaryDto>> ListAsync(EndpointListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var effective = await policyContext.GetAsync(cancellationToken);
        var now = clock.UtcNow;

        var endpoints = db.Set<ManagedEndpoint>().AsNoTracking().AsQueryable();
        if (!query.IncludeRevoked)
        {
            endpoints = endpoints.Where(e => e.Status == EndpointStatus.Active);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{EscapeLike(query.Search.Trim())}%";
            endpoints = endpoints.Where(e => EF.Functions.ILike(e.Hostname, pattern));
        }

        if (query.Band is { } band)
        {
            var staleBefore = now - TimeSpan.FromDays(effective.Settings.StaleAfterDays);
            endpoints = band == ComplianceBand.Unknown
                ? endpoints.Where(e => e.LastReportAtUtc == null || e.LastReportAtUtc < staleBefore || e.Status == EndpointStatus.Revoked)
                : endpoints.Where(e => e.Band == band && e.LastReportAtUtc != null && e.LastReportAtUtc >= staleBefore && e.Status == EndpointStatus.Active);
        }

        var total = await endpoints.LongCountAsync(cancellationToken);
        var items = await endpoints
            .OrderBy(e => e.Score == null).ThenBy(e => e.Score).ThenBy(e => e.Hostname)
            .Skip(page.Skip).Take(page.SafePageSize)
            .ToListAsync(cancellationToken);
        return new PagedResult<EndpointSummaryDto>(
            items.Select(e => EndpointSummaryDto.From(e, now, effective.Settings.StaleAfterDays)).ToList(),
            page.SafePage, page.SafePageSize, total);
    }

    public async Task<EndpointDetailDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var endpoint = await db.Set<ManagedEndpoint>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, cancellationToken)
            ?? throw new NotFoundException("Endpoint", id);
        var effective = await policyContext.GetAsync(cancellationToken);
        var now = clock.UtcNow;
        var summary = EndpointSummaryDto.From(endpoint, now, effective.Settings.StaleAfterDays);

        if (endpoint.LatestSnapshotId is not { } snapshotId)
        {
            return new EndpointDetailDto(summary, null, null, ["The device has not sent a report yet."], [], [], null);
        }

        var snapshot = await db.Set<PostureSnapshot>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == snapshotId, cancellationToken);
        if (snapshot is null)
        {
            return new EndpointDetailDto(summary, null, null, [], [], [], null);
        }

        var accepted = (await db.Set<SecurityFinding>().AsNoTracking()
                .Where(f => f.EndpointId == id && f.Status == FindingStatus.Accepted && f.AcceptedUntilUtc > now)
                .Select(f => new { f.RuleKey, f.SubjectKey })
                .ToListAsync(cancellationToken))
            .Select(f => (f.RuleKey, f.SubjectKey)).ToHashSet();
        var score = ComplianceScorer.Score(snapshot.Evaluation, accepted, summary.IsStale);

        var checks = snapshot.Evaluation
            .OrderBy(c => c.Outcome == CheckOutcome.Fail ? 0 : c.Outcome == CheckOutcome.Unknown ? 1 : 2)
            .ThenByDescending(c => c.Severity)
            .ThenBy(c => c.RuleKey, StringComparer.Ordinal)
            .Select(c => new CheckResultDto(
                c.RuleKey, c.SubjectKey, RuleCatalog.TryGet(c.RuleKey, out var rule) ? rule.Category : RuleCategory.Configuration,
                c.Outcome, c.Severity, c.Title, c.Evidence, c.RemediationActionKey, c.RemediationParameter, ExplanationDto.For(c.RuleKey)))
            .ToList();

        var report = snapshot.Report;
        var facts = report is null
            ? null
            : new DeviceFactsDto(
                report.Defender, report.Firewall, report.BitLocker, report.LocalAdmins, report.Usb,
                PostureEvaluator.EffectiveUsbPolicy(report.Usb), report.Configuration, report.Software?.Count, report.Errors ?? []);

        var policyName = snapshot.PolicyId is null ? "Built-in baseline (detect only)" : (await db.Set<SecurityPolicy>().AsNoTracking()
            .Where(p => p.Id == snapshot.PolicyId).Select(p => p.Name).FirstOrDefaultAsync(cancellationToken));
        return new EndpointDetailDto(summary, policyName, snapshot.PolicyVersion, score.Reasons, score.Penalties, checks, facts);
    }

    public async Task<IReadOnlyList<EndpointHistoryPointDto>> HistoryAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await db.Set<ManagedEndpoint>().AnyAsync(e => e.Id == id, cancellationToken))
        {
            throw new NotFoundException("Endpoint", id);
        }

        var points = await db.Set<PostureSnapshot>().AsNoTracking()
            .Where(s => s.EndpointId == id)
            .OrderByDescending(s => s.ReceivedAtUtc)
            .Take(MaxHistory)
            .Select(s => new EndpointHistoryPointDto(s.ReceivedAtUtc, s.Score, s.Band))
            .ToListAsync(cancellationToken);
        points.Reverse();
        return points;
    }

    public async Task<PagedResult<SoftwareItemDto>> SoftwareAsync(Guid id, SoftwareListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var endpoint = await db.Set<ManagedEndpoint>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, cancellationToken)
            ?? throw new NotFoundException("Endpoint", id);
        var page = new PageRequest(query.Page, query.PageSize);
        var report = endpoint.LatestSnapshotId is { } sid
            ? (await db.Set<PostureSnapshot>().AsNoTracking().Where(s => s.Id == sid).Select(s => s.Report).FirstOrDefaultAsync(cancellationToken))
            : null;
        IEnumerable<InstalledSoftware> items = report?.Software ?? [];
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            items = items.Where(s => s.Name.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        var all = items.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new PagedResult<SoftwareItemDto>(
            all.Skip(page.Skip).Take(page.SafePageSize).Select(s => new SoftwareItemDto(s.Name, s.Version, s.Publisher)).ToList(),
            page.SafePage, page.SafePageSize, all.Count);
    }

    public async Task<EnrollmentDto> EnrollAsync(EnrollEndpointRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenantId = tenant.RequireTenantId();
        var userId = user.RequireUserId();
        if (await db.Set<ManagedEndpoint>().CountAsync(cancellationToken) >= MaxEndpointsPerTenant)
        {
            throw new ConflictException($"A workspace can enrol at most {MaxEndpointsPerTenant} devices.");
        }

        var secret = DeviceKey.NewSecret();
        var endpoint = ManagedEndpoint.Enroll(tenantId, request.Hostname, DeviceKey.Hash(secret), userId, clock.UtcNow);
        db.Set<ManagedEndpoint>().Add(endpoint);
        audit.Record("endpoint.enrolled", nameof(ManagedEndpoint), endpoint.Id, $"Enrolled device '{endpoint.Hostname}'. A device key was issued.", new { endpoint.Hostname });
        await db.SaveChangesAsync(cancellationToken);
        return await ToEnrollmentAsync(endpoint, DeviceKey.Compose(tenantId, endpoint.Id, secret), cancellationToken);
    }

    public async Task<EnrollmentDto> RotateKeyAsync(Guid id, CancellationToken cancellationToken)
    {
        var endpoint = await FindAsync(id, cancellationToken);
        var secret = DeviceKey.NewSecret();
        endpoint.RotateKey(DeviceKey.Hash(secret), clock.UtcNow);
        audit.Record("endpoint.key_rotated", nameof(ManagedEndpoint), endpoint.Id, $"Rotated the device key of '{endpoint.Hostname}'. The previous key stopped working.");
        await db.SaveChangesAsync(cancellationToken);
        return await ToEnrollmentAsync(endpoint, DeviceKey.Compose(endpoint.TenantId, endpoint.Id, secret), cancellationToken);
    }

    public async Task<EndpointSummaryDto> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        var endpoint = await FindAsync(id, cancellationToken);
        if (endpoint.Status == EndpointStatus.Revoked)
        {
            throw new ConflictException("The device is already revoked.");
        }

        var now = clock.UtcNow;
        endpoint.Revoke();
        var open = await db.Set<RemediationRequest>()
            .Where(r => r.EndpointId == id && (r.Status == RemediationStatus.PendingApproval || r.Status == RemediationStatus.Approved))
            .ToListAsync(cancellationToken);
        foreach (var request in open)
        {
            request.CancelBySystem("The device was revoked.", now);
        }

        audit.Record("endpoint.revoked", nameof(ManagedEndpoint), endpoint.Id, $"Revoked '{endpoint.Hostname}'. Its key no longer works and pending remediation was cancelled ({open.Count}).");
        await db.SaveChangesAsync(cancellationToken);
        var effective = await policyContext.GetAsync(cancellationToken);
        return EndpointSummaryDto.From(endpoint, now, effective.Settings.StaleAfterDays);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var endpoint = await FindAsync(id, cancellationToken);
        db.Set<ManagedEndpoint>().Remove(endpoint);
        audit.Record(
            "endpoint.deleted", nameof(ManagedEndpoint), endpoint.Id,
            $"Deleted '{endpoint.Hostname}' and its history (snapshots, findings, alerts, events, remediation). The audit trail is kept.");
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<EnrollmentDto> ToEnrollmentAsync(ManagedEndpoint endpoint, string key, CancellationToken cancellationToken)
    {
        var effective = await policyContext.GetAsync(cancellationToken);
        return new EnrollmentDto(
            EndpointSummaryDto.From(endpoint, clock.UtcNow, effective.Settings.StaleAfterDays),
            key,
            "Store this key in the agent's protected configuration now. It is shown only once and only a hash is kept; rotate the key if it is lost or exposed.");
    }

    private async Task<ManagedEndpoint> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Set<ManagedEndpoint>().FirstOrDefaultAsync(e => e.Id == id, cancellationToken)
        ?? throw new NotFoundException("Endpoint", id);

    internal static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
}
