namespace Solvix.Modules.Devices.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Solvix.Modules.Devices.Application.Groups;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Web;

[ApiVersion(1.0)]
public sealed class DeviceGroupsController(IDeviceGroupService service) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<DeviceGroupDto>>> List([FromQuery] DeviceGroupListQuery query, CancellationToken cancellationToken) =>
        Ok(await service.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<DeviceGroupDto>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.GetAsync(id, cancellationToken));

    [HttpPost]
    [Authorize(Policy = Policies.TenantAdmin)]
    [ProducesResponseType<DeviceGroupDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<DeviceGroupDto>> Create([FromBody] CreateDeviceGroupRequest request, CancellationToken cancellationToken)
    {
        var created = await service.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id, version = "1" }, created);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<DeviceGroupDto>> Update(Guid id, [FromBody] UpdateDeviceGroupRequest request, CancellationToken cancellationToken) =>
        Ok(await service.UpdateAsync(id, request, cancellationToken));

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await service.DeleteAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpPost("{id:guid}/devices")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<DeviceGroupDto>> AddDevices(Guid id, [FromBody] AddGroupDevicesRequest request, CancellationToken cancellationToken) =>
        Ok(await service.AddDevicesAsync(id, request, cancellationToken));

    [HttpDelete("{id:guid}/devices/{deviceId:guid}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<DeviceGroupDto>> RemoveDevice(Guid id, Guid deviceId, CancellationToken cancellationToken) =>
        Ok(await service.RemoveDeviceAsync(id, deviceId, cancellationToken));
}
