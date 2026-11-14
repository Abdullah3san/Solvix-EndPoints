namespace Solvix.SharedKernel.Entities;

/// <summary>
/// Marks an entity as owned by exactly one tenant. Every implementation is
/// automatically protected by: an EF Core global query filter, the
/// <c>TenantEnforcementInterceptor</c> on SaveChanges, a database foreign key
/// to <c>tenants</c>, and a PostgreSQL Row-Level Security policy.
/// </summary>
public interface ITenantScoped
{
    Guid TenantId { get; }
}
