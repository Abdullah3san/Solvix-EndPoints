namespace Solvix.Modules.Assets.Api;

using System.Text;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Solvix.Modules.Assets.Application;
using Solvix.Modules.Assets.Domain;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Web;

[ApiVersion(1.0)]
[Authorize(Policy = Policies.TenantAdmin)]
[Route("api/v{version:apiVersion}/asset-vendors")]
public sealed class AssetVendorsController(IVendorService vendors) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<VendorDto>>> List([FromQuery] VendorListQuery query, CancellationToken cancellationToken) =>
        Ok(await vendors.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<VendorDto>> Get(Guid id, CancellationToken cancellationToken) => Ok(await vendors.GetAsync(id, cancellationToken));

    [HttpPost]
    [ProducesResponseType<VendorDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<VendorDto>> Create([FromBody] VendorRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await vendors.CreateAsync(request, cancellationToken));

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<VendorDto>> Update(Guid id, [FromBody] VendorRequest request, CancellationToken cancellationToken) =>
        Ok(await vendors.UpdateAsync(id, request, cancellationToken));

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await vendors.DeleteAsync(id, cancellationToken);
        return NoContent();
    }
}

[ApiVersion(1.0)]
[Authorize(Policy = Policies.TenantAdmin)]
[Route("api/v{version:apiVersion}/asset-maintenance")]
public sealed class AssetMaintenanceController(IMaintenanceService maintenance) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<MaintenanceRecordDto>>> List([FromQuery] MaintenanceListQuery query, CancellationToken cancellationToken) =>
        Ok(await maintenance.ListAsync(query, cancellationToken));

    [HttpPost("{id:guid}/start")]
    public async Task<ActionResult<MaintenanceRecordDto>> Start(Guid id, CancellationToken cancellationToken) => Ok(await maintenance.StartAsync(id, cancellationToken));

    [HttpPost("{id:guid}/complete")]
    public async Task<ActionResult<MaintenanceRecordDto>> Complete(Guid id, [FromBody] CompleteMaintenanceRequest request, CancellationToken cancellationToken) =>
        Ok(await maintenance.CompleteAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/cancel")]
    public async Task<ActionResult<MaintenanceRecordDto>> Cancel(Guid id, [FromBody] CancelMaintenanceRequest request, CancellationToken cancellationToken) =>
        Ok(await maintenance.CancelAsync(id, request, cancellationToken));
}

[ApiVersion(1.0)]
[Authorize(Policy = Policies.TenantAdmin)]
[Route("api/v{version:apiVersion}/asset-audits")]
public sealed class AssetAuditsController(IPhysicalAuditService audits) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<PhysicalAuditDto>>> List([FromQuery] PhysicalAuditStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default) =>
        Ok(await audits.ListAsync(status, page, pageSize, cancellationToken));

    [HttpPost]
    [ProducesResponseType<PhysicalAuditDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<PhysicalAuditDto>> Start([FromBody] StartPhysicalAuditRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await audits.StartAsync(request, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PhysicalAuditDto>> Get(Guid id, CancellationToken cancellationToken) => Ok(await audits.GetAsync(id, cancellationToken));

    [HttpGet("{id:guid}/items")]
    public async Task<ActionResult<PagedResult<PhysicalAuditItemDto>>> Items(Guid id, [FromQuery] PhysicalAuditItemQuery query, CancellationToken cancellationToken) =>
        Ok(await audits.GetItemsAsync(id, query, cancellationToken));

    [HttpPost("{id:guid}/scan")]
    public async Task<ActionResult<ScanResultDto>> Scan(Guid id, [FromBody] ScanRequest request, CancellationToken cancellationToken) =>
        Ok(await audits.ScanAsync(id, request, cancellationToken));

    [HttpPut("{id:guid}/items/{itemId:guid}")]
    public async Task<ActionResult<PhysicalAuditItemDto>> UpdateItem(Guid id, Guid itemId, [FromBody] UpdateAuditItemRequest request, CancellationToken cancellationToken) =>
        Ok(await audits.UpdateItemAsync(id, itemId, request, cancellationToken));

    [HttpPost("{id:guid}/complete")]
    public async Task<ActionResult<PhysicalAuditDto>> Complete(Guid id, [FromBody] CompletePhysicalAuditRequest request, CancellationToken cancellationToken) =>
        Ok(await audits.CompleteAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/cancel")]
    public async Task<ActionResult<PhysicalAuditDto>> Cancel(Guid id, CancellationToken cancellationToken) => Ok(await audits.CancelAsync(id, cancellationToken));
}

/// <summary>Review of asset ↔ managed endpoint associations.</summary>
[ApiVersion(1.0)]
[Authorize(Policy = Policies.TenantAdmin)]
[Route("api/v{version:apiVersion}/asset-matches")]
public sealed class AssetMatchesController(IAssetMatchingService matching, IEndpointRegistrationService registration) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<AssetDeviceLinkDto>>> List([FromQuery] LinkQuery query, CancellationToken cancellationToken) =>
        Ok(await matching.ListAsync(query, cancellationToken));

    [HttpPost("{id:guid}/approve")]
    public async Task<ActionResult<AssetDeviceLinkDto>> Approve(Guid id, [FromBody] LinkDecisionRequest request, CancellationToken cancellationToken) =>
        Ok(await matching.ApproveAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/reject")]
    public async Task<ActionResult<AssetDeviceLinkDto>> Reject(Guid id, [FromBody] LinkDecisionRequest request, CancellationToken cancellationToken) =>
        Ok(await matching.RejectAsync(id, request, cancellationToken));

    [HttpGet("devices/{deviceId:guid}")]
    public async Task<ActionResult<DeviceAssetDto>> ForDevice(Guid deviceId, CancellationToken cancellationToken) =>
        Ok(await matching.GetForDeviceAsync(deviceId, cancellationToken));

    /// <summary>Active managed endpoints that are not associated with any asset yet.</summary>
    [HttpGet("unregistered-endpoints")]
    public async Task<ActionResult<PagedResult<EndpointDto>>> Unregistered([FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default) =>
        Ok(await matching.GetUnregisteredEndpointsAsync(search, page, pageSize, cancellationToken));

    [HttpPost("register-endpoint")]
    [ProducesResponseType<AssetDetailsDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<AssetDetailsDto>> RegisterEndpoint([FromBody] RegisterEndpointRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await registration.RegisterAsync(request, cancellationToken));

    [HttpPost("rescan")]
    public async Task<ActionResult<RescanResultDto>> Rescan(CancellationToken cancellationToken) => Ok(await matching.RescanAsync(cancellationToken));
}

[ApiVersion(1.0)]
[Authorize(Policy = Policies.TenantAdmin)]
[Route("api/v{version:apiVersion}/asset-reports")]
public sealed class AssetReportsController(IAssetReportService reports, IAssetService assets) : ApiControllerBase
{
    [HttpGet("summary")]
    public async Task<ActionResult<AssetSummaryReportDto>> Summary(CancellationToken cancellationToken) => Ok(await reports.GetSummaryAsync(cancellationToken));

    [HttpGet("depreciation")]
    public async Task<ActionResult<DepreciationReportDto>> Depreciation([FromQuery] DateOnly? asOf, CancellationToken cancellationToken) =>
        Ok(await reports.GetDepreciationAsync(asOf, cancellationToken));

    [HttpGet("depreciation/export")]
    [Produces("text/csv")]
    public async Task<IActionResult> ExportDepreciation([FromQuery] DateOnly? asOf, CancellationToken cancellationToken)
    {
        var csv = await reports.ExportDepreciationCsvAsync(asOf, cancellationToken);
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv; charset=utf-8", "asset-depreciation.csv");
    }

    /// <summary>Tenant-wide asset management audit log.</summary>
    [HttpGet("audit-log")]
    public async Task<ActionResult<PagedResult<AuditLogEntryDto>>> AuditLog([FromQuery] AuditLogQuery query, CancellationToken cancellationToken) =>
        Ok(await assets.GetAuditLogAsync(query, cancellationToken));
}

[ApiVersion(1.0)]
[Authorize(Policy = Policies.TenantAdmin)]
[Route("api/v{version:apiVersion}/asset-settings")]
public sealed class AssetSettingsController(IAssetSettingsService settings) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<AssetSettingsDto>> Get(CancellationToken cancellationToken) => Ok(await settings.GetAsync(cancellationToken));

    [HttpPut]
    public async Task<ActionResult<AssetSettingsDto>> Update([FromBody] AssetSettingsDto request, CancellationToken cancellationToken) =>
        Ok(await settings.UpdateAsync(request, cancellationToken));
}
