namespace Solvix.Infrastructure.Tenancy;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Serilog.Context;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Tenancy;

/// <summary>
/// Resolves the tenant AND the caller's access for authenticated requests.
///
/// The only things trusted from the access token are three ids: user, tenant
/// and session. Everything else is looked up on the server, on every request:
/// the tenant must exist and be active, the session must be alive, the user
/// must still be an active member, and the permissions are those of the roles
/// the member holds right now. An <c>X-Tenant-Id</c> header is accepted purely
/// as a consistency check: if it is present and disagrees with the token, the
/// request is rejected.
/// </summary>
public sealed partial class TenantResolutionMiddleware(RequestDelegate next, ILogger<TenantResolutionMiddleware> logger)
{
    public const string TenantHeader = "X-Tenant-Id";

    public async Task InvokeAsync(
        HttpContext context,
        ITenantContextMutator mutator,
        ITenantDirectory directory,
        IAccessResolver accessResolver,
        IAccessContextMutator accessContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        var principal = context.User;
        if (principal.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        // Anonymous endpoints (sign-in, refresh, sign-out, public links) run in platform scope and must
        // keep working when the caller happens to send a token whose session has already ended.
        if (context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            await next(context);
            return;
        }

        var tenantClaim = principal.FindFirst(SolvixClaimTypes.TenantId)?.Value;
        var userClaim = principal.FindFirst(SolvixClaimTypes.Subject)?.Value;
        var sessionClaim = principal.FindFirst(SolvixClaimTypes.SessionId)?.Value;
        if (!Guid.TryParse(tenantClaim, out var tenantId)
            || !Guid.TryParse(userClaim, out var userId)
            || !Guid.TryParse(sessionClaim, out var sessionId))
        {
            throw new UnauthorizedException("The access token does not carry a valid tenant and session.");
        }

        if (context.Request.Headers.TryGetValue(TenantHeader, out var headerValues))
        {
            var header = headerValues.ToString();
            if (!Guid.TryParse(header, out var headerTenant) || headerTenant != tenantId)
            {
                LogTenantHeaderMismatch(logger, header, tenantId);
                throw new CrossTenantAccessException("The X-Tenant-Id header does not match the authenticated tenant.");
            }
        }

        mutator.SetTenant(tenantId, principal.FindFirst(SolvixClaimTypes.TenantSlug)?.Value);

        var tenant = await directory.FindByIdAsync(tenantId, context.RequestAborted)
            ?? throw new ForbiddenException("The tenant no longer exists.");
        if (!tenant.IsActive)
        {
            throw new TenantInactiveException(tenant.Status.ToString());
        }

        // Runs inside the tenant scope set above: the lookups are filtered by tenant in EF and by RLS in PostgreSQL.
        var resolution = await accessResolver.ResolveAsync(tenantId, userId, sessionId, context.RequestAborted);
        switch (resolution.Status)
        {
            case AccessStatus.Granted when resolution.Grant is not null:
                accessContext.Set(resolution.Grant);
                break;
            case AccessStatus.SessionInvalid:
                throw new UnauthorizedException("Your session has ended. Sign in again.", "session_expired");
            case AccessStatus.UserInactive:
                throw new ForbiddenException("This account has been disabled.", "account_disabled");
            default:
                throw new ForbiddenException("Your membership in this workspace is no longer active.", "membership_inactive");
        }

        using (LogContext.PushProperty("TenantId", tenantId))
        using (LogContext.PushProperty("TenantSlug", tenant.Slug))
        using (LogContext.PushProperty("UserId", userId))
        using (LogContext.PushProperty("SessionId", sessionId))
        {
            await next(context);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected request: X-Tenant-Id header '{Header}' does not match token tenant {TenantId}")]
    private static partial void LogTenantHeaderMismatch(ILogger logger, string header, Guid tenantId);
}
