namespace Solvix.Modules.Security.Application.Remediation;

using FluentValidation;
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
using ValidationException = Solvix.SharedKernel.Exceptions.ValidationException;

public interface IRemediationService
{
    Task<PagedResult<RemediationDto>> ListAsync(RemediationListQuery query, CancellationToken cancellationToken);

    Task<RemediationDto> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<RemediationDto> CreateAsync(CreateRemediationRequest request, CancellationToken cancellationToken);

    Task<RemediationDto> ApproveAsync(Guid id, NoteRequest request, CancellationToken cancellationToken);

    Task<RemediationDto> RejectAsync(Guid id, NoteRequest request, CancellationToken cancellationToken);

    Task<RemediationDto> CancelAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>
/// Remediation is the only way Solvix changes a device, and it is three independent gates: the active policy must permit
/// the action, a second person must approve it (or the policy auto-approves a Low-risk action), and the agent must
/// still have remediation switched on locally. Detection never triggers any of this by itself.
/// </summary>
internal sealed class RemediationService(
    ISolvixDbContext db,
    ITenantContext tenant,
    ICurrentUser user,
    IClock clock,
    IPolicyContext policyContext,
    IAuditTrail audit) : IRemediationService
{
    public async Task<PagedResult<RemediationDto>> ListAsync(RemediationListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var requests = db.Set<RemediationRequest>().AsNoTracking().AsQueryable();
        if (query.Status is { } status)
        {
            requests = requests.Where(r => r.Status == status);
        }
        else if (query.PendingOnly)
        {
            requests = requests.Where(r => r.Status == RemediationStatus.PendingApproval);
        }

        if (query.EndpointId is { } endpointId)
        {
            requests = requests.Where(r => r.EndpointId == endpointId);
        }

        var total = await requests.LongCountAsync(cancellationToken);
        var rows = await requests.OrderByDescending(r => r.RequestedAtUtc).Skip(page.Skip).Take(page.SafePageSize).ToListAsync(cancellationToken);
        var hostnames = await HostnamesAsync(rows.Select(r => r.EndpointId), cancellationToken);
        return new PagedResult<RemediationDto>(rows.Select(r => ToDto(r, hostnames)).ToList(), page.SafePage, page.SafePageSize, total);
    }

    public async Task<RemediationDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var request = await db.Set<RemediationRequest>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new NotFoundException("Remediation request", id);
        return ToDto(request, await HostnamesAsync([request.EndpointId], cancellationToken));
    }

    public async Task<RemediationDto> CreateAsync(CreateRemediationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var effective = await policyContext.GetAsync(cancellationToken);
        if (effective.Settings.EnforcementMode == EnforcementMode.DetectOnly)
        {
            throw new ConflictException("The active policy is detect-only: Solvix reports problems but will not change devices. An owner must approve a policy that allows remediation first.");
        }

        if (!RemediationCatalog.TryGet(request.ActionKey, out var action))
        {
            throw new ValidationException("actionKey", $"'{request.ActionKey}' is not a known remediation action.");
        }

        if (!effective.Settings.AllowedRemediationActions.Contains(action.Key, StringComparer.Ordinal))
        {
            throw new ConflictException($"The active policy does not allow '{action.Title}'. Add it to a new policy version and have it approved.");
        }

        var endpoint = await db.Set<ManagedEndpoint>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == request.EndpointId, cancellationToken)
            ?? throw new NotFoundException("Endpoint", request.EndpointId);
        if (endpoint.Status != EndpointStatus.Active)
        {
            throw new ConflictException("The device is revoked and cannot receive remediation.");
        }

        if (request.FindingId is { } findingId && !await db.Set<SecurityFinding>().AnyAsync(f => f.Id == findingId && f.EndpointId == endpoint.Id, cancellationToken))
        {
            throw new NotFoundException("Finding", findingId);
        }

        var duplicate = await db.Set<RemediationRequest>().AnyAsync(
            r => r.EndpointId == endpoint.Id && r.ActionKey == action.Key && r.Parameter == request.Parameter
                && (r.Status == RemediationStatus.PendingApproval || r.Status == RemediationStatus.Approved || r.Status == RemediationStatus.Dispatched),
            cancellationToken);
        if (duplicate)
        {
            throw new ConflictException("An identical request for this device is already pending or in progress.");
        }

        var created = RemediationRequest.ByUser(
            tenant.RequireTenantId(), endpoint.Id, request.FindingId, action, request.Parameter, request.Reason, user.RequireUserId(), clock.UtcNow);
        db.Set<RemediationRequest>().Add(created);
        audit.Record(
            "remediation.requested", nameof(RemediationRequest), created.Id,
            $"Requested '{action.Title}' on {endpoint.Hostname}. It needs approval by a different person before anything changes.",
            new { endpoint = endpoint.Id, action = action.Key, parameter = request.Parameter, risk = action.Risk.ToString(), request.Reason, finding = request.FindingId });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(created, new Dictionary<Guid, string> { [endpoint.Id] = endpoint.Hostname });
    }

    public async Task<RemediationDto> ApproveAsync(Guid id, NoteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var item = await FindAsync(id, cancellationToken);
        var effective = await policyContext.GetAsync(cancellationToken);

        if (item.Risk == RemediationRisk.High && user.Role != TenantRole.Owner)
        {
            throw new ForbiddenException("High-risk remediation can only be approved by a workspace owner.");
        }

        if (effective.Settings.EnforcementMode == EnforcementMode.DetectOnly
            || !effective.Settings.AllowedRemediationActions.Contains(item.ActionKey, StringComparer.Ordinal))
        {
            throw new ConflictException("The active policy no longer permits this action, so it cannot be approved.");
        }

        item.Approve(user.RequireUserId(), request.Note, clock.UtcNow);
        var hostnames = await HostnamesAsync([item.EndpointId], cancellationToken);
        audit.Record(
            "remediation.approved", nameof(RemediationRequest), item.Id,
            $"Approved '{ActionTitle(item)}' on {hostnames.GetValueOrDefault(item.EndpointId, "?")}. The agent will receive it at its next check-in.",
            new { item.ActionKey, item.Parameter, risk = item.Risk.ToString(), request.Note });
        await SaveAsync(cancellationToken);
        return ToDto(item, hostnames);
    }

    public async Task<RemediationDto> RejectAsync(Guid id, NoteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var item = await FindAsync(id, cancellationToken);
        item.Reject(user.RequireUserId(), request.Note ?? string.Empty, clock.UtcNow);
        var hostnames = await HostnamesAsync([item.EndpointId], cancellationToken);
        audit.Record("remediation.rejected", nameof(RemediationRequest), item.Id, $"Rejected '{ActionTitle(item)}' on {hostnames.GetValueOrDefault(item.EndpointId, "?")}.", new { item.ActionKey, request.Note });
        await SaveAsync(cancellationToken);
        return ToDto(item, hostnames);
    }

    public async Task<RemediationDto> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await FindAsync(id, cancellationToken);
        item.Cancel(user.RequireUserId(), clock.UtcNow);
        var hostnames = await HostnamesAsync([item.EndpointId], cancellationToken);
        audit.Record("remediation.cancelled", nameof(RemediationRequest), item.Id, $"Cancelled '{ActionTitle(item)}' on {hostnames.GetValueOrDefault(item.EndpointId, "?")}.", new { item.ActionKey });
        await SaveAsync(cancellationToken);
        return ToDto(item, hostnames);
    }

    private async Task<RemediationRequest> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await db.Set<RemediationRequest>().FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new NotFoundException("Remediation request", id);
        if (item.ExpireIfDue(clock.UtcNow))
        {
            audit.Record("remediation.expired", nameof(RemediationRequest), item.Id, $"Remediation '{item.ActionKey}' expired.");
            await db.SaveChangesAsync(cancellationToken);
        }

        return item;
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The request was changed by someone else at the same time. Reload and check its current state.");
        }
    }

    private async Task<Dictionary<Guid, string>> HostnamesAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var set = ids.Distinct().ToList();
        return await db.Set<ManagedEndpoint>().AsNoTracking().Where(e => set.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Hostname, cancellationToken);
    }

    private static string ActionTitle(RemediationRequest r) => RemediationCatalog.TryGet(r.ActionKey, out var a) ? a.Title : r.ActionKey;

    private static RemediationDto ToDto(RemediationRequest r, IReadOnlyDictionary<Guid, string> hostnames)
    {
        RemediationCatalog.TryGet(r.ActionKey, out var action);
        return new RemediationDto(
            r.Id, r.EndpointId, hostnames.GetValueOrDefault(r.EndpointId, string.Empty), r.FindingId, r.ActionKey, action?.Title ?? r.ActionKey,
            r.Parameter, r.Risk, action?.Impact ?? string.Empty, r.Status, r.Reason, r.RequestedByUserId, r.RequestedByUserId is null,
            r.RequestedAtUtc, r.ExpiresAtUtc, r.DecidedByUserId, r.DecidedAtUtc, r.DecisionNote, r.AutoApproved, r.DispatchedAtUtc,
            r.CompletedAtUtc, r.ResultMessage,
            r.Risk == RemediationRisk.High ? "Owner (not the requester)" : "Owner or administrator (not the requester)");
    }
}

public sealed class CreateRemediationRequestValidator : AbstractValidator<CreateRemediationRequest>
{
    public CreateRemediationRequestValidator()
    {
        RuleFor(x => x.EndpointId).NotEmpty();
        RuleFor(x => x.ActionKey).NotEmpty().MaximumLength(64);
        RuleFor(x => x.Parameter).MaximumLength(32);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}
