namespace Solvix.Modules.Devices.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Solvix.Modules.Devices.Application.Devices;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Web;

/// <summary>Device inventory and management for workspace members (writes: admins).</summary>
[ApiVersion(1.0)]
public sealed class DevicesController(IDeviceService service) : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<DeviceSummaryDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<DeviceSummaryDto>>> List([FromQuery] DeviceListQuery query, CancellationToken cancellationToken) =>
        Ok(await service.ListAsync(query, cancellationToken));

    [HttpGet("stats")]
    [ProducesResponseType<DeviceStatsDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<DeviceStatsDto>> Stats(CancellationToken cancellationToken) =>
        Ok(await service.GetStatsAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<DeviceDetailsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeviceDetailsDto>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.GetAsync(id, cancellationToken));

    [HttpGet("{id:guid}/inventory")]
    [ProducesResponseType<DeviceInventoryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeviceInventoryDto>> Inventory(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.GetInventoryAsync(id, cancellationToken));

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<DeviceDetailsDto>> Update(Guid id, [FromBody] UpdateDeviceRequest request, CancellationToken cancellationToken) =>
        Ok(await service.UpdateAsync(id, request, cancellationToken));

    [HttpPut("{id:guid}/assignment")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<DeviceDetailsDto>> Assign(Guid id, [FromBody] UpdateDeviceAssignmentRequest request, CancellationToken cancellationToken) =>
        Ok(await service.AssignAsync(id, request, cancellationToken));

    [HttpPut("{id:guid}/groups")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<DeviceDetailsDto>> SetGroups(Guid id, [FromBody] SetDeviceGroupsRequest request, CancellationToken cancellationToken) =>
        Ok(await service.SetGroupsAsync(id, request, cancellationToken));

    /// <summary>Asks the agent to send a fresh inventory on its next heartbeat (built-in collection only).</summary>
    [HttpPost("{id:guid}/inventory-request")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<DeviceDetailsDto>> RequestInventory(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.RequestInventoryAsync(id, cancellationToken));

    /// <summary>Revokes all credentials and blocks re-enrollment until the record is deleted.</summary>
    [HttpPost("{id:guid}/retire")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<DeviceDetailsDto>> Retire(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.RetireAsync(id, cancellationToken));

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await service.DeleteAsync(id, cancellationToken);
        return NoContent();
    }
}
