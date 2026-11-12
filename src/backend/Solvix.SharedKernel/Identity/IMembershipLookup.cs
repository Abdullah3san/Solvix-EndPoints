namespace Solvix.SharedKernel.Identity;

/// <summary>
/// Looks up the membership of a user inside the current tenant scope. Used by
/// the tenant resolution middleware to reject tokens whose membership has been
/// revoked. Implemented (and cached) by the Identity module.
/// </summary>
public interface IMembershipLookup
{
    Task<MembershipDescriptor?> FindAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken);
}

public sealed record MembershipDescriptor(Guid TenantId, Guid UserId, TenantRole Role, bool IsActive);
