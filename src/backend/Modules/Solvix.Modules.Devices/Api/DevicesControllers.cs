namespace Solvix.Modules.Devices.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Solvix.Modules.Devices.Application.Devices;
using Solvix.Modules.Devices.Application.Enrollment;
using Solvix.Modules.Devices.Application.Groups;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Web;

[ApiVersion(1.0)]
public sealed class DevicesController(IDeviceService service) : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<DeviceDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<DeviceDto>>> List([FromQuery] DeviceListQuery query, CancellationToken cancellationToken) =>
        Ok(await service.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<DeviceDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeviceDto>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.GetAsync(id, cancellationToken));

    /// <summary>Moves a device to a branch/department (drives policy targeting).</summary>
    [HttpPut("{id:guid}/placement")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<DeviceDto>> Place(Guid id, [FromBody] PlaceDeviceRequest request, CancellationToken cancellationToken) =>
        Ok(await service.PlaceAsync(id, request, cancellationToken));

    /// <summary>Retires the device and revokes its agent credential immediately.</summary>
    [HttpPost("{id:guid}/retire")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<DeviceDto>> Retire(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.RetireAsync(id, cancellationToken));
}

[ApiVersion(1.0)]
public sealed class DeviceGroupsController(IDeviceGroupService service) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DeviceGroupDto>>> List(CancellationToken cancellationToken) =>
        Ok(await service.ListAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<DeviceGroupDto>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.GetAsync(id, cancellationToken));

    [HttpPost]
    [Authorize(Policy = Policies.TenantAdmin)]
    [ProducesResponseType<DeviceGroupDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<DeviceGroupDto>> Create([FromBody] SaveDeviceGroupRequest request, CancellationToken cancellationToken)
    {
        var created = await service.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id, version = "1" }, created);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<DeviceGroupDto>> Update(Guid id, [FromBody] SaveDeviceGroupRequest request, CancellationToken cancellationToken) =>
        Ok(await service.UpdateAsync(id, request, cancellationToken));

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.TenantAdmin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await service.DeleteAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpPost("{id:guid}/members")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<DeviceGroupDto>> AddMembers(Guid id, [FromBody] DeviceGroupMembersRequest request, CancellationToken cancellationToken) =>
        Ok(await service.AddMembersAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/members/remove")]
    [Authorize(Policy = Policies.TenantAdmin)]
    public async Task<ActionResult<DeviceGroupDto>> RemoveMembers(Guid id, [FromBody] DeviceGroupMembersRequest request, CancellationToken cancellationToken) =>
        Ok(await service.RemoveMembersAsync(id, request, cancellationToken));
}

[ApiVersion(1.0)]
[Authorize(Policy = Policies.TenantAdmin)]
public sealed class EnrollmentTokensController(IEnrollmentTokenService service) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<EnrollmentTokenDto>>> List(CancellationToken cancellationToken) =>
        Ok(await service.ListAsync(cancellationToken));

    /// <summary>Creates a token. The plaintext token is in the response once and never again.</summary>
    [HttpPost]
    [ProducesResponseType<CreatedEnrollmentTokenDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<CreatedEnrollmentTokenDto>> Create([FromBody] CreateEnrollmentTokenRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await service.CreateAsync(request, cancellationToken));

    [HttpPost("{id:guid}/revoke")]
    public async Task<ActionResult<EnrollmentTokenDto>> Revoke(Guid id, CancellationToken cancellationToken) =>
        Ok(await service.RevokeAsync(id, cancellationToken));
}
