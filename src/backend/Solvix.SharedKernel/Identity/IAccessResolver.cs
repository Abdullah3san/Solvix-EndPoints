namespace Solvix.SharedKernel.Identity;

/// <summary>
/// Resolves what an authenticated caller may do in a tenant. Called once per
/// request by the tenant resolution middleware, after the tenant scope is set,
/// so the lookup itself is subject to query filters and Row-Level Security.
/// Implemented (and cached) by the Identity module.
/// </summary>
public interface IAccessResolver
{
    Task<AccessResolution> ResolveAsync(Guid tenantId, Guid userId, Guid sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Permissions a member currently holds in <paramref name="tenantId"/>, independent of any
    /// sign-in session (used to re-authorize work queued on someone's behalf, e.g. a remote
    /// command delivered after the requester signed out). Empty when the membership or the
    /// account is inactive. Same cache and invalidation as <see cref="ResolveAsync"/>.
    /// </summary>
    Task<IReadOnlySet<string>> ResolveMemberPermissionsAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken);
}

public enum AccessStatus
{
    Granted = 0,

    /// <summary>The session was revoked, expired, or does not belong to this user and tenant.</summary>
    SessionInvalid = 1,

    /// <summary>The user is not (or no longer) an active member of the tenant.</summary>
    MembershipInactive = 2,

    /// <summary>The account itself is disabled.</summary>
    UserInactive = 3,
}

public sealed record AccessResolution(AccessStatus Status, AccessGrant? Grant)
{
    public static AccessResolution Denied(AccessStatus status) => new(status, null);
}

/// <param name="MfaEnrollmentRequired">
/// The tenant requires multi-factor authentication and this session has not
/// completed it: only self-service endpoints are available until it has.
/// </param>
public sealed record AccessGrant(
    Guid MembershipId,
    Guid SessionId,
    IReadOnlySet<string> Permissions,
    IReadOnlyList<string> Roles,
    bool MfaVerified,
    bool MfaEnrollmentRequired);

/// <summary>Per-request holder of the resolved <see cref="AccessGrant"/>.</summary>
public interface IAccessContext
{
    AccessGrant? Grant { get; }
}

public interface IAccessContextMutator
{
    void Set(AccessGrant grant);
}

public sealed class AccessContext : IAccessContext, IAccessContextMutator
{
    public AccessGrant? Grant { get; private set; }

    public void Set(AccessGrant grant) => Grant = grant ?? throw new ArgumentNullException(nameof(grant));
}
