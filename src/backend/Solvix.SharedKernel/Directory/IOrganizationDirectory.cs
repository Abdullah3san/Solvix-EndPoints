namespace Solvix.SharedKernel.Directory;

/// <summary>
/// Read-only view of the organisation structure of the current tenant, for
/// modules that reference branches or departments without depending on the
/// Organization module. Implemented by Organization; tenant-scoped like any query.
/// </summary>
public interface IOrganizationDirectory
{
    Task<IReadOnlyDictionary<Guid, BranchSummary>> FindBranchesAsync(IReadOnlyCollection<Guid> branchIds, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, DepartmentSummary>> FindDepartmentsAsync(IReadOnlyCollection<Guid> departmentIds, CancellationToken cancellationToken);
}

public sealed record BranchSummary(Guid Id, string Name, string Code, bool IsActive);

public sealed record DepartmentSummary(Guid Id, Guid BranchId, string Name, string Code, bool IsActive);
