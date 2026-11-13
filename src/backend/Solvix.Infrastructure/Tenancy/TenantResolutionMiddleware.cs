namespace Solvix.Infrastructure.Tenancy;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Serilog.Context;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Tenancy;

/// <summary>
/// Resolves the tenant for authenticated requests. The ONLY trusted source is
/// the <c>tid</c> claim of the validated access token. An <c>X-Tenant-Id</c>
/// header is accepted purely as a consistency check: if it is present and
/// disagrees with the token, the request is rejected. The tenant must exist,
/// be active, and the caller must hold an active membership in it.
/// </summary>
public sealed partial class TenantResolutionMiddleware(RequestDelegate next, ILogger<TenantResolutionMiddleware> logger)
{
    public const string TenantHeader = "X-Tenant-Id";

    public async Task InvokeAsync(
        HttpContext context,
        ITenantContextMutator mutator,
        ITenantDirectory directory,
        IMembershipLookup memberships)
    {
        ArgumentNullException.ThrowIfNull(context);
        var principal = context.User;
        if (principal.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        var tenantClaim = principal.FindFirst(SolvixClaimTypes.TenantId)?.Value;
        var userClaim = principal.FindFirst(SolvixClaimTypes.Subject)?.Value;
        if (!Guid.TryParse(tenantClaim, out var tenantId) || !Guid.TryParse(userClaim, out var userId))
        {
            throw new UnauthorizedException("The access token does not carry a valid tenant.");
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

        // Endpoint agents have no membership: the device authentication handler already
        // verified that the device is enrolled in this tenant and not retired.
        var isAgent = principal.FindFirst(SolvixClaimTypes.ActorType)?.Value == ActorTypes.Agent;
        if (!isAgent)
        {
            var membership = await memberships.FindAsync(tenantId, userId, context.RequestAborted);
            if (membership is not { IsActive: true })
            {
                throw new ForbiddenException("Your membership in this workspace is no longer active.");
            }
        }

        using (LogContext.PushProperty("TenantId", tenantId))
        using (LogContext.PushProperty("TenantSlug", tenant.Slug))
        using (LogContext.PushProperty("UserId", userId))
        {
            await next(context);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected request: X-Tenant-Id header '{Header}' does not match token tenant {TenantId}")]
    private static partial void LogTenantHeaderMismatch(ILogger logger, string header, Guid tenantId);
}
