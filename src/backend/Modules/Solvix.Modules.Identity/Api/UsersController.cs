namespace Solvix.Modules.Identity.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Solvix.Modules.Identity.Application.Auth;
using Solvix.Modules.Identity.Application.Users;
using Solvix.SharedKernel.Authorization;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Web;

/// <summary>Members of the current tenant: roles, activation and sessions.</summary>
[ApiVersion(1.0)]
public sealed class UsersController(IUserService users) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Users.View)]
    [ProducesResponseType<PagedResult<TenantUserDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<TenantUserDto>>> List([FromQuery] UserListQuery query, CancellationToken cancellationToken) =>
        Ok(await users.ListAsync(query, cancellationToken));

    [HttpGet("{userId:guid}")]
    [RequirePermission(Permissions.Users.View)]
    [ProducesResponseType<TenantUserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantUserDto>> Get(Guid userId, CancellationToken cancellationToken) =>
        Ok(await users.GetAsync(userId, cancellationToken));

    /// <summary>Replaces the user's roles. The caller can only assign roles whose permissions they hold themselves.</summary>
    [HttpPut("{userId:guid}/roles")]
    [RequirePermission(Permissions.Users.Manage)]
    [ProducesResponseType<TenantUserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<TenantUserDto>> SetRoles(Guid userId, [FromBody] SetUserRolesRequest request, CancellationToken cancellationToken) =>
        Ok(await users.SetRolesAsync(userId, request, cancellationToken));

    /// <summary>Suspends the user's access to this workspace and ends their sessions here.</summary>
    [HttpPost("{userId:guid}/deactivate")]
    [RequirePermission(Permissions.Users.Manage)]
    [ProducesResponseType<TenantUserDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TenantUserDto>> Deactivate(Guid userId, CancellationToken cancellationToken) =>
        Ok(await users.DeactivateAsync(userId, cancellationToken));

    [HttpPost("{userId:guid}/activate")]
    [RequirePermission(Permissions.Users.Manage)]
    [ProducesResponseType<TenantUserDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TenantUserDto>> Activate(Guid userId, CancellationToken cancellationToken) =>
        Ok(await users.ActivateAsync(userId, cancellationToken));

    /// <summary>Removes the user from this workspace. Their account and other workspaces are not affected.</summary>
    [HttpDelete("{userId:guid}")]
    [RequirePermission(Permissions.Users.Manage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Remove(Guid userId, CancellationToken cancellationToken)
    {
        await users.RemoveAsync(userId, cancellationToken);
        return NoContent();
    }

    /// <summary>The user's active sessions in this workspace.</summary>
    [HttpGet("{userId:guid}/sessions")]
    [RequirePermission(Permissions.Security.View)]
    [ProducesResponseType<IReadOnlyList<SessionDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<SessionDto>>> Sessions(Guid userId, CancellationToken cancellationToken) =>
        Ok(await users.ListSessionsAsync(userId, cancellationToken));

    /// <summary>Signs the user out of this workspace everywhere.</summary>
    [HttpPost("{userId:guid}/sessions/revoke")]
    [RequirePermission(Permissions.Security.Manage)]
    [ProducesResponseType<SessionsRevokedDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SessionsRevokedDto>> RevokeSessions(Guid userId, CancellationToken cancellationToken) =>
        Ok(await users.RevokeSessionsAsync(userId, cancellationToken));
}
