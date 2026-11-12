namespace Solvix.Modules.Assets.Application;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Assets.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;

public interface IMaintenanceService
{
    Task<PagedResult<MaintenanceRecordDto>> ListAsync(MaintenanceListQuery query, CancellationToken cancellationToken);

    Task<MaintenanceRecordDto> CreateAsync(Guid assetId, CreateMaintenanceRequest request, CancellationToken cancellationToken);

    Task<MaintenanceRecordDto> StartAsync(Guid id, CancellationToken cancellationToken);

    Task<MaintenanceRecordDto> CompleteAsync(Guid id, CompleteMaintenanceRequest request, CancellationToken cancellationToken);

    Task<MaintenanceRecordDto> CancelAsync(Guid id, CancelMaintenanceRequest request, CancellationToken cancellationToken);
}

/// <summary>Maintenance history. Starting work puts the asset in maintenance; finishing or cancelling it restores the previous status.</summary>
internal sealed class MaintenanceService(ISolvixDbContext db, ICurrentUser user, AssetReferences references, AssetAuditTrail audit, IClock clock) : IMaintenanceService
{
    public async Task<PagedResult<MaintenanceRecordDto>> ListAsync(MaintenanceListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var records = db.Set<MaintenanceRecord>().AsNoTracking();
        if (query.AssetId is { } assetId)
        {
            if (!await db.Set<Asset>().AnyAsync(a => a.Id == assetId, cancellationToken))
            {
                throw new NotFoundException("Asset", assetId);
            }

            records = records.Where(m => m.AssetId == assetId);
        }

        if (query.Status is { } status)
        {
            records = records.Where(m => m.Status == status);
        }

        var total = await records.LongCountAsync(cancellationToken);
        var rows = await records
            .OrderByDescending(m => m.Status == MaintenanceStatus.InProgress)
            .ThenByDescending(m => m.Status == MaintenanceStatus.Scheduled)
            .ThenByDescending(m => m.CompletedAtUtc ?? m.StartedAtUtc ?? m.CreatedAtUtc)
            .ThenBy(m => m.Id)
            .Skip(page.Skip).Take(page.SafePageSize)
            .ToListAsync(cancellationToken);
        var assetIds = rows.Select(r => r.AssetId).Distinct().ToList();
        var assets = await db.Set<Asset>().AsNoTracking().Where(a => assetIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, cancellationToken);
        await references.PreloadAsync(rows.Select(r => r.CreatedByUserId), [], [], rows.Select(r => r.VendorId), cancellationToken);
        return new PagedResult<MaintenanceRecordDto>(rows.Select(r => ToDto(r, assets.GetValueOrDefault(r.AssetId), references)).ToList(), page.SafePage, page.SafePageSize, total);
    }

    public async Task<MaintenanceRecordDto> CreateAsync(Guid assetId, CreateMaintenanceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var asset = await db.Set<Asset>().FirstOrDefaultAsync(a => a.Id == assetId, cancellationToken) ?? throw new NotFoundException("Asset", assetId);
        await references.ValidateVendorAsync(request.VendorId, cancellationToken);
        var record = MaintenanceRecord.Create(asset, request.Type, request.Title, request.Description, request.VendorId, request.PerformedBy, request.ScheduledFor, user.UserId);
        db.Set<MaintenanceRecord>().Add(record);
        audit.Record(AssetAuditTrail.MaintenanceEntity, record.Id, asset.Id, "maintenance.scheduled", $"{record.Type}: {record.Title}");
        if (request.StartNow)
        {
            Start(asset, record);
        }

        await AssetPersistence.SaveAsync(db, cancellationToken);
        return await GetAsync(record.Id, cancellationToken);
    }

    public async Task<MaintenanceRecordDto> StartAsync(Guid id, CancellationToken cancellationToken)
    {
        var (record, asset) = await LoadAsync(id, cancellationToken);
        Start(asset, record);
        await AssetPersistence.SaveAsync(db, cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<MaintenanceRecordDto> CompleteAsync(Guid id, CompleteMaintenanceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (record, asset) = await LoadAsync(id, cancellationToken);
        var wasInProgress = record.Status == MaintenanceStatus.InProgress;
        record.Complete(clock.UtcNow, request.Cost, request.Resolution);
        if (wasInProgress)
        {
            asset.EndMaintenance();
        }

        audit.Record(AssetAuditTrail.MaintenanceEntity, record.Id, asset.Id, "maintenance.completed", $"{record.Title} completed{(record.Cost is { } c ? $" (cost {c:0.00})" : string.Empty)}.");
        await AssetPersistence.SaveAsync(db, cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<MaintenanceRecordDto> CancelAsync(Guid id, CancelMaintenanceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (record, asset) = await LoadAsync(id, cancellationToken);
        var wasInProgress = record.Status == MaintenanceStatus.InProgress;
        record.Cancel(clock.UtcNow, request.Reason);
        if (wasInProgress)
        {
            asset.EndMaintenance();
        }

        audit.Record(AssetAuditTrail.MaintenanceEntity, record.Id, asset.Id, "maintenance.cancelled", $"{record.Title} cancelled.");
        await AssetPersistence.SaveAsync(db, cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    internal static MaintenanceRecordDto ToDto(MaintenanceRecord r, Asset? asset, AssetReferences references) =>
        new(
            r.Id,
            r.AssetId,
            asset?.AssetTag,
            asset?.Name,
            r.Type,
            r.Status,
            r.Title,
            r.Description,
            references.Vendor(r.VendorId),
            r.PerformedBy,
            r.Cost,
            r.ScheduledFor,
            r.StartedAtUtc,
            r.CompletedAtUtc,
            r.Resolution,
            references.User(r.CreatedByUserId),
            r.CreatedAtUtc);

    private void Start(Asset asset, MaintenanceRecord record)
    {
        var before = asset.Snapshot();
        record.Start(clock.UtcNow);
        asset.StartMaintenance();
        audit.Record(AssetAuditTrail.MaintenanceEntity, record.Id, asset.Id, "maintenance.started", $"{record.Title} started.", AssetAuditTrail.Diff(before, asset.Snapshot()));
    }

    private async Task<(MaintenanceRecord Record, Asset Asset)> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        var record = await db.Set<MaintenanceRecord>().FirstOrDefaultAsync(m => m.Id == id, cancellationToken) ?? throw new NotFoundException("Maintenance record", id);
        var asset = await db.Set<Asset>().FirstAsync(a => a.Id == record.AssetId, cancellationToken);
        return (record, asset);
    }

    private async Task<MaintenanceRecordDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var record = await db.Set<MaintenanceRecord>().AsNoTracking().FirstAsync(m => m.Id == id, cancellationToken);
        var asset = await db.Set<Asset>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == record.AssetId, cancellationToken);
        await references.PreloadAsync([record.CreatedByUserId], [], [], [record.VendorId], cancellationToken);
        return ToDto(record, asset, references);
    }
}
