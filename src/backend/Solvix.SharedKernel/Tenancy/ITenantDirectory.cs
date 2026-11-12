namespace Solvix.SharedKernel.Tenancy;

/// <summary>
/// Read-only, platform-level lookup of tenants used by cross-cutting
/// components (tenant resolution, authentication) that run before a tenant
/// scope exists. Implemented by the Tenancy module.
/// </summary>
public interface ITenantDirectory
{
    Task<TenantDescriptor?> FindByIdAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<TenantDescriptor?> FindBySlugAsync(string slug, CancellationToken cancellationToken);
}

public sealed record TenantDescriptor(Guid Id, string Slug, string Name, TenantStatus Status)
{
    public bool IsActive => Status == TenantStatus.Active;
}

public enum TenantStatus
{
    Active = 1,
    Suspended = 2,
    PendingDeletion = 3,
}
