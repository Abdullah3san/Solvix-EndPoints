namespace Solvix.Modules.Tenancy.Application.Management;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Identity.Domain;
using Solvix.Modules.Organization.Domain;
using Solvix.Modules.Tenancy.Application.Directory;
using Solvix.Modules.Tenancy.Domain;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public interface ITenantService
{
    Task<TenantDto> GetCurrentAsync(CancellationToken cancellationToken);

    Task<TenantOverviewDto> GetOverviewAsync(CancellationToken cancellationToken);

    Task<TenantDto> UpdateCurrentAsync(UpdateTenantRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Management of the *current* tenant only. There is deliberately no endpoint
/// that takes a tenant id: the tenant always comes from the validated token.
/// </summary>
internal sealed class TenantService(ISolvixDbContext db, ITenantContext tenantContext, ITenantDirectoryCache directoryCache) : ITenantService
{
    public async Task<TenantDto> GetCurrentAsync(CancellationToken cancellationToken) =>
        TenantDto.From(await LoadCurrentAsync(cancellationToken));

    public async Task<TenantOverviewDto> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var tenant = await LoadCurrentAsync(cancellationToken);
        var members = await db.Set<TenantMembership>().CountAsync(m => m.IsActive, cancellationToken);
        var branches = await db.Set<Branch>().CountAsync(b => b.IsActive, cancellationToken);
        var departments = await db.Set<Department>().CountAsync(d => d.IsActive, cancellationToken);
        return new TenantOverviewDto(TenantDto.From(tenant), members, branches, departments);
    }

    public async Task<TenantDto> UpdateCurrentAsync(UpdateTenantRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenant = await LoadCurrentAsync(cancellationToken);
        tenant.Rename(request.Name);
        await db.SaveChangesAsync(cancellationToken);
        await directoryCache.InvalidateAsync(tenant.Id, tenant.Slug, cancellationToken);
        return TenantDto.From(tenant);
    }

    private async Task<Tenant> LoadCurrentAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.RequireTenantId();
        return await db.Set<Tenant>().FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
            ?? throw new NotFoundException("Tenant", tenantId);
    }
}
