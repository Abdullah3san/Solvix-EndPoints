namespace Solvix.SharedKernel.Entities;

/// <summary>
/// Base class for tenant-owned entities. <see cref="TenantId"/> is assigned
/// once at creation and is immutable afterwards; the persistence layer
/// rejects any attempt to move a row between tenants.
/// </summary>
public abstract class TenantEntity : Entity, ITenantScoped
{
    protected TenantEntity()
    {
    }

    protected TenantEntity(Guid tenantId)
    {
        TenantId = tenantId;
    }

    public Guid TenantId { get; private set; }

    /// <summary>
    /// Assigns the tenant for an entity created inside a tenant scope where the
    /// id was not supplied explicitly. Only callable once.
    /// </summary>
    public void AssignTenant(Guid tenantId)
    {
        if (TenantId != Guid.Empty && TenantId != tenantId)
        {
            throw new InvalidOperationException("TenantId is immutable once assigned.");
        }

        TenantId = tenantId;
    }
}
