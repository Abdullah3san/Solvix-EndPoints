namespace Solvix.SharedKernel.Tenancy;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Creates an isolated dependency-injection scope bound to a single tenant.
/// Background jobs MUST obtain their services from such a scope so that the
/// query filters, SaveChanges enforcement and database RLS session variable
/// all apply exactly as they do for an HTTP request.
/// </summary>
public interface ITenantScopeFactory
{
    AsyncServiceScope CreateScope(Guid tenantId, string? tenantSlug = null);
}
