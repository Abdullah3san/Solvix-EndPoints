namespace Solvix.Modules.Security.Application.Findings;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Security.Application.Common;
using Solvix.Modules.Security.Application.Endpoints;
using Solvix.Modules.Security.Domain;
using Solvix.Modules.Security.Evaluation;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;

public interface IFindingService
{
    Task<PagedResult<FindingDto>> ListAsync(FindingListQuery query, CancellationToken cancellationToken);

    Task<FindingDto> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<FindingDto> AcceptRiskAsync(Guid id, AcceptRiskRequest request, CancellationToken cancellationToken);

    Task<FindingDto> RevokeAcceptanceAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed class FindingService(ISolvixDbContext db, ICurrentUser user, IClock clock, IAuditTrail audit) : IFindingService
{
    public async Task<PagedResult<FindingDto>> ListAsync(FindingListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var findings = db.Set<SecurityFinding>().AsNoTracking().AsQueryable();

        findings = query.Status is { } status
            ? findings.Where(f => f.Status == status)
            : findings.Where(f => f.Status != FindingStatus.Resolved);
        if (query.MinSeverity is { } min)
        {
            // Severity is stored as text; map "at least" to the set of qualifying names.
            var allowed = Enum.GetValues<Severity>().Where(s => s >= min).ToList();
            findings = findings.Where(f => allowed.Contains(f.Severity));
        }

        if (query.EndpointId is { } endpointId)
        {
            findings = findings.Where(f => f.EndpointId == endpointId);
        }

        if (!string.IsNullOrWhiteSpace(query.RuleKey))
        {
            var key = query.RuleKey.Trim().ToUpperInvariant();
            findings = findings.Where(f => f.RuleKey == key);
        }

        if (query.Category is { } category)
        {
            var keys = RuleCatalog.All.Where(r => r.Category == category).Select(r => r.Key).ToList();
            findings = findings.Where(f => keys.Contains(f.RuleKey));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{EndpointService.EscapeLike(query.Search.Trim())}%";
            findings = findings.Where(f => EF.Functions.ILike(f.Title, pattern) || EF.Functions.ILike(f.Evidence, pattern));
        }

        var total = await findings.LongCountAsync(cancellationToken);
        var rows = await findings
            .OrderByDescending(f => f.Severity == Severity.Critical ? 4 : f.Severity == Severity.High ? 3 : f.Severity == Severity.Medium ? 2 : f.Severity == Severity.Low ? 1 : 0)
            .ThenByDescending(f => f.LastDetectedAtUtc)
            .Skip(page.Skip).Take(page.SafePageSize)
            .ToListAsync(cancellationToken);
        var hostnames = await HostnamesAsync(rows.Select(f => f.EndpointId), cancellationToken);
        return new PagedResult<FindingDto>(rows.Select(f => ToDto(f, hostnames)).ToList(), page.SafePage, page.SafePageSize, total);
    }

    public async Task<FindingDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var finding = await db.Set<SecurityFinding>().AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, cancellationToken)
            ?? throw new NotFoundException("Finding", id);
        return ToDto(finding, await HostnamesAsync([finding.EndpointId], cancellationToken));
    }

    public async Task<FindingDto> AcceptRiskAsync(Guid id, AcceptRiskRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var finding = await db.Set<SecurityFinding>().FirstOrDefaultAsync(f => f.Id == id, cancellationToken)
            ?? throw new NotFoundException("Finding", id);

        // Accepting a serious risk is a decision, not a triage step: it needs an Owner.
        if (finding.Severity >= Severity.High && user.Role != TenantRole.Owner)
        {
            throw new ForbiddenException("Only a workspace owner can accept the risk of a High or Critical finding.");
        }

        finding.AcceptRisk(user.RequireUserId(), request.Reason, request.AcceptedUntilUtc, clock.UtcNow);
        var hostnames = await HostnamesAsync([finding.EndpointId], cancellationToken);
        audit.Record(
            "finding.risk_accepted", nameof(SecurityFinding), finding.Id,
            $"Accepted the risk of '{finding.Title}' on {hostnames.GetValueOrDefault(finding.EndpointId, "?")} until {request.AcceptedUntilUtc:u}.",
            new { finding.RuleKey, finding.SubjectKey, severity = finding.Severity.ToString(), request.Reason, until = request.AcceptedUntilUtc });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(finding, hostnames);
    }

    public async Task<FindingDto> RevokeAcceptanceAsync(Guid id, CancellationToken cancellationToken)
    {
        var finding = await db.Set<SecurityFinding>().FirstOrDefaultAsync(f => f.Id == id, cancellationToken)
            ?? throw new NotFoundException("Finding", id);
        finding.RevokeAcceptance();
        audit.Record("finding.acceptance_revoked", nameof(SecurityFinding), finding.Id, $"Revoked the risk acceptance of '{finding.Title}'.", new { finding.RuleKey, finding.SubjectKey });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(finding, await HostnamesAsync([finding.EndpointId], cancellationToken));
    }

    internal static FindingDto ToDto(SecurityFinding f, IReadOnlyDictionary<Guid, string> hostnames) => new(
        f.Id, f.EndpointId, hostnames.GetValueOrDefault(f.EndpointId, string.Empty), f.RuleKey, f.SubjectKey,
        RuleCatalog.TryGet(f.RuleKey, out var rule) ? rule.Category : RuleCategory.Configuration,
        f.Status, f.Severity, f.Title, f.Evidence, f.FirstDetectedAtUtc, f.LastDetectedAtUtc, f.ResolvedAtUtc, f.RecurrenceCount,
        f.AcceptedUntilUtc, f.AcceptanceReason, f.RemediationActionKey, f.RemediationParameter, ExplanationDto.For(f.RuleKey));

    private async Task<Dictionary<Guid, string>> HostnamesAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var set = ids.Distinct().ToList();
        return await db.Set<ManagedEndpoint>().AsNoTracking().Where(e => set.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Hostname, cancellationToken);
    }
}
