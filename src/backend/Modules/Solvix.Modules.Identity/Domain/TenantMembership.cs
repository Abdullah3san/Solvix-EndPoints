namespace Solvix.Modules.Identity.Domain;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Identity;

/// <summary>
/// Links a <see cref="User"/> to a tenant with a role. Tenant-owned, so it is
/// covered by query filters and Row-Level Security. Unique per (tenant, user).
/// </summary>
public sealed class TenantMembership : TenantEntity
{
    private TenantMembership()
    {
    }

    private TenantMembership(Guid tenantId, Guid userId, TenantRole role)
        : base(tenantId)
    {
        UserId = userId;
        Role = role;
        IsActive = true;
    }

    public Guid UserId { get; private set; }

    public TenantRole Role { get; private set; }

    public bool IsActive { get; private set; }

    public User? User { get; private set; }

    public static TenantMembership Create(Guid tenantId, Guid userId, TenantRole role) =>
        new(tenantId, userId, role);

    public void ChangeRole(TenantRole role) => Role = role;

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;
}
