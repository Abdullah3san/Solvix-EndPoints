namespace Solvix.Modules.Assets.Application;

using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvix.Modules.Assets.Configuration;
using Solvix.Modules.Assets.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Endpoints;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Tenancy;

public interface IAssetService
{
    Task<PagedResult<AssetSummaryDto>> ListAsync(AssetListQuery query, CancellationToken cancellationToken);

    Task<AssetStatsDto> GetStatsAsync(CancellationToken cancellationToken);

    Task<string> ExportCsvAsync(AssetListQuery query, CancellationToken cancellationToken);

    Task<AssetDetailsDto> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<AssetLookupDto> LookupAsync(string code, CancellationToken cancellationToken);

    Task<AssetDetailsDto> CreateAsync(CreateAssetRequest request, CancellationToken cancellationToken);

    Task<AssetDetailsDto> UpdateAsync(Guid id, UpdateAssetRequest request, CancellationToken cancellationToken);

    Task<AssetDetailsDto> AssignAsync(Guid id, AssignAssetRequest request, CancellationToken cancellationToken);

    Task<AssetDetailsDto> ReturnAsync(Guid id, ReturnAssetRequest request, CancellationToken cancellationToken);

    Task<AssetDetailsDto> TransferAsync(Guid id, TransferAssetRequest request, CancellationToken cancellationToken);

    Task<AssetDetailsDto> RetireAsync(Guid id, RetireAssetRequest request, CancellationToken cancellationToken);

    Task<AssetDetailsDto> ReinstateAsync(Guid id, CancellationToken cancellationToken);

    Task<AssetDetailsDto> DisposeAsync(Guid id, DisposeAssetRequest request, CancellationToken cancellationToken);

    Task<AssetDetailsDto> MarkMissingAsync(Guid id, CancellationToken cancellationToken);

    Task<AssetDetailsDto> MarkFoundAsync(Guid id, CancellationToken cancellationToken);

    Task<BulkAssetResultDto> BulkAsync(BulkAssetRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<AssignmentDto>> GetAssignmentsAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<TransferDto>> GetTransfersAsync(Guid id, CancellationToken cancellationToken);

    Task<PagedResult<AuditLogEntryDto>> GetAuditLogAsync(AuditLogQuery query, CancellationToken cancellationToken);

    Task<DepreciationResult> GetDepreciationAsync(Guid id, DateOnly? asOf, CancellationToken cancellationToken);

    Task<IReadOnlyList<MyAssetDto>> GetMineAsync(CancellationToken cancellationToken);

    Task<Asset> FindAsync(Guid id, bool tracking, CancellationToken cancellationToken);

    string QrPayload(Asset asset);
}

internal sealed class AssetService(
    ISolvixDbContext db,
    ITenantContext tenant,
    ICurrentUser user,
    AssetReferences references,
    AssetNumbering numbering,
    AssetAuditTrail audit,
    IAssetSettingsService settings,
    IAssetMatchingService matching,
    IOptions<AssetsOptions> options,
    IClock clock) : IAssetService
{
    public const int MaxBulkSize = 500;
    public const int MaxExportRows = 50_000;
    public const string QrScheme = "SOLVIX-ASSET:";

    public async Task<PagedResult<AssetSummaryDto>> ListAsync(AssetListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var assets = await FilterAsync(query, cancellationToken);
        var total = await assets.LongCountAsync(cancellationToken);
        var items = await Sort(assets, query).Skip(page.Skip).Take(page.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<AssetSummaryDto>(await ToSummariesAsync(items, cancellationToken), page.SafePage, page.SafePageSize, total);
    }

    public async Task<AssetStatsDto> GetStatsAsync(CancellationToken cancellationToken)
    {
        var counts = await db.Set<Asset>().AsNoTracking()
            .GroupBy(a => a.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);
        var config = await settings.GetAsync(cancellationToken);
        var today = Today();
        var soon = today.AddDays(config.WarrantyAlertDays);
        var expiring = await db.Set<Asset>().CountAsync(
            a => a.Status != AssetStatus.Disposed && a.Status != AssetStatus.Retired && a.WarrantyEndDate >= today && a.WarrantyEndDate <= soon,
            cancellationToken);
        var pending = await db.Set<AssetDeviceLink>().CountAsync(l => l.Status == AssetDeviceLinkStatus.Suggested, cancellationToken);
        return new AssetStatsDto(
            counts.Values.Sum(),
            counts.GetValueOrDefault(AssetStatus.InStock),
            counts.GetValueOrDefault(AssetStatus.InUse),
            counts.GetValueOrDefault(AssetStatus.InMaintenance),
            counts.GetValueOrDefault(AssetStatus.Missing),
            counts.GetValueOrDefault(AssetStatus.Retired),
            counts.GetValueOrDefault(AssetStatus.Disposed),
            expiring,
            pending);
    }

    public async Task<string> ExportCsvAsync(AssetListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var assets = await Sort(await FilterAsync(query, cancellationToken), query).Take(MaxExportRows).ToListAsync(cancellationToken);
        var summaries = await ToSummariesAsync(assets, cancellationToken);
        var byId = assets.ToDictionary(a => a.Id);
        var today = Today();
        var csv = new StringBuilder();
        Csv.Row(
            csv,
            "Asset tag", "Name", "Category", "Status", "Condition", "Manufacturer", "Model", "Serial number", "Branch", "Department", "Location",
            "Assigned to", "Employee number", "Vendor", "Purchase date", "Purchase cost", "Currency", "PO number", "Invoice number", "Warranty end",
            "Warranty", "Book value", "Endpoint link", "Last audited (UTC)", "Created (UTC)");
        foreach (var s in summaries)
        {
            var asset = byId[s.Id];
            var book = DepreciationCalculator.Calculate(asset, today)?.BookValue ?? asset.PurchaseCost;
            Csv.Row(
                csv,
                s.AssetTag,
                s.Name,
                AssetCategories.Get(s.Category).DisplayName,
                s.Status.ToString(),
                s.Condition.ToString(),
                s.Manufacturer,
                s.Model,
                s.SerialNumber,
                s.Branch?.Name,
                s.Department?.Name,
                s.Location,
                s.Assignee?.DisplayName,
                s.Assignee?.EmployeeNumber,
                s.Vendor?.Name,
                s.PurchaseDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                s.PurchaseCost?.ToString("0.00", CultureInfo.InvariantCulture),
                s.Currency,
                asset.PurchaseOrderNumber,
                asset.InvoiceNumber,
                s.WarrantyEndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                s.Warranty.ToString(),
                book?.ToString("0.00", CultureInfo.InvariantCulture),
                s.EndpointLink.ToString(),
                s.LastAuditedAtUtc?.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                s.CreatedAtUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        }

        return csv.ToString();
    }

    public async Task<AssetDetailsDto> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await ToDetailsAsync(await FindAsync(id, tracking: false, cancellationToken), cancellationToken);

    public async Task<AssetLookupDto> LookupAsync(string code, CancellationToken cancellationToken)
    {
        var raw = code?.Trim() ?? string.Empty;
        if (raw.Length is 0 or > 512)
        {
            throw new ValidationException("code", "Scan or enter an asset tag, QR code or serial number.");
        }

        Asset? asset = null;
        var matchedBy = "assetTag";
        if (raw.StartsWith(QrScheme, StringComparison.OrdinalIgnoreCase))
        {
            raw = raw[QrScheme.Length..];
        }
        else if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var index = Array.FindLastIndex(segments, s => s.Equals("assets", StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && index + 1 < segments.Length && Guid.TryParse(segments[index + 1], out var assetId))
            {
                asset = await db.Set<Asset>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == assetId, cancellationToken);
                matchedBy = "qrCode";
            }
        }

        if (asset is null && raw.Length <= Asset.TagMaxLength)
        {
            var tag = raw.ToUpperInvariant();
            asset = await db.Set<Asset>().AsNoTracking().FirstOrDefaultAsync(a => a.AssetTag == tag, cancellationToken);
        }

        if (asset is null && HardwareIdentity.NormalizeSerial(raw) is { } serial)
        {
            var bySerial = await db.Set<Asset>().AsNoTracking().Where(a => a.SerialNumberKey == serial).Take(2).ToListAsync(cancellationToken);
            if (bySerial.Count > 1)
            {
                throw new ConflictException("Several assets share this serial number; scan the asset tag instead.", "asset_lookup_ambiguous");
            }

            asset = bySerial.SingleOrDefault();
            matchedBy = "serialNumber";
        }

        return asset is null
            ? throw new NotFoundException("No asset matches the scanned code.")
            : new AssetLookupDto(asset.Id, asset.AssetTag, asset.Name, asset.Status, matchedBy);
    }

    public async Task<AssetDetailsDto> CreateAsync(CreateAssetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Asset);
        var input = request.Asset;
        var config = await settings.GetAsync(cancellationToken);
        var details = ToDetails(input, input.Currency ?? config.DefaultCurrency);
        var (branchId, departmentId) = await references.ValidateOrganizationAsync(input.BranchId, input.DepartmentId, cancellationToken);
        await references.ValidateVendorAsync(input.VendorId, cancellationToken);
        var assignee = request.AssignTo is null ? null : await references.ValidateAssigneeAsync(request.AssignTo, cancellationToken);

        string tag;
        if (string.IsNullOrWhiteSpace(input.AssetTag))
        {
            tag = await numbering.NextTagAsync(input.Category, config.TagPrefix, cancellationToken);
        }
        else
        {
            tag = Asset.NormalizeTag(input.AssetTag);
            if (await db.Set<Asset>().AnyAsync(a => a.AssetTag == tag, cancellationToken))
            {
                throw new ConflictException($"Another asset already uses the tag {tag}.", "asset_tag_duplicate");
            }
        }

        var asset = Asset.Register(tenant.RequireTenantId(), tag, details, branchId, departmentId);
        await EnsureSerialIsUniqueAsync(asset, cancellationToken);
        db.Set<Asset>().Add(asset);
        audit.Asset(asset, "asset.registered", $"Registered {asset.AssetTag} ({asset.Name}).");
        if (assignee is not null)
        {
            Assign(asset, assignee, request.AssignTo!);
        }

        await AssetPersistence.SaveAsync(db, cancellationToken);
        await matching.EvaluateAssetAsync(asset.Id, cancellationToken);
        return await GetAsync(asset.Id, cancellationToken);
    }

    public async Task<AssetDetailsDto> UpdateAsync(Guid id, UpdateAssetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Asset);
        var asset = await FindAsync(id, tracking: true, cancellationToken);
        if (request.Version is { } version && version != asset.Version)
        {
            throw new ConflictException("The asset was changed by someone else. Reload it and try again.", "concurrency_conflict");
        }

        var input = request.Asset;
        await references.ValidateVendorAsync(input.VendorId, cancellationToken);
        var before = asset.Snapshot();
        var identityBefore = (asset.SerialNumberKey, asset.ManufacturerKey, asset.Model, asset.Category);

        // Branch, department and location only change through transfers (which keep history).
        asset.Update(ToDetails(input, input.Currency) with { Location = asset.Location });
        if (!string.IsNullOrWhiteSpace(input.AssetTag))
        {
            var tag = Asset.NormalizeTag(input.AssetTag);
            if (tag != asset.AssetTag)
            {
                throw new ValidationException("assetTag", "The asset tag cannot be changed after registration.");
            }
        }

        await EnsureSerialIsUniqueAsync(asset, cancellationToken);
        var changes = AssetAuditTrail.Diff(before, asset.Snapshot());
        if (changes.Count > 0)
        {
            audit.Asset(asset, "asset.updated", $"Updated {string.Join(", ", changes.Keys)}.", changes);
        }

        await AssetPersistence.SaveAsync(db, cancellationToken);
        if (identityBefore != (asset.SerialNumberKey, asset.ManufacturerKey, asset.Model, asset.Category))
        {
            await matching.EvaluateAssetAsync(asset.Id, cancellationToken);
        }

        return await GetAsync(id, cancellationToken);
    }

    public async Task<AssetDetailsDto> AssignAsync(Guid id, AssignAssetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var asset = await FindAsync(id, tracking: true, cancellationToken);
        var assignee = await references.ValidateAssigneeAsync(request, cancellationToken);
        await CloseOpenAssignmentAsync(asset, "Re-assigned.", null, cancellationToken);
        Assign(asset, assignee, request);
        await AssetPersistence.SaveAsync(db, cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<AssetDetailsDto> ReturnAsync(Guid id, ReturnAssetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var asset = await FindAsync(id, tracking: true, cancellationToken);
        Return(asset, await LoadOpenAssignmentAsync(asset.Id, cancellationToken), request.Condition, request.Notes);
        await AssetPersistence.SaveAsync(db, cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<AssetDetailsDto> TransferAsync(Guid id, TransferAssetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var asset = await FindAsync(id, tracking: true, cancellationToken);
        var (branchId, departmentId) = await references.ValidateOrganizationAsync(request.BranchId, request.DepartmentId, cancellationToken);
        Transfer(asset, branchId, departmentId, request.Location, request.Reason);
        await AssetPersistence.SaveAsync(db, cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<AssetDetailsDto> RetireAsync(Guid id, RetireAssetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var asset = await FindAsync(id, tracking: true, cancellationToken);
        await RetireAsync(asset, request.Reason, cancellationToken);
        await AssetPersistence.SaveAsync(db, cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<AssetDetailsDto> ReinstateAsync(Guid id, CancellationToken cancellationToken)
    {
        var asset = await FindAsync(id, tracking: true, cancellationToken);
        asset.Reinstate();
        audit.Asset(asset, "asset.reinstated", "Returned to stock from retirement.");
        await AssetPersistence.SaveAsync(db, cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<AssetDetailsDto> DisposeAsync(Guid id, DisposeAssetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var asset = await FindAsync(id, tracking: true, cancellationToken);
        var now = clock.UtcNow;
        var disposedAt = request.DisposedAtUtc ?? now;
        if (disposedAt > now.AddDays(1))
        {
            throw new ValidationException("disposedAtUtc", "The disposal date cannot be in the future.");
        }

        asset.RecordDisposal(request.Method, disposedAt, request.Value, request.Reference, request.Notes);
        audit.Asset(
            asset,
            "asset.disposed",
            $"Disposed of ({request.Method}).",
            new Dictionary<string, AuditChangeDto>
            {
                ["status"] = new(nameof(AssetStatus.Retired), nameof(AssetStatus.Disposed)),
                ["disposalMethod"] = new(null, request.Method.ToString()),
                ["disposalValue"] = new(null, asset.DisposalValue?.ToString(CultureInfo.InvariantCulture)),
            });
        await AssetPersistence.SaveAsync(db, cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<AssetDetailsDto> MarkMissingAsync(Guid id, CancellationToken cancellationToken)
    {
        var asset = await FindAsync(id, tracking: true, cancellationToken);
        MarkMissing(asset, "Marked missing.");
        await AssetPersistence.SaveAsync(db, cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<AssetDetailsDto> MarkFoundAsync(Guid id, CancellationToken cancellationToken)
    {
        var asset = await FindAsync(id, tracking: true, cancellationToken);
        var before = asset.Status;
        asset.MarkFound(clock.UtcNow);
        audit.Asset(asset, "asset.found", "Found again.", new Dictionary<string, AuditChangeDto> { ["status"] = new(before.ToString(), asset.Status.ToString()) });
        await AssetPersistence.SaveAsync(db, cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    /// <summary>All-or-nothing: the first asset that cannot take the action aborts the whole batch.</summary>
    public async Task<BulkAssetResultDto> BulkAsync(BulkAssetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ids = (request.AssetIds ?? []).Distinct().ToList();
        if (ids.Count == 0 || ids.Count > MaxBulkSize)
        {
            throw new ValidationException("assetIds", $"Select between 1 and {MaxBulkSize} assets.");
        }

        if (!Enum.IsDefined(request.Action))
        {
            throw new ValidationException("action", "Unknown bulk action.");
        }

        var assets = await db.Set<Asset>().Where(a => ids.Contains(a.Id)).ToListAsync(cancellationToken);
        if (assets.Count != ids.Count)
        {
            throw new NotFoundException("Asset", ids.Except(assets.Select(a => a.Id)).First());
        }

        (Guid? Branch, Guid? Department) target = default;
        if (request.Action == BulkAssetAction.Transfer)
        {
            target = await references.ValidateOrganizationAsync(request.BranchId, request.DepartmentId, cancellationToken);
        }

        var openAssignments = request.Action is BulkAssetAction.Return or BulkAssetAction.Retire
            ? await db.Set<AssetAssignment>().Where(x => ids.Contains(x.AssetId) && x.ReturnedAtUtc == null).ToDictionaryAsync(x => x.AssetId, cancellationToken)
            : [];

        foreach (var asset in assets.OrderBy(a => a.AssetTag, StringComparer.Ordinal))
        {
            try
            {
                switch (request.Action)
                {
                    case BulkAssetAction.Transfer:
                        Transfer(asset, target.Branch, target.Department, request.Location, request.Reason);
                        break;
                    case BulkAssetAction.Return:
                        Return(asset, openAssignments.GetValueOrDefault(asset.Id), request.Condition, request.Reason);
                        break;
                    case BulkAssetAction.Retire:
                        Retire(asset, openAssignments.GetValueOrDefault(asset.Id), request.Reason ?? string.Empty);
                        break;
                    case BulkAssetAction.SetCondition:
                        SetCondition(asset, request.Condition ?? throw new ValidationException("condition", "Choose a condition."));
                        break;
                    case BulkAssetAction.MarkMissing:
                        MarkMissing(asset, "Marked missing (bulk).");
                        break;
                    default:
                        throw new ValidationException("action", "Unknown bulk action.");
                }
            }
            catch (DomainException ex) when (ex is ConflictException or ValidationException)
            {
                throw new ConflictException($"{asset.AssetTag}: {DescribeError(ex)} No assets were changed.", "bulk_action_failed");
            }
        }

        await AssetPersistence.SaveAsync(db, cancellationToken);
        return new BulkAssetResultDto(request.Action, assets.Count);
    }

    public async Task<IReadOnlyList<AssignmentDto>> GetAssignmentsAsync(Guid id, CancellationToken cancellationToken)
    {
        await FindAsync(id, tracking: false, cancellationToken);
        var rows = await db.Set<AssetAssignment>().AsNoTracking()
            .Where(a => a.AssetId == id)
            .OrderByDescending(a => a.AssignedAtUtc).ThenByDescending(a => a.Id)
            .Take(500)
            .ToListAsync(cancellationToken);
        await references.PreloadAsync(
            rows.SelectMany(r => new[] { r.AssignedUserId, r.AssignedByUserId, r.ReturnedByUserId }),
            rows.Select(r => r.BranchId),
            rows.Select(r => r.DepartmentId),
            [],
            cancellationToken);
        return rows.Select(r => new AssignmentDto(
                r.Id,
                references.Assignee(r.AssignedUserId, r.AssigneeName, r.EmployeeNumber)!,
                references.Branch(r.BranchId),
                references.Department(r.DepartmentId),
                r.AssignedAtUtc,
                references.User(r.AssignedByUserId),
                r.ExpectedReturnDate,
                r.Notes,
                r.ReturnedAtUtc,
                references.User(r.ReturnedByUserId),
                r.ReturnNotes,
                r.ReturnCondition))
            .ToList();
    }

    public async Task<IReadOnlyList<TransferDto>> GetTransfersAsync(Guid id, CancellationToken cancellationToken)
    {
        await FindAsync(id, tracking: false, cancellationToken);
        var rows = await db.Set<AssetTransfer>().AsNoTracking()
            .Where(t => t.AssetId == id)
            .OrderByDescending(t => t.TransferredAtUtc).ThenByDescending(t => t.Id)
            .Take(500)
            .ToListAsync(cancellationToken);
        await references.PreloadAsync(
            rows.Select(r => r.TransferredByUserId),
            rows.SelectMany(r => new[] { r.FromBranchId, r.ToBranchId }),
            rows.SelectMany(r => new[] { r.FromDepartmentId, r.ToDepartmentId }),
            [],
            cancellationToken);
        return rows.Select(r => new TransferDto(
                r.Id,
                references.Branch(r.FromBranchId),
                references.Department(r.FromDepartmentId),
                r.FromLocation,
                references.Branch(r.ToBranchId),
                references.Department(r.ToDepartmentId),
                r.ToLocation,
                r.TransferredAtUtc,
                references.User(r.TransferredByUserId),
                r.Reason))
            .ToList();
    }

    public async Task<PagedResult<AuditLogEntryDto>> GetAuditLogAsync(AuditLogQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.AssetId is { } assetId)
        {
            await FindAsync(assetId, tracking: false, cancellationToken);
        }

        var page = new PageRequest(query.Page, query.PageSize);
        var entries = db.Set<AssetAuditLogEntry>().AsNoTracking();
        if (query.AssetId is { } a)
        {
            entries = entries.Where(e => e.AssetId == a);
        }

        if (!string.IsNullOrWhiteSpace(query.EntityType))
        {
            entries = entries.Where(e => e.EntityType == query.EntityType);
        }

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            entries = entries.Where(e => e.Action == query.Action);
        }

        var total = await entries.LongCountAsync(cancellationToken);
        var rows = await entries.OrderByDescending(e => e.OccurredAtUtc).ThenByDescending(e => e.Id)
            .Skip(page.Skip).Take(page.SafePageSize).ToListAsync(cancellationToken);
        await references.PreloadAsync(rows.Select(r => r.ActorUserId), [], [], [], cancellationToken);
        var items = rows.Select(r => new AuditLogEntryDto(
                r.Id,
                r.OccurredAtUtc,
                r.EntityType,
                r.EntityId,
                r.AssetId,
                r.Action,
                r.Summary,
                r.ChangesJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, AuditChangeDto>>(r.ChangesJson, AssetsJson.Options),
                r.ActorKind,
                references.User(r.ActorUserId),
                r.CorrelationId))
            .ToList();
        return new PagedResult<AuditLogEntryDto>(items, page.SafePage, page.SafePageSize, total);
    }

    public async Task<DepreciationResult> GetDepreciationAsync(Guid id, DateOnly? asOf, CancellationToken cancellationToken)
    {
        var asset = await FindAsync(id, tracking: false, cancellationToken);
        return DepreciationCalculator.Calculate(asset, asOf ?? Today())
            ?? throw new NotFoundException("The asset is not depreciated (no method, cost or purchase date).");
    }

    public async Task<IReadOnlyList<MyAssetDto>> GetMineAsync(CancellationToken cancellationToken)
    {
        var userId = user.RequireUserId();
        return await db.Set<Asset>().AsNoTracking()
            .Where(a => a.AssignedUserId == userId && a.Status != AssetStatus.Disposed)
            .OrderBy(a => a.AssetTag)
            .Select(a => new MyAssetDto(a.Id, a.AssetTag, a.Name, a.Category, a.Status, a.Condition, a.Manufacturer, a.Model, a.SerialNumber, a.AssignedAtUtc, a.WarrantyEndDate, a.Location))
            .ToListAsync(cancellationToken);
    }

    public async Task<Asset> FindAsync(Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var query = tracking ? db.Set<Asset>() : db.Set<Asset>().AsNoTracking();
        return await query.FirstOrDefaultAsync(a => a.Id == id, cancellationToken) ?? throw new NotFoundException("Asset", id);
    }

    public string QrPayload(Asset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var baseUrl = options.Value.LabelBaseUrl?.TrimEnd('/');
        return string.IsNullOrEmpty(baseUrl) ? QrScheme + asset.AssetTag : $"{baseUrl}/assets/{asset.Id}";
    }

    // ------------------------------------------------------------ lifecycle helpers (shared by single and bulk actions)

    internal void Assign(Asset asset, Assignee assignee, AssignAssetRequest request)
    {
        var now = clock.UtcNow;
        var before = asset.Snapshot();
        asset.Assign(assignee, now);
        db.Set<AssetAssignment>().Add(AssetAssignment.Open(asset, assignee, now, user.UserId, request.ExpectedReturnDate, request.Notes));
        audit.Asset(asset, "asset.assigned", $"Assigned to {assignee.Name ?? "member " + assignee.UserId}.", AssetAuditTrail.Diff(before, asset.Snapshot()));
    }

    private void Return(Asset asset, AssetAssignment? open, AssetCondition? condition, string? notes)
    {
        var before = asset.Snapshot();
        asset.Return(condition);
        open?.Close(clock.UtcNow, user.UserId, notes, condition);
        audit.Asset(asset, "asset.returned", "Checked in.", AssetAuditTrail.Diff(before, asset.Snapshot()));
    }

    private void Transfer(Asset asset, Guid? branchId, Guid? departmentId, string? location, string? reason)
    {
        var (fromBranch, fromDepartment, fromLocation) = (asset.BranchId, asset.DepartmentId, asset.Location);
        var before = asset.Snapshot();
        asset.Transfer(branchId, departmentId, location);
        db.Set<AssetTransfer>().Add(AssetTransfer.Record(asset, fromBranch, fromDepartment, fromLocation, clock.UtcNow, user.UserId, reason));
        audit.Asset(asset, "asset.transferred", string.IsNullOrWhiteSpace(reason) ? "Transferred." : $"Transferred: {reason.Trim()}", AssetAuditTrail.Diff(before, asset.Snapshot()));
    }

    private async Task RetireAsync(Asset asset, string reason, CancellationToken cancellationToken) =>
        Retire(asset, asset.IsAssigned ? await LoadOpenAssignmentAsync(asset.Id, cancellationToken) : null, reason);

    private void Retire(Asset asset, AssetAssignment? open, string reason)
    {
        var before = asset.Snapshot();
        asset.Retire(reason, clock.UtcNow);
        open?.Close(clock.UtcNow, user.UserId, "Asset retired.", null);
        audit.Asset(asset, "asset.retired", $"Retired: {asset.RetirementReason}", AssetAuditTrail.Diff(before, asset.Snapshot()));
    }

    private void SetCondition(Asset asset, AssetCondition condition)
    {
        var before = asset.Snapshot();
        asset.ChangeCondition(condition);
        audit.Asset(asset, "asset.condition_changed", $"Condition set to {condition}.", AssetAuditTrail.Diff(before, asset.Snapshot()));
    }

    private void MarkMissing(Asset asset, string summary)
    {
        var before = asset.Snapshot();
        asset.MarkMissing();
        audit.Asset(asset, "asset.missing", summary, AssetAuditTrail.Diff(before, asset.Snapshot()));
    }

    private async Task CloseOpenAssignmentAsync(Asset asset, string note, AssetCondition? condition, CancellationToken cancellationToken)
    {
        if (!asset.IsAssigned)
        {
            return;
        }

        (await LoadOpenAssignmentAsync(asset.Id, cancellationToken))?.Close(clock.UtcNow, user.UserId, note, condition);
    }

    private Task<AssetAssignment?> LoadOpenAssignmentAsync(Guid assetId, CancellationToken cancellationToken) =>
        db.Set<AssetAssignment>().FirstOrDefaultAsync(a => a.AssetId == assetId && a.ReturnedAtUtc == null, cancellationToken);

    private async Task EnsureSerialIsUniqueAsync(Asset asset, CancellationToken cancellationToken)
    {
        if (asset.SerialNumberKey is not { } serial)
        {
            return;
        }

        var duplicate = await db.Set<Asset>().AsNoTracking()
            .Where(a => a.Id != asset.Id && a.SerialNumberKey == serial && a.ManufacturerKey == asset.ManufacturerKey)
            .Select(a => a.AssetTag)
            .FirstOrDefaultAsync(cancellationToken);
        if (duplicate is not null)
        {
            throw new ConflictException($"Asset {duplicate} from the same manufacturer already has serial number {asset.SerialNumber}.", "asset_serial_duplicate");
        }
    }

    private static string DescribeError(DomainException ex) =>
        ex is ValidationException { Errors.Count: > 0 } v ? string.Join(" ", v.Errors.SelectMany(e => e.Value)) : ex.Message;

    private static AssetDetails ToDetails(AssetInput input, string? currency) => new(
        input.Name,
        input.Category,
        input.Condition,
        input.Manufacturer,
        input.Model,
        input.SerialNumber,
        input.Description,
        input.Location,
        input.VendorId,
        input.PurchaseDate,
        input.PurchaseCost,
        input.PurchaseCost is null ? input.Currency : currency,
        input.PurchaseOrderNumber,
        input.InvoiceNumber,
        input.WarrantyStartDate,
        input.WarrantyEndDate,
        input.WarrantyProvider,
        input.WarrantyReference,
        input.DepreciationMethod,
        input.UsefulLifeMonths,
        input.SalvageValue);

    private DateOnly Today() => DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

    // ------------------------------------------------------------ query building

    private async Task<IQueryable<Asset>> FilterAsync(AssetListQuery query, CancellationToken cancellationToken)
    {
        var assets = db.Set<Asset>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            var pattern = $"%{term.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)}%";
            assets = assets.Where(a =>
                EF.Functions.ILike(a.AssetTag, pattern)
                || EF.Functions.ILike(a.Name, pattern)
                || (a.SerialNumber != null && EF.Functions.ILike(a.SerialNumber, pattern))
                || (a.Manufacturer != null && EF.Functions.ILike(a.Manufacturer, pattern))
                || (a.Model != null && EF.Functions.ILike(a.Model, pattern))
                || (a.AssigneeName != null && EF.Functions.ILike(a.AssigneeName, pattern))
                || (a.EmployeeNumber != null && EF.Functions.ILike(a.EmployeeNumber, pattern))
                || (a.Location != null && EF.Functions.ILike(a.Location, pattern))
                || (a.PurchaseOrderNumber != null && EF.Functions.ILike(a.PurchaseOrderNumber, pattern))
                || (a.InvoiceNumber != null && EF.Functions.ILike(a.InvoiceNumber, pattern)));
        }

        if (query.Category is { Count: > 0 } categories)
        {
            var list = categories.Distinct().ToList();
            assets = assets.Where(a => list.Contains(a.Category));
        }

        if (query.Status is { Count: > 0 } statuses)
        {
            var list = statuses.Distinct().ToList();
            assets = assets.Where(a => list.Contains(a.Status));
        }

        if (query.Condition is { } condition)
        {
            assets = assets.Where(a => a.Condition == condition);
        }

        if (query.BranchId is { } branch)
        {
            assets = assets.Where(a => a.BranchId == branch);
        }

        if (query.DepartmentId is { } department)
        {
            assets = assets.Where(a => a.DepartmentId == department);
        }

        if (query.VendorId is { } vendor)
        {
            assets = assets.Where(a => a.VendorId == vendor);
        }

        if (query.AssignedUserId is { } assignedUser)
        {
            assets = assets.Where(a => a.AssignedUserId == assignedUser);
        }

        if (query.Assigned is { } assigned)
        {
            assets = assigned
                ? assets.Where(a => a.AssignedUserId != null || a.AssigneeName != null)
                : assets.Where(a => a.AssignedUserId == null && a.AssigneeName == null);
        }

        if (query.PurchasedFrom is { } from)
        {
            assets = assets.Where(a => a.PurchaseDate >= from);
        }

        if (query.PurchasedTo is { } to)
        {
            assets = assets.Where(a => a.PurchaseDate <= to);
        }

        if (query.NotAuditedForDays is { } days and > 0)
        {
            var since = clock.UtcNow.AddDays(-days);
            assets = assets.Where(a => a.LastAuditedAtUtc == null || a.LastAuditedAtUtc < since);
        }

        if (query.Warranty is { } warranty)
        {
            var today = Today();
            var soon = today.AddDays((await settings.GetAsync(cancellationToken)).WarrantyAlertDays);
            assets = warranty switch
            {
                WarrantyState.None => assets.Where(a => a.WarrantyEndDate == null),
                WarrantyState.Expired => assets.Where(a => a.WarrantyEndDate < today),
                WarrantyState.ExpiringSoon => assets.Where(a => a.WarrantyEndDate >= today && a.WarrantyEndDate <= soon),
                _ => assets.Where(a => a.WarrantyEndDate > soon),
            };
        }

        if (query.EndpointLink is { } linkState)
        {
            var links = db.Set<AssetDeviceLink>();
            var manageable = AssetCategories.All.Where(c => c.EndpointManageable).Select(c => c.Category).ToList();
            assets = linkState switch
            {
                EndpointLinkState.Linked => assets.Where(a => links.Any(l => l.AssetId == a.Id && l.Status == AssetDeviceLinkStatus.Approved)),
                EndpointLinkState.Suggested => assets.Where(a =>
                    links.Any(l => l.AssetId == a.Id && l.Status == AssetDeviceLinkStatus.Suggested)
                    && !links.Any(l => l.AssetId == a.Id && l.Status == AssetDeviceLinkStatus.Approved)),
                EndpointLinkState.Unlinked => assets.Where(a =>
                    manageable.Contains(a.Category) && a.Status != AssetStatus.Disposed
                    && !links.Any(l => l.AssetId == a.Id && (l.Status == AssetDeviceLinkStatus.Approved || l.Status == AssetDeviceLinkStatus.Suggested))),
                _ => assets.Where(a => !manageable.Contains(a.Category) && !links.Any(l => l.AssetId == a.Id && l.Status == AssetDeviceLinkStatus.Approved)),
            };
        }

        return assets;
    }

    private static IQueryable<Asset> Sort(IQueryable<Asset> assets, AssetListQuery query)
    {
        var desc = query.Direction == SortDirection.Desc;
        IOrderedQueryable<Asset> ordered = query.Sort switch
        {
            AssetSortField.Name => desc ? assets.OrderByDescending(a => a.Name) : assets.OrderBy(a => a.Name),
            AssetSortField.Category => desc ? assets.OrderByDescending(a => a.Category) : assets.OrderBy(a => a.Category),
            AssetSortField.Status => desc ? assets.OrderByDescending(a => a.Status) : assets.OrderBy(a => a.Status),
            AssetSortField.Manufacturer => desc ? assets.OrderByDescending(a => a.Manufacturer) : assets.OrderBy(a => a.Manufacturer),
            AssetSortField.Model => desc ? assets.OrderByDescending(a => a.Model) : assets.OrderBy(a => a.Model),
            AssetSortField.SerialNumber => desc ? assets.OrderByDescending(a => a.SerialNumber) : assets.OrderBy(a => a.SerialNumber),
            AssetSortField.PurchaseDate => desc ? assets.OrderByDescending(a => a.PurchaseDate) : assets.OrderBy(a => a.PurchaseDate),
            AssetSortField.PurchaseCost => desc ? assets.OrderByDescending(a => a.PurchaseCost) : assets.OrderBy(a => a.PurchaseCost),
            AssetSortField.WarrantyEndDate => desc ? assets.OrderByDescending(a => a.WarrantyEndDate) : assets.OrderBy(a => a.WarrantyEndDate),
            AssetSortField.LastAuditedAt => desc ? assets.OrderByDescending(a => a.LastAuditedAtUtc) : assets.OrderBy(a => a.LastAuditedAtUtc),
            AssetSortField.CreatedAt => desc ? assets.OrderByDescending(a => a.CreatedAtUtc) : assets.OrderBy(a => a.CreatedAtUtc),
            AssetSortField.UpdatedAt => desc ? assets.OrderByDescending(a => a.UpdatedAtUtc) : assets.OrderBy(a => a.UpdatedAtUtc),
            _ => desc ? assets.OrderByDescending(a => a.AssetTag) : assets.OrderBy(a => a.AssetTag),
        };
        return ordered.ThenBy(a => a.Id);
    }

    private async Task<IReadOnlyList<AssetSummaryDto>> ToSummariesAsync(IReadOnlyList<Asset> assets, CancellationToken cancellationToken)
    {
        var ids = assets.Select(a => a.Id).ToList();
        var links = await db.Set<AssetDeviceLink>().AsNoTracking()
            .Where(l => ids.Contains(l.AssetId) && (l.Status == AssetDeviceLinkStatus.Approved || l.Status == AssetDeviceLinkStatus.Suggested))
            .Select(l => new { l.AssetId, l.DeviceId, l.Status })
            .ToListAsync(cancellationToken);
        await references.PreloadAsync(assets.Select(a => a.AssignedUserId), assets.Select(a => a.BranchId), assets.Select(a => a.DepartmentId), assets.Select(a => a.VendorId), cancellationToken);
        var alertDays = (await settings.GetAsync(cancellationToken)).WarrantyAlertDays;
        var today = Today();
        return assets.Select(a =>
            {
                var approved = links.FirstOrDefault(l => l.AssetId == a.Id && l.Status == AssetDeviceLinkStatus.Approved);
                var state = approved is not null ? EndpointLinkState.Linked
                    : links.Any(l => l.AssetId == a.Id) ? EndpointLinkState.Suggested
                    : AssetCategories.Get(a.Category).EndpointManageable && a.Status != AssetStatus.Disposed ? EndpointLinkState.Unlinked
                    : EndpointLinkState.NotApplicable;
                return new AssetSummaryDto(
                    a.Id,
                    a.AssetTag,
                    a.Name,
                    a.Category,
                    a.Status,
                    a.Condition,
                    a.Manufacturer,
                    a.Model,
                    a.SerialNumber,
                    references.Branch(a.BranchId),
                    references.Department(a.DepartmentId),
                    a.Location,
                    references.Assignee(a.AssignedUserId, a.AssigneeName, a.EmployeeNumber),
                    references.Vendor(a.VendorId),
                    a.PurchaseDate,
                    a.PurchaseCost,
                    a.Currency,
                    a.WarrantyEndDate,
                    Asset.WarrantyStateOn(a.WarrantyEndDate, today, alertDays),
                    state,
                    approved?.DeviceId,
                    a.LastAuditedAtUtc,
                    a.CreatedAtUtc,
                    a.UpdatedAtUtc);
            })
            .ToList();
    }

    private async Task<AssetDetailsDto> ToDetailsAsync(Asset asset, CancellationToken cancellationToken)
    {
        var summary = (await ToSummariesAsync([asset], cancellationToken))[0];
        var link = await matching.GetApprovedForAssetAsync(asset, cancellationToken);
        var pending = await db.Set<AssetDeviceLink>().CountAsync(l => l.AssetId == asset.Id && l.Status == AssetDeviceLinkStatus.Suggested, cancellationToken);
        var open = await db.Set<MaintenanceRecord>().AsNoTracking()
            .Where(m => m.AssetId == asset.Id && (m.Status == MaintenanceStatus.InProgress || m.Status == MaintenanceStatus.Scheduled))
            .OrderByDescending(m => m.Status == MaintenanceStatus.InProgress).ThenBy(m => m.ScheduledFor)
            .FirstOrDefaultAsync(cancellationToken);
        if (open is not null)
        {
            await references.PreloadAsync([open.CreatedByUserId], [], [], [open.VendorId], cancellationToken);
        }

        return new AssetDetailsDto
        {
            Summary = summary,
            Version = asset.Version,
            Description = asset.Description,
            PurchaseOrderNumber = asset.PurchaseOrderNumber,
            InvoiceNumber = asset.InvoiceNumber,
            WarrantyStartDate = asset.WarrantyStartDate,
            WarrantyProvider = asset.WarrantyProvider,
            WarrantyReference = asset.WarrantyReference,
            DepreciationMethod = asset.DepreciationMethod,
            UsefulLifeMonths = asset.UsefulLifeMonths,
            SalvageValue = asset.SalvageValue,
            Depreciation = DepreciationCalculator.Calculate(asset, Today()),
            AssignedAtUtc = asset.AssignedAtUtc,
            RetiredAtUtc = asset.RetiredAtUtc,
            RetirementReason = asset.RetirementReason,
            DisposedAtUtc = asset.DisposedAtUtc,
            DisposalMethod = asset.DisposalMethod,
            DisposalValue = asset.DisposalValue,
            DisposalReference = asset.DisposalReference,
            DisposalNotes = asset.DisposalNotes,
            EndpointManageable = AssetCategories.Get(asset.Category).EndpointManageable,
            EndpointLink = link,
            PendingSuggestions = pending,
            OpenMaintenance = open is null ? null : MaintenanceService.ToDto(open, asset, references),
            QrPayload = QrPayload(asset),
        };
    }
}

/// <summary>RFC 4180 CSV with spreadsheet formula-injection protection.</summary>
internal static class Csv
{
    public static void Row(StringBuilder builder, params string?[] cells)
    {
        ArgumentNullException.ThrowIfNull(builder);
        for (var i = 0; i < cells.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(Escape(cells[i]));
        }

        builder.Append("\r\n");
    }

    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Cells that a spreadsheet would evaluate as a formula are prefixed with an apostrophe.
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
    }
}
