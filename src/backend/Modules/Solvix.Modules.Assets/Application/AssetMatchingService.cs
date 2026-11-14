namespace Solvix.Modules.Assets.Application;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Solvix.Modules.Assets.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Endpoints;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Tenancy;

public interface IAssetMatchingService
{
    /// <summary>Re-evaluates every endpoint that reports the asset's serial number. Never throws for persistence races.</summary>
    Task EvaluateAssetAsync(Guid assetId, CancellationToken cancellationToken);

    /// <summary>Finds assets for an endpoint whose hardware identity became known or changed.</summary>
    Task<(int Created, int AutoApproved)> EvaluateDeviceAsync(Guid deviceId, CancellationToken cancellationToken);

    Task<AssetEndpointLinkDto?> GetApprovedForAssetAsync(Asset asset, CancellationToken cancellationToken);

    Task<PagedResult<AssetDeviceLinkDto>> ListAsync(LinkQuery query, CancellationToken cancellationToken);

    Task<AssetDeviceLinkDto> ApproveAsync(Guid linkId, LinkDecisionRequest request, CancellationToken cancellationToken);

    Task<AssetDeviceLinkDto> RejectAsync(Guid linkId, LinkDecisionRequest request, CancellationToken cancellationToken);

    Task<AssetDeviceLinkDto> LinkManuallyAsync(Guid assetId, ManualLinkRequest request, CancellationToken cancellationToken);

    Task UnlinkAsync(Guid assetId, LinkDecisionRequest request, CancellationToken cancellationToken);

    Task<DeviceAssetDto> GetForDeviceAsync(Guid deviceId, CancellationToken cancellationToken);

    Task<PagedResult<EndpointDto>> GetUnregisteredEndpointsAsync(string? search, int page, int pageSize, CancellationToken cancellationToken);

    Task<RescanResultDto> RescanAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Relates physical assets to managed endpoints. Separate records are kept on
/// both sides; this service only maintains <see cref="AssetDeviceLink"/> rows:
///  * an endpoint and an asset become a Suggested pair when their normalised
///    serial numbers are equal (the matcher scores manufacturer and model);
///  * nothing is linked without an administrator, unless the tenant enabled
///    auto-approval AND the pair is High confidence AND unambiguous on both
///    sides (one asset, one endpoint, neither already linked);
///  * rejected, superseded and unlinked pairs are remembered and not re-suggested.
/// </summary>
internal sealed partial class AssetMatchingService(
    ISolvixDbContext db,
    ITenantContext tenant,
    ICurrentUser user,
    IEndpointDirectory endpoints,
    IAssetSettingsService settings,
    AssetReferences references,
    AssetAuditTrail audit,
    IClock clock,
    ILogger<AssetMatchingService> logger) : IAssetMatchingService
{
    private const int RescanBatch = 1000;

    public async Task EvaluateAssetAsync(Guid assetId, CancellationToken cancellationToken)
    {
        try
        {
            var asset = await db.Set<Asset>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == assetId, cancellationToken);
            if (asset is null)
            {
                return;
            }

            // Suggestions for this asset that no longer share its serial number are withdrawn.
            var suggestions = await db.Set<AssetDeviceLink>().Where(l => l.AssetId == assetId && l.Status == AssetDeviceLinkStatus.Suggested).ToListAsync(cancellationToken);
            var devices = asset.SerialNumberKey is { } key && asset.Status != AssetStatus.Disposed
                ? await endpoints.FindBySerialKeysAsync([key], cancellationToken)
                : [];
            foreach (var stale in suggestions.Where(l => devices.All(d => d.Id != l.DeviceId)))
            {
                Withdraw(stale, asset.Id);
            }

            await AssetPersistence.SaveAsync(db, cancellationToken);
            foreach (var device in devices)
            {
                await EvaluateDeviceAsync(device, cancellationToken);
            }
        }
        catch (ConflictException ex)
        {
            // A concurrent evaluation got there first; the next trigger or a rescan converges.
            LogEvaluationConflict(logger, assetId, ex.Code);
            DetachLinks();
        }
    }

    public async Task<(int Created, int AutoApproved)> EvaluateDeviceAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var found = await endpoints.FindAsync([deviceId], cancellationToken);
        return found.TryGetValue(deviceId, out var device) ? await EvaluateDeviceAsync(device, cancellationToken) : (0, 0);
    }

    public async Task<AssetEndpointLinkDto?> GetApprovedForAssetAsync(Asset asset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var link = await db.Set<AssetDeviceLink>().AsNoTracking().FirstOrDefaultAsync(l => l.AssetId == asset.Id && l.Status == AssetDeviceLinkStatus.Approved, cancellationToken);
        if (link is null)
        {
            return null;
        }

        var found = await endpoints.FindAsync([link.DeviceId], cancellationToken);
        if (!found.TryGetValue(link.DeviceId, out var device))
        {
            return null;
        }

        return new AssetEndpointLinkDto(link.Id, ToEndpoint(device), link.Source, link.Confidence, link.DecidedAtUtc, Discrepancies(asset, device));
    }

    public async Task<PagedResult<AssetDeviceLinkDto>> ListAsync(LinkQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var links = db.Set<AssetDeviceLink>().AsNoTracking();
        if (query.Status is { } status)
        {
            links = links.Where(l => l.Status == status);
        }

        if (query.AssetId is { } assetId)
        {
            links = links.Where(l => l.AssetId == assetId);
        }

        if (query.DeviceId is { } deviceId)
        {
            links = links.Where(l => l.DeviceId == deviceId);
        }

        var total = await links.LongCountAsync(cancellationToken);
        var rows = await links
            .OrderBy(l => l.IsAmbiguous).ThenByDescending(l => l.Score).ThenByDescending(l => l.DetectedAtUtc).ThenBy(l => l.Id)
            .Skip(page.Skip).Take(page.SafePageSize)
            .ToListAsync(cancellationToken);
        return new PagedResult<AssetDeviceLinkDto>(await ToDtosAsync(rows, cancellationToken), page.SafePage, page.SafePageSize, total);
    }

    public async Task<AssetDeviceLinkDto> ApproveAsync(Guid linkId, LinkDecisionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var link = await FindLinkAsync(linkId, cancellationToken);
        if (link.Status != AssetDeviceLinkStatus.Suggested)
        {
            throw new ConflictException("Only suggested associations can be approved. Link the endpoint manually to restore a decided pair.", "asset_link_status_conflict");
        }

        var asset = await db.Set<Asset>().AsNoTracking().FirstAsync(a => a.Id == link.AssetId, cancellationToken);
        asset.EnsureNotDisposed();
        var endpoint = await RequireEndpointAsync(link.DeviceId, cancellationToken);
        await EnsureNotLinkedElsewhereAsync(link.AssetId, link.DeviceId, cancellationToken);

        var now = clock.UtcNow;
        link.Approve(now, user.UserId, request.Note);
        await SupersedeOthersAsync(link, now, cancellationToken);
        audit.Record(AssetAuditTrail.LinkEntity, link.Id, link.AssetId, "endpoint_link.approved", $"Associated with endpoint {endpoint.Hostname} ({link.Confidence} confidence).");
        await AssetPersistence.SaveAsync(db, cancellationToken);
        return (await ToDtosAsync([link], cancellationToken))[0];
    }

    public async Task<AssetDeviceLinkDto> RejectAsync(Guid linkId, LinkDecisionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var link = await FindLinkAsync(linkId, cancellationToken);
        link.Reject(clock.UtcNow, user.UserId, request.Note);
        audit.Record(AssetAuditTrail.LinkEntity, link.Id, link.AssetId, "endpoint_link.rejected", $"Rejected suggested endpoint {link.DeviceId}.");
        await AssetPersistence.SaveAsync(db, cancellationToken);
        return (await ToDtosAsync([link], cancellationToken))[0];
    }

    public async Task<AssetDeviceLinkDto> LinkManuallyAsync(Guid assetId, ManualLinkRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var asset = await db.Set<Asset>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == assetId, cancellationToken) ?? throw new NotFoundException("Asset", assetId);
        asset.EnsureNotDisposed();
        var device = await RequireEndpointAsync(request.DeviceId, cancellationToken);
        await EnsureNotLinkedElsewhereAsync(assetId, device.Id, cancellationToken);

        var now = clock.UtcNow;
        var match = MatchEvaluator.Evaluate(Facts(asset), asset.Category, Facts(device));
        var link = await db.Set<AssetDeviceLink>().FirstOrDefaultAsync(l => l.AssetId == assetId && l.DeviceId == device.Id, cancellationToken);
        if (link is null)
        {
            link = AssetDeviceLink.Manual(tenant.RequireTenantId(), assetId, device.Id, match, Serialize(match), now, user.UserId, request.Note);
            db.Set<AssetDeviceLink>().Add(link);
        }
        else
        {
            if (link.Status == AssetDeviceLinkStatus.Approved)
            {
                throw new ConflictException("The asset is already associated with this endpoint.", "asset_already_linked");
            }

            link.Reevaluate(match, Serialize(match), now);
            link.Relink(now, user.UserId, request.Note);
        }

        await SupersedeOthersAsync(link, now, cancellationToken);
        audit.Record(AssetAuditTrail.LinkEntity, link.Id, assetId, "endpoint_link.linked", $"Manually associated with endpoint {device.Hostname}.");
        await AssetPersistence.SaveAsync(db, cancellationToken);
        return (await ToDtosAsync([link], cancellationToken))[0];
    }

    public async Task UnlinkAsync(Guid assetId, LinkDecisionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await db.Set<Asset>().AnyAsync(a => a.Id == assetId, cancellationToken))
        {
            throw new NotFoundException("Asset", assetId);
        }

        var link = await db.Set<AssetDeviceLink>().FirstOrDefaultAsync(l => l.AssetId == assetId && l.Status == AssetDeviceLinkStatus.Approved, cancellationToken)
            ?? throw new NotFoundException("The asset is not associated with an endpoint.");
        link.Unlink(clock.UtcNow, user.UserId, request.Note);
        audit.Record(AssetAuditTrail.LinkEntity, link.Id, assetId, "endpoint_link.unlinked", $"Association with endpoint {link.DeviceId} removed.");
        await AssetPersistence.SaveAsync(db, cancellationToken);
    }

    public async Task<DeviceAssetDto> GetForDeviceAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        _ = await RequireEndpointAsync(deviceId, cancellationToken);
        var links = await db.Set<AssetDeviceLink>().AsNoTracking()
            .Where(l => l.DeviceId == deviceId && (l.Status == AssetDeviceLinkStatus.Approved || l.Status == AssetDeviceLinkStatus.Suggested))
            .OrderByDescending(l => l.Score)
            .ToListAsync(cancellationToken);
        var dtos = await ToDtosAsync(links, cancellationToken);
        return new DeviceAssetDto(dtos.FirstOrDefault(d => d.Status == AssetDeviceLinkStatus.Approved), dtos.Where(d => d.Status == AssetDeviceLinkStatus.Suggested).ToList());
    }

    public async Task<PagedResult<EndpointDto>> GetUnregisteredEndpointsAsync(string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        var request = new PageRequest(page, pageSize);
        var linked = await db.Set<AssetDeviceLink>().AsNoTracking()
            .Where(l => l.Status == AssetDeviceLinkStatus.Approved)
            .Select(l => l.DeviceId)
            .ToListAsync(cancellationToken);
        var result = await endpoints.SearchAsync(search, linked, activeOnly: true, request.Skip, request.SafePageSize, cancellationToken);
        return new PagedResult<EndpointDto>(result.Items.Select(ToEndpoint).ToList(), request.SafePage, request.SafePageSize, result.TotalCount);
    }

    public async Task<RescanResultDto> RescanAsync(CancellationToken cancellationToken)
    {
        var keys = await db.Set<Asset>().AsNoTracking()
            .Where(a => a.SerialNumberKey != null && a.Status != AssetStatus.Disposed)
            .Select(a => a.SerialNumberKey!)
            .Distinct()
            .ToListAsync(cancellationToken);
        int evaluated = 0, created = 0, approved = 0;
        foreach (var batch in keys.Chunk(RescanBatch))
        {
            foreach (var device in await endpoints.FindBySerialKeysAsync(batch, cancellationToken))
            {
                var (c, a) = await EvaluateDeviceAsync(device, cancellationToken);
                evaluated++;
                created += c;
                approved += a;
            }
        }

        audit.Record(AssetAuditTrail.SettingsEntity, tenant.RequireTenantId(), null, "endpoint_match.rescan", $"Re-evaluated {evaluated} endpoints: {created} new suggestions, {approved} approved automatically.");
        await AssetPersistence.SaveAsync(db, cancellationToken);
        return new RescanResultDto(evaluated, created, approved);
    }

    // ------------------------------------------------------------ evaluation

    private async Task<(int Created, int AutoApproved)> EvaluateDeviceAsync(EndpointSummary device, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var key = HardwareIdentity.NormalizeSerial(device.SerialNumber);
        var links = await db.Set<AssetDeviceLink>().Where(l => l.DeviceId == device.Id).ToListAsync(cancellationToken);
        var candidates = key is null
            ? []
            : await db.Set<Asset>().AsNoTracking().Where(a => a.SerialNumberKey == key && a.Status != AssetStatus.Disposed).ToListAsync(cancellationToken);

        foreach (var stale in links.Where(l => l.Status == AssetDeviceLinkStatus.Suggested && candidates.All(a => a.Id != l.AssetId)))
        {
            Withdraw(stale, stale.AssetId);
        }

        if (candidates.Count == 0)
        {
            await AssetPersistence.SaveAsync(db, cancellationToken);
            return (0, 0);
        }

        var deviceCount = Math.Max(1, (await endpoints.FindBySerialKeysAsync([key!], cancellationToken)).Count);
        var approvedForDevice = links.FirstOrDefault(l => l.Status == AssetDeviceLinkStatus.Approved);
        var candidateIds = candidates.Select(a => a.Id).ToList();
        var assetsLinkedElsewhere = (await db.Set<AssetDeviceLink>().AsNoTracking()
                .Where(l => candidateIds.Contains(l.AssetId) && l.DeviceId != device.Id && l.Status == AssetDeviceLinkStatus.Approved)
                .Select(l => l.AssetId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var created = 0;
        var evaluated = new List<(AssetDeviceLink Link, MatchResult Match)>();
        foreach (var asset in candidates)
        {
            var linkedElsewhere = (approvedForDevice is not null && approvedForDevice.AssetId != asset.Id) || assetsLinkedElsewhere.Contains(asset.Id);
            var match = MatchEvaluator.Evaluate(Facts(asset), asset.Category, Facts(device), candidates.Count, deviceCount, linkedElsewhere);
            var link = links.FirstOrDefault(l => l.AssetId == asset.Id);
            if (link is null)
            {
                link = AssetDeviceLink.Suggest(tenant.RequireTenantId(), asset.Id, device.Id, match, Serialize(match), now);
                db.Set<AssetDeviceLink>().Add(link);
                links.Add(link);
                created++;
                audit.Record(
                    AssetAuditTrail.LinkEntity, link.Id, asset.Id, "endpoint_match.suggested",
                    $"Endpoint {device.Hostname} matches by serial number ({match.Confidence} confidence{(match.IsAmbiguous ? ", ambiguous" : string.Empty)}).");
            }
            else if (link.Status is AssetDeviceLinkStatus.Suggested or AssetDeviceLinkStatus.Approved)
            {
                link.Reevaluate(match, Serialize(match), now);
            }
            else
            {
                continue; // Rejected / superseded / unlinked pairs are remembered decisions.
            }

            evaluated.Add((link, match));
        }

        var autoApproved = 0;
        var config = await settings.GetAsync(cancellationToken);
        if (config.AutoApproveHighConfidenceMatches && candidates.Count == 1 && deviceCount == 1 && approvedForDevice is null
            && evaluated is [{ Link.Status: AssetDeviceLinkStatus.Suggested } only] && only.Match.IsUnambiguousHigh)
        {
            only.Link.Approve(now, null, "Approved automatically: unambiguous serial number, manufacturer and model match.");
            autoApproved = 1;
            audit.Record(AssetAuditTrail.LinkEntity, only.Link.Id, only.Link.AssetId, "endpoint_match.auto_approved", $"Associated with endpoint {device.Hostname} automatically (High confidence, unambiguous).");
        }

        await AssetPersistence.SaveAsync(db, cancellationToken);
        if (autoApproved > 0)
        {
            LogAutoApproved(logger, evaluated[0].Link.AssetId, device.Id);
        }

        return (created, autoApproved);
    }

    private void Withdraw(AssetDeviceLink link, Guid assetId)
    {
        db.Set<AssetDeviceLink>().Remove(link);
        audit.Record(AssetAuditTrail.LinkEntity, link.Id, assetId, "endpoint_match.withdrawn", $"Suggestion for endpoint {link.DeviceId} withdrawn: the serial numbers no longer match.");
    }

    private async Task SupersedeOthersAsync(AssetDeviceLink approved, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var others = await db.Set<AssetDeviceLink>()
            .Where(l => l.Id != approved.Id && l.Status == AssetDeviceLinkStatus.Suggested && (l.AssetId == approved.AssetId || l.DeviceId == approved.DeviceId))
            .ToListAsync(cancellationToken);
        foreach (var other in others)
        {
            other.Supersede(now, user.UserId);
        }
    }

    private async Task EnsureNotLinkedElsewhereAsync(Guid assetId, Guid deviceId, CancellationToken cancellationToken)
    {
        if (await db.Set<AssetDeviceLink>().AnyAsync(l => l.AssetId == assetId && l.DeviceId != deviceId && l.Status == AssetDeviceLinkStatus.Approved, cancellationToken))
        {
            throw new ConflictException("The asset is already associated with another endpoint. Remove that association first.", "asset_already_linked");
        }

        if (await db.Set<AssetDeviceLink>().AnyAsync(l => l.DeviceId == deviceId && l.AssetId != assetId && l.Status == AssetDeviceLinkStatus.Approved, cancellationToken))
        {
            throw new ConflictException("The endpoint is already associated with another asset. Remove that association first.", "device_already_linked");
        }
    }

    private async Task<AssetDeviceLink> FindLinkAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Set<AssetDeviceLink>().FirstOrDefaultAsync(l => l.Id == id, cancellationToken) ?? throw new NotFoundException("Asset association", id);

    private async Task<EndpointSummary> RequireEndpointAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var found = await endpoints.FindAsync([deviceId], cancellationToken);
        return found.TryGetValue(deviceId, out var device) ? device : throw new NotFoundException("Device", deviceId);
    }

    private void DetachLinks()
    {
        if (db is DbContext context)
        {
            foreach (var entry in context.ChangeTracker.Entries().Where(e => e.Entity is AssetDeviceLink or AssetAuditLogEntry).ToList())
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    private async Task<IReadOnlyList<AssetDeviceLinkDto>> ToDtosAsync(IReadOnlyList<AssetDeviceLink> links, CancellationToken cancellationToken)
    {
        if (links.Count == 0)
        {
            return [];
        }

        var assetIds = links.Select(l => l.AssetId).Distinct().ToList();
        var assets = await db.Set<Asset>().AsNoTracking().Where(a => assetIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, cancellationToken);
        var devices = await endpoints.FindAsync(links.Select(l => l.DeviceId).Distinct().ToList(), cancellationToken);
        await references.PreloadAsync(links.Select(l => l.DecidedByUserId), [], [], [], cancellationToken);
        return links
            .Where(l => assets.ContainsKey(l.AssetId))
            .Select(l =>
            {
                var asset = assets[l.AssetId];
                var device = devices.GetValueOrDefault(l.DeviceId);
                return new AssetDeviceLinkDto(
                    l.Id,
                    new AssetRefDto(asset.Id, asset.AssetTag, asset.Name, asset.Category, asset.Status, asset.Manufacturer, asset.Model, asset.SerialNumber),
                    device is null ? null : ToEndpoint(device),
                    l.Status,
                    l.Source,
                    l.Confidence,
                    l.Score,
                    l.IsAmbiguous,
                    JsonSerializer.Deserialize<List<MatchEvidence>>(l.EvidenceJson, AssetsJson.Options) ?? [],
                    device is null ? [] : Discrepancies(asset, device),
                    l.DetectedAtUtc,
                    l.DecidedAtUtc,
                    references.User(l.DecidedByUserId),
                    l.DecisionNote);
            })
            .ToList();
    }

    internal static IReadOnlyList<LinkDiscrepancyDto> Discrepancies(Asset asset, EndpointSummary device)
    {
        var result = new List<LinkDiscrepancyDto>();
        if (HardwareIdentity.NormalizeSerial(asset.SerialNumber) != HardwareIdentity.NormalizeSerial(device.SerialNumber))
        {
            result.Add(new("serialNumber", asset.SerialNumber, device.SerialNumber));
        }

        var makerA = HardwareIdentity.NormalizeManufacturer(asset.Manufacturer);
        var makerD = HardwareIdentity.NormalizeManufacturer(device.Manufacturer);
        if (makerA is not null && makerD is not null && makerA != makerD)
        {
            result.Add(new("manufacturer", asset.Manufacturer, device.Manufacturer));
        }

        var modelA = HardwareIdentity.NormalizeModel(asset.Model);
        var modelD = HardwareIdentity.NormalizeModel(device.Model);
        if (modelA is not null && modelD is not null && modelA != modelD && !modelA.Contains(modelD, StringComparison.Ordinal) && !modelD.Contains(modelA, StringComparison.Ordinal))
        {
            result.Add(new("model", asset.Model, device.Model));
        }

        if (asset.BranchId is not null && device.BranchId is not null && asset.BranchId != device.BranchId)
        {
            result.Add(new("branch", asset.BranchId.ToString(), device.BranchId.ToString()));
        }

        if (asset.AssignedUserId is not null && device.AssignedUserId is not null && asset.AssignedUserId != device.AssignedUserId)
        {
            result.Add(new("assignedUser", asset.AssignedUserId.ToString(), device.AssignedUserId.ToString()));
        }

        return result;
    }

    internal static EndpointDto ToEndpoint(EndpointSummary d) =>
        new(d.Id, d.Hostname, d.DisplayName, d.IsActive, d.SerialNumber, d.Manufacturer, d.Model, d.OsName, d.Connectivity, d.LastSeenAtUtc, d.EnrolledAtUtc);

    private static HardwareFacts Facts(Asset asset) => new(asset.SerialNumber, asset.Manufacturer, asset.Model);

    private static HardwareFacts Facts(EndpointSummary device) => new(device.SerialNumber, device.Manufacturer, device.Model);

    private static string Serialize(MatchResult match) => JsonSerializer.Serialize(match.Evidence, AssetsJson.Options);

    [LoggerMessage(Level = LogLevel.Information, Message = "Asset {AssetId} associated with device {DeviceId} automatically")]
    private static partial void LogAutoApproved(ILogger logger, Guid assetId, Guid deviceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Endpoint matching for asset {AssetId} skipped after a concurrent change ({Code})")]
    private static partial void LogEvaluationConflict(ILogger logger, Guid assetId, string code);
}

/// <summary>Runs asset matching whenever the Devices module reports a new or changed hardware identity.</summary>
internal sealed class AssetEndpointObserver(IAssetMatchingService matching) : IEndpointIdentityObserver
{
    public Task OnEndpointIdentityChangedAsync(Guid deviceId, EndpointIdentityChangeKind kind, CancellationToken cancellationToken) =>
        matching.EvaluateDeviceAsync(deviceId, cancellationToken);
}
