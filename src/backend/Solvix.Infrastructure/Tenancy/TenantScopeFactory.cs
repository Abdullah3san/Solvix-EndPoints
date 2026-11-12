namespace Solvix.Infrastructure.Tenancy;

using Microsoft.Extensions.DependencyInjection;
using Solvix.SharedKernel.Tenancy;

internal sealed class TenantScopeFactory(IServiceScopeFactory scopeFactory) : ITenantScopeFactory
{
    public AsyncServiceScope CreateScope(Guid tenantId, string? tenantSlug = null)
    {
        var scope = scopeFactory.CreateAsyncScope();
        try
        {
            scope.ServiceProvider.GetRequiredService<ITenantContextMutator>().SetTenant(tenantId, tenantSlug);
            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }
}
