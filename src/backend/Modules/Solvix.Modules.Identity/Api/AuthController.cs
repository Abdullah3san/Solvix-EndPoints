namespace Solvix.Modules.Identity.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Solvix.Modules.Identity.Application.Auth;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Tenancy;
using Solvix.SharedKernel.Web;

[ApiVersion(1.0)]
public sealed class AuthController(IAuthService authService, ICurrentUser currentUser, ITenantContext tenantContext)
    : ApiControllerBase
{
    /// <summary>Exchanges e-mail + password for a tenant-bound access token.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthResponse>> Login([FromBody] LoginRequest request, CancellationToken cancellationToken) =>
        Ok(await authService.LoginAsync(request, cancellationToken));

    /// <summary>Issues a new token for another workspace the caller belongs to.</summary>
    [HttpPost("switch-tenant")]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AuthResponse>> SwitchTenant([FromBody] SwitchTenantRequest request, CancellationToken cancellationToken) =>
        Ok(await authService.SwitchTenantAsync(currentUser.RequireUserId(), request.TenantSlug, cancellationToken));

    /// <summary>Returns the caller and the tenant resolved for this request.</summary>
    [HttpGet("me")]
    [ProducesResponseType<CurrentUserDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<CurrentUserDto>> Me(CancellationToken cancellationToken) =>
        Ok(await authService.GetCurrentAsync(currentUser.RequireUserId(), tenantContext.RequireTenantId(), cancellationToken));
}
