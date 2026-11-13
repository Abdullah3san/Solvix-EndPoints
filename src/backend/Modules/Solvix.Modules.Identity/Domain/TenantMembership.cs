namespace Solvix.Modules.Identity.Domain;

using Solvix.SharedKernel.Entities;

/// <summary>
/// Links a <see cref="User"/> to a tenant. Tenant-owned, so it is covered by
/// query filters and Row-Level Security. Unique per (tenant, user). What the
/// member may do is decided by the roles assigned through <see cref="Roles"/>.
/// </summary>
public sealed class TenantMembership : TenantEntity
{
    private readonly List<MembershipRole> _roles = [];

    private TenantMembership()
    {
    }

    private TenantMembership(Guid tenantId, Guid userId)
        : base(tenantId)
    {
        UserId = userId;
        IsActive = true;
    }

    public Guid UserId { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset? DeactivatedAtUtc { get; private set; }

    public User? User { get; private set; }

    public IReadOnlyCollection<MembershipRole> Roles => _roles;

    public static TenantMembership Create(Guid tenantId, Guid userId) => new(tenantId, userId);

    public void Deactivate(DateTimeOffset at)
    {
        IsActive = false;
        DeactivatedAtUtc = at;
    }

    public void Activate()
    {
        IsActive = true;
        DeactivatedAtUtc = null;
    }

    /// <summary>Makes the assigned roles exactly <paramref name="roleIds"/>. Requires <see cref="Roles"/> to be loaded.</summary>
    public void SetRoles(IReadOnlyCollection<Guid> roleIds)
    {
        ArgumentNullException.ThrowIfNull(roleIds);
        _roles.RemoveAll(r => !roleIds.Contains(r.RoleId));
        foreach (var roleId in roleIds)
        {
            if (_roles.TrueForAll(r => r.RoleId != roleId))
            {
                _roles.Add(MembershipRole.Create(TenantId, Id, roleId));
            }
        }
    }
}

/// <summary>Assignment of a role to a membership. Both ends must belong to the same tenant (composite foreign keys).</summary>
public sealed class MembershipRole : TenantEntity
{
    private MembershipRole()
    {
    }

    private MembershipRole(Guid tenantId, Guid membershipId, Guid roleId)
        : base(tenantId)
    {
        MembershipId = membershipId;
        RoleId = roleId;
    }

    public Guid MembershipId { get; private set; }

    public Guid RoleId { get; private set; }

    public Role? Role { get; private set; }

    public static MembershipRole Create(Guid tenantId, Guid membershipId, Guid roleId) => new(tenantId, membershipId, roleId);
}
