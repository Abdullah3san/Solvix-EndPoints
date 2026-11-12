namespace Solvix.Modules.Identity.Api;

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Solvix.Modules.Identity.Application.Auth;
using Solvix.Modules.Identity.Application.Sessions;
using Solvix.SharedKernel.Authorization;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Web;

/// <summary>Sign-in, token refresh, sign-out and the caller's own sessions.</summary>
[ApiVersion(1.0)]
public sealed class AuthController(IAuthService auth, ISessionService sessions) : ApiControllerBase
{
    /// <summary>
    /// Checks e-mail and password. Returns a session, or, when the account has
    /// multi-factor authentication, a short-lived challenge to complete with <c>mfa/verify</c>.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Credentials)]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest request, CancellationToken cancellationToken) =>
        Ok(await auth.LoginAsync(request, cancellationToken));

    /// <summary>Completes a sign-in with an authenticator code or a recovery code.</summary>
    [HttpPost("mfa/verify")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Credentials)]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthResponse>> VerifyMfa([FromBody] MfaVerifyRequest request, CancellationToken cancellationToken) =>
        Ok(await auth.VerifyMfaAsync(request, cancellationToken));

    /// <summary>Exchanges a refresh token for a new access token and a new refresh token. The old one stops working.</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Refresh)]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthResponse>> Refresh([FromBody] RefreshRequest request, CancellationToken cancellationToken) =>
        Ok(await auth.RefreshAsync(request, cancellationToken));

    /// <summary>
    /// Ends the session of the presented access token and/or refresh token.
    /// Anonymous on purpose so that signing out still works once the access token has expired.
    /// </summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Refresh)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest? request, CancellationToken cancellationToken)
    {
        // Claims of a signature-validated token; the session itself may already be gone.
        Guid? sessionId = Guid.TryParse(User.FindFirst(SolvixClaimTypes.SessionId)?.Value, out var sid) ? sid : null;
        Guid? userId = Guid.TryParse(User.FindFirst(SolvixClaimTypes.Subject)?.Value, out var uid) ? uid : null;
        await auth.LogoutAsync(sessionId, userId, request?.RefreshToken, cancellationToken);
        return NoContent();
    }

    /// <summary>Signs the caller into another workspace they belong to. The current session ends.</summary>
    [HttpPost("switch-tenant")]
    [AuthenticatedOnly]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AuthResponse>> SwitchTenant([FromBody] SwitchTenantRequest request, CancellationToken cancellationToken) =>
        Ok(await auth.SwitchTenantAsync(request.TenantSlug, cancellationToken));

    /// <summary>The caller, the tenant resolved for this request and the permissions held there right now.</summary>
    [HttpGet("me")]
    [AuthenticatedOnly]
    [ProducesResponseType<CurrentUserDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<CurrentUserDto>> Me(CancellationToken cancellationToken) =>
        Ok(await auth.GetCurrentAsync(cancellationToken));

    /// <summary>The workspaces the caller can switch to.</summary>
    [HttpGet("workspaces")]
    [AuthenticatedOnly]
    [ProducesResponseType<IReadOnlyList<AuthTenantDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AuthTenantDto>>> Workspaces(CancellationToken cancellationToken) =>
        Ok(await auth.ListWorkspacesAsync(cancellationToken));

    /// <summary>The caller's own active sessions.</summary>
    [HttpGet("sessions")]
    [AuthenticatedOnly]
    [ProducesResponseType<IReadOnlyList<SessionDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<SessionDto>>> Sessions(CancellationToken cancellationToken) =>
        Ok(await sessions.ListMineAsync(cancellationToken));

    [HttpDelete("sessions/{sessionId:guid}")]
    [AuthenticatedOnly]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeSession(Guid sessionId, CancellationToken cancellationToken)
    {
        await sessions.RevokeMineAsync(sessionId, cancellationToken);
        return NoContent();
    }

    [HttpPost("sessions/revoke-others")]
    [AuthenticatedOnly]
    [ProducesResponseType<Application.Users.SessionsRevokedDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<Application.Users.SessionsRevokedDto>> RevokeOtherSessions(CancellationToken cancellationToken) =>
        Ok(new Application.Users.SessionsRevokedDto(await sessions.RevokeMyOtherSessionsAsync(cancellationToken)));
}
