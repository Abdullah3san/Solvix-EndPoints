namespace Solvix.Modules.Identity.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Solvix.Modules.Identity.Application.Roles;
using Solvix.SharedKernel.Authorization;
using Solvix.SharedKernel.Web;

[ApiVersion(1.0)]
public sealed class RolesController(IRoleService roles) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Roles.View)]
    [ProducesResponseType<IReadOnlyList<RoleDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<RoleDto>>> List(CancellationToken cancellationToken) =>
        Ok(await roles.ListAsync(cancellationToken));

    /// <summary>Every permission a role can contain.</summary>
    [HttpGet("permissions")]
    [RequirePermission(Permissions.Roles.View)]
    [ProducesResponseType<IReadOnlyList<PermissionDto>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<PermissionDto>> ListPermissions() => Ok(roles.ListPermissions());

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.Roles.View)]
    [ProducesResponseType<RoleDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RoleDto>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await roles.GetAsync(id, cancellationToken));

    /// <summary>Creates a custom role. It cannot contain a permission the caller does not hold.</summary>
    [HttpPost]
    [RequirePermission(Permissions.Roles.Manage)]
    [ProducesResponseType<RoleDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<RoleDto>> Create([FromBody] SaveRoleRequest request, CancellationToken cancellationToken)
    {
        var created = await roles.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id, version = "1" }, created);
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.Roles.Manage)]
    [ProducesResponseType<RoleDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<RoleDto>> Update(Guid id, [FromBody] SaveRoleRequest request, CancellationToken cancellationToken) =>
        Ok(await roles.UpdateAsync(id, request, cancellationToken));

    [HttpDelete("{id:guid}")]
    [RequirePermission(Permissions.Roles.Manage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await roles.DeleteAsync(id, cancellationToken);
        return NoContent();
    }
}
