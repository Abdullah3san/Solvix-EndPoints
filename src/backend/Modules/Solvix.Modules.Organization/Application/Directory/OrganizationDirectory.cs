namespace Solvix.Modules.Organization.Application.Directory;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Organization.Domain;
using Solvix.SharedKernel.Directory;
using Solvix.SharedKernel.Persistence;

/// <summary>Tenant-scoped branch/department lookups for other modules.</summary>
internal sealed class OrganizationDirectory(ISolvixDbContext db) : IOrganizationDirectory
{
    public async Task<IReadOnlyDictionary<Guid, BranchSummary>> FindBranchesAsync(IReadOnlyCollection<Guid> branchIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(branchIds);
        if (branchIds.Count == 0)
        {
            return new Dictionary<Guid, BranchSummary>();
        }

        var ids = branchIds.Distinct().ToList();
        return await db.Set<Branch>().AsNoTracking()
            .Where(b => ids.Contains(b.Id))
            .Select(b => new BranchSummary(b.Id, b.Name, b.Code, b.IsActive))
            .ToDictionaryAsync(b => b.Id, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, DepartmentSummary>> FindDepartmentsAsync(IReadOnlyCollection<Guid> departmentIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(departmentIds);
        if (departmentIds.Count == 0)
        {
            return new Dictionary<Guid, DepartmentSummary>();
        }

        var ids = departmentIds.Distinct().ToList();
        return await db.Set<Department>().AsNoTracking()
            .Where(d => ids.Contains(d.Id))
            .Select(d => new DepartmentSummary(d.Id, d.BranchId, d.Name, d.Code, d.IsActive))
            .ToDictionaryAsync(d => d.Id, cancellationToken);
    }
}
