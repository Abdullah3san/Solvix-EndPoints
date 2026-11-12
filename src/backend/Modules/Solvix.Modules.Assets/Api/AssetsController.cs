namespace Solvix.Modules.Assets.Api;

using System.Text;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Solvix.Modules.Assets.Application;
using Solvix.Modules.Assets.Domain;
using Solvix.Modules.Assets.Labels;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Web;

/// <summary>The asset register. Administrators only: it contains purchase and financial data.</summary>
[ApiVersion(1.0)]
[Authorize(Policy = Policies.TenantAdmin)]
[Route("api/v{version:apiVersion}/assets")]
public sealed class AssetsController(IAssetService assets, IAssetMatchingService matching, IMaintenanceService maintenance) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<AssetSummaryDto>>> List([FromQuery] AssetListQuery query, CancellationToken cancellationToken) =>
        Ok(await assets.ListAsync(query, cancellationToken));

    [HttpGet("stats")]
    public async Task<ActionResult<AssetStatsDto>> Stats(CancellationToken cancellationToken) => Ok(await assets.GetStatsAsync(cancellationToken));

    [HttpGet("categories")]
    public ActionResult<IReadOnlyList<CategoryDto>> Categories() =>
        Ok(AssetCategories.All.Select(c => new CategoryDto(c.Category, c.Code, c.DisplayName, c.EndpointManageable, c.DefaultUsefulLifeMonths)).ToList());

    [HttpGet("export")]
    [Produces("text/csv")]
    public async Task<IActionResult> Export([FromQuery] AssetListQuery query, CancellationToken cancellationToken)
    {
        var csv = await assets.ExportCsvAsync(query, cancellationToken);
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv; charset=utf-8", "asset-register.csv");
    }

    /// <summary>Resolves a scanned asset tag, QR payload or serial number.</summary>
    [HttpGet("lookup")]
    public async Task<ActionResult<AssetLookupDto>> Lookup([FromQuery] string code, CancellationToken cancellationToken) =>
        Ok(await assets.LookupAsync(code, cancellationToken));

    [HttpPost]
    [ProducesResponseType<AssetDetailsDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<AssetDetailsDto>> Create([FromBody] CreateAssetRequest request, CancellationToken cancellationToken)
    {
        var created = await assets.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Summary.Id, version = "1.0" }, created);
    }

    [HttpPost("bulk")]
    public async Task<ActionResult<BulkAssetResultDto>> Bulk([FromBody] BulkAssetRequest request, CancellationToken cancellationToken) =>
        Ok(await assets.BulkAsync(request, cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AssetDetailsDto>> Get(Guid id, CancellationToken cancellationToken) => Ok(await assets.GetAsync(id, cancellationToken));

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<AssetDetailsDto>> Update(Guid id, [FromBody] UpdateAssetRequest request, CancellationToken cancellationToken) =>
        Ok(await assets.UpdateAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/assign")]
    public async Task<ActionResult<AssetDetailsDto>> Assign(Guid id, [FromBody] AssignAssetRequest request, CancellationToken cancellationToken) =>
        Ok(await assets.AssignAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/return")]
    public async Task<ActionResult<AssetDetailsDto>> Return(Guid id, [FromBody] ReturnAssetRequest request, CancellationToken cancellationToken) =>
        Ok(await assets.ReturnAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/transfer")]
    public async Task<ActionResult<AssetDetailsDto>> Transfer(Guid id, [FromBody] TransferAssetRequest request, CancellationToken cancellationToken) =>
        Ok(await assets.TransferAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/retire")]
    public async Task<ActionResult<AssetDetailsDto>> Retire(Guid id, [FromBody] RetireAssetRequest request, CancellationToken cancellationToken) =>
        Ok(await assets.RetireAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/reinstate")]
    public async Task<ActionResult<AssetDetailsDto>> Reinstate(Guid id, CancellationToken cancellationToken) =>
        Ok(await assets.ReinstateAsync(id, cancellationToken));

    [HttpPost("{id:guid}/dispose")]
    public async Task<ActionResult<AssetDetailsDto>> Dispose(Guid id, [FromBody] DisposeAssetRequest request, CancellationToken cancellationToken) =>
        Ok(await assets.DisposeAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/mark-missing")]
    public async Task<ActionResult<AssetDetailsDto>> MarkMissing(Guid id, CancellationToken cancellationToken) =>
        Ok(await assets.MarkMissingAsync(id, cancellationToken));

    [HttpPost("{id:guid}/mark-found")]
    public async Task<ActionResult<AssetDetailsDto>> MarkFound(Guid id, CancellationToken cancellationToken) =>
        Ok(await assets.MarkFoundAsync(id, cancellationToken));

    [HttpGet("{id:guid}/assignments")]
    public async Task<ActionResult<IReadOnlyList<AssignmentDto>>> Assignments(Guid id, CancellationToken cancellationToken) =>
        Ok(await assets.GetAssignmentsAsync(id, cancellationToken));

    [HttpGet("{id:guid}/transfers")]
    public async Task<ActionResult<IReadOnlyList<TransferDto>>> Transfers(Guid id, CancellationToken cancellationToken) =>
        Ok(await assets.GetTransfersAsync(id, cancellationToken));

    [HttpGet("{id:guid}/maintenance")]
    public async Task<ActionResult<PagedResult<MaintenanceRecordDto>>> Maintenance(Guid id, [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken cancellationToken = default) =>
        Ok(await maintenance.ListAsync(new MaintenanceListQuery(null, id, page, pageSize), cancellationToken));

    [HttpPost("{id:guid}/maintenance")]
    [ProducesResponseType<MaintenanceRecordDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<MaintenanceRecordDto>> CreateMaintenance(Guid id, [FromBody] CreateMaintenanceRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await maintenance.CreateAsync(id, request, cancellationToken));

    [HttpGet("{id:guid}/history")]
    public async Task<ActionResult<PagedResult<AuditLogEntryDto>>> History(Guid id, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken cancellationToken = default) =>
        Ok(await assets.GetAuditLogAsync(new AuditLogQuery(id, null, null, page, pageSize), cancellationToken));

    [HttpGet("{id:guid}/depreciation")]
    public async Task<ActionResult<DepreciationResult>> Depreciation(Guid id, [FromQuery] DateOnly? asOf, CancellationToken cancellationToken) =>
        Ok(await assets.GetDepreciationAsync(id, asOf, cancellationToken));

    [HttpPut("{id:guid}/endpoint-link")]
    public async Task<ActionResult<AssetDeviceLinkDto>> Link(Guid id, [FromBody] ManualLinkRequest request, CancellationToken cancellationToken) =>
        Ok(await matching.LinkManuallyAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/endpoint-link/remove")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Unlink(Guid id, [FromBody] LinkDecisionRequest request, CancellationToken cancellationToken)
    {
        await matching.UnlinkAsync(id, request, cancellationToken);
        return NoContent();
    }

    [HttpGet("{id:guid}/barcode.svg")]
    [Produces("image/svg+xml")]
    public async Task<IActionResult> Barcode(Guid id, CancellationToken cancellationToken)
    {
        var asset = await assets.FindAsync(id, tracking: false, cancellationToken);
        return Svg(LabelRenderer.Code128Svg(asset.AssetTag));
    }

    [HttpGet("{id:guid}/qr.svg")]
    [Produces("image/svg+xml")]
    public async Task<IActionResult> Qr(Guid id, CancellationToken cancellationToken)
    {
        var asset = await assets.FindAsync(id, tracking: false, cancellationToken);
        return Svg(LabelRenderer.QrSvg(assets.QrPayload(asset)));
    }

    private FileContentResult Svg(string svg)
    {
        // Rendered as an image only: no scripts may run even if the SVG is opened directly.
        Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(Encoding.UTF8.GetBytes(svg), "image/svg+xml");
    }
}

/// <summary>Assets held by the signed-in user. Any workspace member; never exposes other people's assets or costs.</summary>
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/my-assets")]
public sealed class MyAssetsController(IAssetService assets) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<MyAssetDto>>> List(CancellationToken cancellationToken) => Ok(await assets.GetMineAsync(cancellationToken));
}
