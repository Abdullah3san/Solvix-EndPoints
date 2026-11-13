namespace Solvix.Modules.EndpointManagement.Application.Patches;

using Microsoft.EntityFrameworkCore;
using Solvix.Endpoint.Contracts.Patching;
using Solvix.Modules.Devices.Domain;
using Solvix.Modules.EndpointManagement.Domain.Deployments;
using Solvix.Modules.EndpointManagement.Domain.Patches;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Auditing;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Tenancy;

public interface IPatchCatalogService
{
    Task<PagedResult<PatchDto>> ListAsync(PatchListQuery query, CancellationToken cancellationToken);

    Task<PatchDetailDto> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<PatchDto>> ApproveAsync(PatchDecisionRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<PatchDto>> DeclineAsync(PatchDecisionRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<PatchDto>> ResetAsync(PatchDecisionRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<AutoApprovalRuleDto>> ListRulesAsync(CancellationToken cancellationToken);

    Task<AutoApprovalRuleDto> CreateRuleAsync(SaveAutoApprovalRuleRequest request, CancellationToken cancellationToken);

    Task<AutoApprovalRuleDto> UpdateRuleAsync(Guid id, SaveAutoApprovalRuleRequest request, CancellationToken cancellationToken);

    Task DeleteRuleAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed class PatchCatalogService(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IClock clock,
    IAuditTrail audit) : IPatchCatalogService
{
    private static readonly DeploymentStatus[] ActiveDeploymentStatuses =
    [
        DeploymentStatus.Draft, DeploymentStatus.Scheduled, DeploymentStatus.InProgress,
        DeploymentStatus.AwaitingPromotion, DeploymentStatus.Halted, DeploymentStatus.Paused,
    ];

    public async Task<PagedResult<PatchDto>> ListAsync(PatchListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var patches = db.Set<Patch>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            var pattern = $"%{term}%";
            var kb = term.StartsWith("KB", StringComparison.OrdinalIgnoreCase) ? term[2..] : term;
            patches = patches.Where(p => EF.Functions.ILike(p.Title, pattern) || p.KbArticleIds.Contains(kb) || p.CveIds.Contains(term.ToUpperInvariant()));
        }

        if (query.Classification is { } c)
        {
            patches = patches.Where(p => p.Classification == c);
        }

        if (query.Severity is { } s)
        {
            patches = patches.Where(p => p.Severity == s);
        }

        if (query.ApprovalState is { } a)
        {
            patches = patches.Where(p => p.ApprovalState == a);
        }

        if (query.OnlyNeeded)
        {
            patches = patches.Where(p => db.Set<DevicePatchState>().Any(st => st.PatchId == p.Id
                && (st.Status == DevicePatchStatus.Missing || st.Status == DevicePatchStatus.Failed)));
        }

        var total = await patches.LongCountAsync(cancellationToken);
        var rows = await patches
            .OrderByDescending(p => p.Severity).ThenByDescending(p => p.ReleaseDateUtc).ThenBy(p => p.Title)
            .Skip(page.Skip).Take(page.SafePageSize)
            .ToListAsync(cancellationToken);
        return new PagedResult<PatchDto>(await ToDtosAsync(rows, cancellationToken), page.SafePage, page.SafePageSize, total);
    }

    public async Task<PatchDetailDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var patch = await db.Set<Patch>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new NotFoundException("Patch", id);
        var devices = await (
                from st in db.Set<DevicePatchState>().AsNoTracking()
                join d in db.Set<Device>().AsNoTracking() on st.DeviceId equals d.Id
                where st.PatchId == id && d.Status == DeviceStatus.Active
                orderby d.Hostname
                select new PatchDeviceStateDto(d.Id, d.Hostname, st.Status, st.IsDownloaded, st.LastDetectedAtUtc, st.InstalledAtUtc, st.FailureCount, st.LastFailureHResult, st.LastFailureAtUtc))
            .ToListAsync(cancellationToken);
        return new PatchDetailDto((await ToDtosAsync([patch], cancellationToken))[0], devices);
    }

    public Task<IReadOnlyList<PatchDto>> ApproveAsync(PatchDecisionRequest request, CancellationToken cancellationToken) =>
        DecideAsync(request, PatchApprovalState.Approved, cancellationToken);

    public Task<IReadOnlyList<PatchDto>> DeclineAsync(PatchDecisionRequest request, CancellationToken cancellationToken) =>
        DecideAsync(request, PatchApprovalState.Declined, cancellationToken);

    public Task<IReadOnlyList<PatchDto>> ResetAsync(PatchDecisionRequest request, CancellationToken cancellationToken) =>
        DecideAsync(request, PatchApprovalState.NotReviewed, cancellationToken);

    public async Task<IReadOnlyList<AutoApprovalRuleDto>> ListRulesAsync(CancellationToken cancellationToken) =>
        (await db.Set<PatchAutoApprovalRule>().AsNoTracking().OrderBy(r => r.Name).ToListAsync(cancellationToken)).Select(ToDto).ToList();

    public async Task<AutoApprovalRuleDto> CreateRuleAsync(SaveAutoApprovalRuleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureUniqueRuleNameAsync(request.Name, null, cancellationToken);
        var rule = new PatchAutoApprovalRule(
            tenantContext.RequireTenantId(), request.Name, [.. request.Classifications], [.. request.Severities ?? []], currentUser.RequireUserId());
        rule.SetEnabled(request.IsEnabled);
        db.Set<PatchAutoApprovalRule>().Add(rule);
        audit.Record("patch.auto_approval_rule_created", nameof(PatchAutoApprovalRule), rule.Id, $"Auto-approval rule '{rule.Name}' created.", request);
        await ApplyToExistingAsync(rule, request.ApplyToExisting, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(rule);
    }

    public async Task<AutoApprovalRuleDto> UpdateRuleAsync(Guid id, SaveAutoApprovalRuleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rule = await db.Set<PatchAutoApprovalRule>().FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new NotFoundException("PatchAutoApprovalRule", id);
        await EnsureUniqueRuleNameAsync(request.Name, id, cancellationToken);
        rule.Update(request.Name, [.. request.Classifications], [.. request.Severities ?? []]);
        rule.SetEnabled(request.IsEnabled);
        audit.Record("patch.auto_approval_rule_updated", nameof(PatchAutoApprovalRule), id, $"Auto-approval rule '{rule.Name}' updated.", request);
        await ApplyToExistingAsync(rule, request.ApplyToExisting, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(rule);
    }

    public async Task DeleteRuleAsync(Guid id, CancellationToken cancellationToken)
    {
        var rule = await db.Set<PatchAutoApprovalRule>().FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new NotFoundException("PatchAutoApprovalRule", id);
        db.Set<PatchAutoApprovalRule>().Remove(rule);
        audit.Record("patch.auto_approval_rule_deleted", nameof(PatchAutoApprovalRule), id, $"Auto-approval rule '{rule.Name}' deleted (existing approvals are kept).");
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<PatchDto>> DecideAsync(PatchDecisionRequest request, PatchApprovalState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ids = request.PatchIds.Distinct().ToList();
        var patches = await db.Set<Patch>().Where(p => ids.Contains(p.Id)).ToListAsync(cancellationToken);
        var missing = ids.Except(patches.Select(p => p.Id)).ToList();
        if (missing.Count > 0)
        {
            throw new NotFoundException("Patch", string.Join(", ", missing));
        }

        if (state != PatchApprovalState.Approved)
        {
            var inUse = await (
                    from dp in db.Set<PatchDeploymentPatch>()
                    join d in db.Set<PatchDeployment>() on dp.DeploymentId equals d.Id
                    where ids.Contains(dp.PatchId) && ActiveDeploymentStatuses.Contains(d.Status)
                    select d.Name)
                .Distinct()
                .ToListAsync(cancellationToken);
            if (inUse.Count > 0)
            {
                throw new ConflictException($"Patches used by active deployments cannot lose their approval: {string.Join(", ", inUse)}. Cancel or complete those deployments first.");
            }
        }

        var userId = currentUser.RequireUserId();
        var now = clock.UtcNow;
        foreach (var patch in patches)
        {
            switch (state)
            {
                case PatchApprovalState.Approved:
                    patch.Approve(userId, request.Note, now);
                    break;
                case PatchApprovalState.Declined:
                    patch.Decline(userId, request.Note, now);
                    break;
                default:
                    patch.ResetApproval(userId, request.Note, now);
                    break;
            }

            audit.Record(
                state switch { PatchApprovalState.Approved => "patch.approved", PatchApprovalState.Declined => "patch.declined", _ => "patch.approval_reset" },
                nameof(Patch),
                patch.Id,
                $"'{patch.Title}' {state}.",
                new { patch.UpdateId, patch.RevisionNumber, patch.KbArticleIds, request.Note });
        }

        await db.SaveChangesAsync(cancellationToken);
        return await ToDtosAsync(patches, cancellationToken);
    }

    private async Task ApplyToExistingAsync(PatchAutoApprovalRule rule, bool applyToExisting, CancellationToken cancellationToken)
    {
        if (!applyToExisting || !rule.IsEnabled)
        {
            return;
        }

        var now = clock.UtcNow;
        var candidates = await db.Set<Patch>().Where(p => p.ApprovalState == PatchApprovalState.NotReviewed).ToListAsync(cancellationToken);
        foreach (var patch in candidates.Where(rule.Matches))
        {
            patch.AutoApprove(rule.Id, rule.Name, now);
            audit.Record("patch.auto_approved", nameof(Patch), patch.Id, $"'{patch.Title}' auto-approved by rule '{rule.Name}'.", new { patch.UpdateId });
        }
    }

    private async Task EnsureUniqueRuleNameAsync(string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        var trimmed = name.Trim();
        if (await db.Set<PatchAutoApprovalRule>().AnyAsync(r => r.Name == trimmed && r.Id != exceptId, cancellationToken))
        {
            throw new ConflictException($"An auto-approval rule named '{trimmed}' already exists.");
        }
    }

    private async Task<IReadOnlyList<PatchDto>> ToDtosAsync(IReadOnlyList<Patch> patches, CancellationToken cancellationToken)
    {
        var ids = patches.Select(p => p.Id).ToList();
        var counts = await (
                from st in db.Set<DevicePatchState>().AsNoTracking()
                join d in db.Set<Device>().AsNoTracking() on st.DeviceId equals d.Id
                where ids.Contains(st.PatchId) && d.Status == DeviceStatus.Active
                group st by new { st.PatchId, st.Status } into g
                select new { g.Key.PatchId, g.Key.Status, Count = g.Count() })
            .ToListAsync(cancellationToken);
        int Count(Guid id, DevicePatchStatus status) => counts.FirstOrDefault(c => c.PatchId == id && c.Status == status)?.Count ?? 0;

        return patches.Select(p => new PatchDto(
            p.Id, p.UpdateId, p.RevisionNumber, p.Title, p.KbArticleIds, p.CveIds, p.Classification, p.Severity, p.ReleaseDateUtc,
            p.MaxDownloadSizeBytes, p.RebootBehavior, p.SupportUrl, p.ApprovalState, p.ApprovalDecidedAtUtc, p.ApprovalDecidedByUserId,
            p.AutoApprovalRuleId, p.ApprovalNote, p.FirstSeenAtUtc,
            Count(p.Id, DevicePatchStatus.Missing),
            Count(p.Id, DevicePatchStatus.Failed),
            Count(p.Id, DevicePatchStatus.PendingRestart),
            Count(p.Id, DevicePatchStatus.Installed))).ToList();
    }

    private static AutoApprovalRuleDto ToDto(PatchAutoApprovalRule r) =>
        new(r.Id, r.Name, r.Classifications, r.Severities, r.IsEnabled, r.CreatedAtUtc);
}
