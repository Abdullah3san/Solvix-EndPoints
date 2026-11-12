namespace Solvix.Modules.Assets.Application;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Assets.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Tenancy;

public interface IPhysicalAuditService
{
    Task<PagedResult<PhysicalAuditDto>> ListAsync(PhysicalAuditStatus? status, int page, int pageSize, CancellationToken cancellationToken);

    Task<PhysicalAuditDto> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<PhysicalAuditDto> StartAsync(StartPhysicalAuditRequest request, CancellationToken cancellationToken);

    Task<PagedResult<PhysicalAuditItemDto>> GetItemsAsync(Guid id, PhysicalAuditItemQuery query, CancellationToken cancellationToken);

    Task<ScanResultDto> ScanAsync(Guid id, ScanRequest request, CancellationToken cancellationToken);

    Task<PhysicalAuditItemDto> UpdateItemAsync(Guid id, Guid itemId, UpdateAuditItemRequest request, CancellationToken cancellationToken);

    Task<PhysicalAuditDto> CompleteAsync(Guid id, CompletePhysicalAuditRequest request, CancellationToken cancellationToken);

    Task<PhysicalAuditDto> CancelAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed class PhysicalAuditService(
    ISolvixDbContext db,
    ITenantContext tenant,
    ICurrentUser user,
    IAssetService assets,
    AssetReferences references,
    AssetAuditTrail audit,
    IClock clock) : IPhysicalAuditService
{
    public const int MaxScopeSize = 20_000;

    public async Task<PagedResult<PhysicalAuditDto>> ListAsync(PhysicalAuditStatus? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        var request = new PageRequest(page, pageSize);
        var audits = db.Set<PhysicalAudit>().AsNoTracking();
        if (status is { } s)
        {
            audits = audits.Where(a => a.Status == s);
        }

        var total = await audits.LongCountAsync(cancellationToken);
        var rows = await audits.OrderByDescending(a => a.StartedAtUtc).ThenBy(a => a.Id).Skip(request.Skip).Take(request.SafePageSize).ToListAsync(cancellationToken);
        var items = new List<PhysicalAuditDto>(rows.Count);
        foreach (var row in rows)
        {
            items.Add(await ToDtoAsync(row, cancellationToken));
        }

        return new PagedResult<PhysicalAuditDto>(items, request.SafePage, request.SafePageSize, total);
    }

    public async Task<PhysicalAuditDto> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await ToDtoAsync(await FindAsync(id, tracking: false, cancellationToken), cancellationToken);

    public async Task<PhysicalAuditDto> StartAsync(StartPhysicalAuditRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (branchId, departmentId) = await references.ValidateOrganizationAsync(request.BranchId, request.DepartmentId, cancellationToken);
        if (request.Category is { } c && !Enum.IsDefined(c))
        {
            throw new ValidationException("category", "Unknown category.");
        }

        var scope = db.Set<Asset>().AsNoTracking().Where(a => a.Status != AssetStatus.Retired && a.Status != AssetStatus.Disposed);
        if (branchId is { } b)
        {
            scope = scope.Where(a => a.BranchId == b);
        }

        if (departmentId is { } d)
        {
            scope = scope.Where(a => a.DepartmentId == d);
        }

        if (request.Category is { } category)
        {
            scope = scope.Where(a => a.Category == category);
        }

        var expected = await scope.Take(MaxScopeSize + 1).ToListAsync(cancellationToken);
        if (expected.Count > MaxScopeSize)
        {
            throw new ValidationException("branchId", $"The audit scope contains more than {MaxScopeSize} assets; narrow it by branch, department or category.");
        }

        var physicalAudit = PhysicalAudit.Start(tenant.RequireTenantId(), request.Name, branchId, departmentId, request.Category, request.Notes, clock.UtcNow, user.UserId);
        db.Set<PhysicalAudit>().Add(physicalAudit);
        db.Set<PhysicalAuditItem>().AddRange(expected.Select(a => PhysicalAuditItem.Expect(physicalAudit, a)));
        audit.Record(AssetAuditTrail.PhysicalAuditEntity, physicalAudit.Id, null, "physical_audit.started", $"Audit \"{physicalAudit.Name}\" started with {expected.Count} expected assets.");
        await AssetPersistence.SaveAsync(db, cancellationToken);
        return await GetAsync(physicalAudit.Id, cancellationToken);
    }

    public async Task<PagedResult<PhysicalAuditItemDto>> GetItemsAsync(Guid id, PhysicalAuditItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await FindAsync(id, tracking: false, cancellationToken);
        var page = new PageRequest(query.Page, query.PageSize);
        var rows = from i in db.Set<PhysicalAuditItem>().AsNoTracking()
                   join a in db.Set<Asset>().AsNoTracking() on i.AssetId equals a.Id
                   where i.PhysicalAuditId == id
                   select new { Item = i, Asset = a };
        if (query.Result is { } result)
        {
            rows = rows.Where(r => r.Item.Result == result);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{query.Search.Trim()}%";
            rows = rows.Where(r => EF.Functions.ILike(r.Asset.AssetTag, pattern) || EF.Functions.ILike(r.Asset.Name, pattern) || (r.Asset.SerialNumber != null && EF.Functions.ILike(r.Asset.SerialNumber, pattern)));
        }

        var total = await rows.LongCountAsync(cancellationToken);
        var list = await rows.OrderBy(r => r.Item.Result).ThenBy(r => r.Asset.AssetTag).Skip(page.Skip).Take(page.SafePageSize).ToListAsync(cancellationToken);
        await references.PreloadAsync([], list.Select(r => r.Item.ExpectedBranchId), [], [], cancellationToken);
        return new PagedResult<PhysicalAuditItemDto>(list.Select(r => ToItemDto(r.Item, r.Asset)).ToList(), page.SafePage, page.SafePageSize, total);
    }

    public async Task<ScanResultDto> ScanAsync(Guid id, ScanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var physicalAudit = await FindAsync(id, tracking: false, cancellationToken);
        physicalAudit.EnsureInProgress();
        var lookup = await assets.LookupAsync(request.Code, cancellationToken);
        var asset = await assets.FindAsync(lookup.Id, tracking: false, cancellationToken);

        var item = await db.Set<PhysicalAuditItem>().FirstOrDefaultAsync(i => i.PhysicalAuditId == id && i.AssetId == asset.Id, cancellationToken);
        var alreadyScanned = item is { ScannedAtUtc: not null };
        if (item is null)
        {
            item = PhysicalAuditItem.Unexpected(physicalAudit, asset);
            db.Set<PhysicalAuditItem>().Add(item);
        }

        item.Record(PhysicalAuditResult.Found, clock.UtcNow, user.UserId, request.ObservedLocation, request.Condition, request.Notes);
        await AssetPersistence.SaveAsync(db, cancellationToken);
        await references.PreloadAsync([], [item.ExpectedBranchId], [], [], cancellationToken);
        return new ScanResultDto(ToItemDto(item, asset), alreadyScanned, await GetAsync(id, cancellationToken));
    }

    public async Task<PhysicalAuditItemDto> UpdateItemAsync(Guid id, Guid itemId, UpdateAuditItemRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var physicalAudit = await FindAsync(id, tracking: false, cancellationToken);
        physicalAudit.EnsureInProgress();
        if (!Enum.IsDefined(request.Result) || request.Result == PhysicalAuditResult.Unexpected)
        {
            throw new ValidationException("result", "Choose Pending, Found or Missing.");
        }

        var item = await db.Set<PhysicalAuditItem>().FirstOrDefaultAsync(i => i.Id == itemId && i.PhysicalAuditId == id, cancellationToken)
            ?? throw new NotFoundException("Audit item", itemId);
        item.Record(request.Result, clock.UtcNow, user.UserId, null, request.Condition, request.Notes);
        await AssetPersistence.SaveAsync(db, cancellationToken);
        var asset = await assets.FindAsync(item.AssetId, tracking: false, cancellationToken);
        await references.PreloadAsync([], [item.ExpectedBranchId], [], [], cancellationToken);
        return ToItemDto(item, asset);
    }

    public async Task<PhysicalAuditDto> CompleteAsync(Guid id, CompletePhysicalAuditRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var physicalAudit = await FindAsync(id, tracking: true, cancellationToken);
        physicalAudit.EnsureInProgress();
        var now = clock.UtcNow;
        var items = await db.Set<PhysicalAuditItem>().Where(i => i.PhysicalAuditId == id).ToListAsync(cancellationToken);
        foreach (var item in items)
        {
            item.MarkMissingIfPending();
        }

        var assetIds = items.Select(i => i.AssetId).ToList();
        var tracked = await db.Set<Asset>().Where(a => assetIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, cancellationToken);
        var markedMissing = 0;
        foreach (var item in items)
        {
            if (!tracked.TryGetValue(item.AssetId, out var asset))
            {
                continue;
            }

            if (item.Result is PhysicalAuditResult.Found or PhysicalAuditResult.Unexpected)
            {
                if (asset.Status == AssetStatus.Missing)
                {
                    asset.MarkFound(item.ScannedAtUtc ?? now);
                    audit.Asset(asset, "asset.found", $"Found during audit \"{physicalAudit.Name}\".");
                }

                asset.RecordAudit(item.ScannedAtUtc ?? now, item.ObservedCondition);
            }
            else if (item.Result == PhysicalAuditResult.Missing && request.MarkMissingAssets && asset.Status is AssetStatus.InStock or AssetStatus.InUse)
            {
                asset.MarkMissing();
                markedMissing++;
                audit.Asset(asset, "asset.missing", $"Not found during audit \"{physicalAudit.Name}\".");
            }
        }

        physicalAudit.Complete(now, user.UserId, request.Notes);
        var found = items.Count(i => i.Result is PhysicalAuditResult.Found or PhysicalAuditResult.Unexpected);
        audit.Record(
            AssetAuditTrail.PhysicalAuditEntity, physicalAudit.Id, null, "physical_audit.completed",
            $"Audit \"{physicalAudit.Name}\" completed: {found} found, {items.Count(i => i.Result == PhysicalAuditResult.Missing)} missing ({markedMissing} marked missing).");
        await AssetPersistence.SaveAsync(db, cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<PhysicalAuditDto> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        var physicalAudit = await FindAsync(id, tracking: true, cancellationToken);
        physicalAudit.Cancel(clock.UtcNow, user.UserId);
        audit.Record(AssetAuditTrail.PhysicalAuditEntity, physicalAudit.Id, null, "physical_audit.cancelled", $"Audit \"{physicalAudit.Name}\" cancelled.");
        await AssetPersistence.SaveAsync(db, cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    private async Task<PhysicalAudit> FindAsync(Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var query = tracking ? db.Set<PhysicalAudit>() : db.Set<PhysicalAudit>().AsNoTracking();
        return await query.FirstOrDefaultAsync(a => a.Id == id, cancellationToken) ?? throw new NotFoundException("Audit", id);
    }

    private async Task<PhysicalAuditDto> ToDtoAsync(PhysicalAudit a, CancellationToken cancellationToken)
    {
        var counts = await db.Set<PhysicalAuditItem>().AsNoTracking()
            .Where(i => i.PhysicalAuditId == a.Id)
            .GroupBy(i => new { i.Result, i.Expected })
            .Select(g => new { g.Key.Result, g.Key.Expected, Count = g.Count() })
            .ToListAsync(cancellationToken);
        await references.PreloadAsync([a.StartedByUserId, a.CompletedByUserId], [a.BranchId], [a.DepartmentId], [], cancellationToken);
        int Count(PhysicalAuditResult r) => counts.Where(c => c.Result == r).Sum(c => c.Count);
        return new PhysicalAuditDto(
            a.Id,
            a.Name,
            references.Branch(a.BranchId),
            references.Department(a.DepartmentId),
            a.Category,
            a.Status,
            a.StartedAtUtc,
            references.User(a.StartedByUserId),
            a.CompletedAtUtc,
            references.User(a.CompletedByUserId),
            a.Notes,
            counts.Where(c => c.Expected).Sum(c => c.Count),
            Count(PhysicalAuditResult.Found),
            Count(PhysicalAuditResult.Missing),
            Count(PhysicalAuditResult.Pending),
            Count(PhysicalAuditResult.Unexpected));
    }

    private PhysicalAuditItemDto ToItemDto(PhysicalAuditItem i, Asset a) =>
        new(i.Id, a.Id, a.AssetTag, a.Name, a.Category, a.Status, i.Expected, i.Result, references.Branch(i.ExpectedBranchId), i.ExpectedLocation, i.ObservedLocation, i.ObservedCondition, i.ScannedAtUtc, i.Notes);
}
