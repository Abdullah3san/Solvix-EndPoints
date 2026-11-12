namespace Solvix.Modules.Organization.Application.Departments;

using Solvix.Modules.Organization.Domain;

public sealed record DepartmentDto(
    Guid Id,
    Guid BranchId,
    string BranchName,
    Guid? ParentDepartmentId,
    string Name,
    string Code,
    string? Description,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public static DepartmentDto From(Department d) => new(
        d.Id, d.BranchId, d.Branch?.Name ?? string.Empty, d.ParentDepartmentId, d.Name, d.Code, d.Description, d.IsActive,
        d.CreatedAtUtc, d.UpdatedAtUtc);
}

public sealed record CreateDepartmentRequest(Guid BranchId, Guid? ParentDepartmentId, string Name, string Code, string? Description);

public sealed record UpdateDepartmentRequest(Guid? ParentDepartmentId, string Name, string Code, string? Description);

public sealed record DepartmentListQuery(Guid? BranchId, string? Search, bool IncludeInactive, int Page = 1, int PageSize = 25);

public sealed record DepartmentTreeNode(Guid Id, string Name, string Code, bool IsActive, IReadOnlyList<DepartmentTreeNode> Children);
