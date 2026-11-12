namespace Solvix.Infrastructure.Authorization;

using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Solvix.SharedKernel.Authorization;
using Solvix.SharedKernel.Identity;

/// <summary>
/// Turns <c>perm:&lt;key&gt;</c> policy names (produced by
/// <see cref="RequirePermissionAttribute"/>) into policies, so permissions do
/// not have to be registered one by one and a new permission cannot be
/// forgotten.
/// </summary>
internal sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    private readonly ConcurrentDictionary<string, AuthorizationPolicy> _policies = new(StringComparer.Ordinal);

    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        ArgumentNullException.ThrowIfNull(policyName);
        if (!policyName.StartsWith(RequirePermissionAttribute.PolicyPrefix, StringComparison.Ordinal))
        {
            return await base.GetPolicyAsync(policyName);
        }

        var permission = policyName[RequirePermissionAttribute.PolicyPrefix.Length..];
        if (!PermissionCatalog.IsKnown(permission))
        {
            // An unknown key must never resolve to "no requirements".
            return null;
        }

        return _policies.GetOrAdd(policyName, _ => new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireClaim(SolvixClaimTypes.TenantId)
            .RequireClaim(SolvixClaimTypes.SessionId)
            .AddRequirements(new PermissionRequirement(permission))
            .Build());
    }
}

/// <summary>
/// Decides a <see cref="PermissionRequirement"/> from the permissions resolved
/// on the server for this request and tenant. The access token is not consulted.
/// </summary>
internal sealed class PermissionAuthorizationHandler(IAccessContext access) : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        var grant = access.Grant;
        if (grant is null)
        {
            context.Fail(new AuthorizationFailureReason(this, AuthorizationFailureCodes.PermissionDenied));
        }
        else if (grant.MfaEnrollmentRequired)
        {
            // The tenant demands a second factor: until the session has one, permissions are not usable.
            context.Fail(new AuthorizationFailureReason(this, AuthorizationFailureCodes.MfaEnrollmentRequired));
        }
        else if (grant.Permissions.Contains(requirement.Permission))
        {
            context.Succeed(requirement);
        }
        else
        {
            context.Fail(new AuthorizationFailureReason(this, AuthorizationFailureCodes.PermissionDenied));
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Requirement of the base policy every controller carries: the request must
/// have been resolved to an active session and membership. This is what makes
/// "authenticated" mean "authenticated AND still allowed in this tenant".
/// </summary>
public sealed class ActiveTenantSessionRequirement : IAuthorizationRequirement
{
}

internal sealed class ActiveTenantSessionHandler(IAccessContext access) : AuthorizationHandler<ActiveTenantSessionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ActiveTenantSessionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (access.Grant is not null)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
