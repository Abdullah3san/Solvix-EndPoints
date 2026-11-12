namespace Solvix.Modules.Organization.Domain;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Guards;

/// <summary>
/// An organisational unit inside a branch. Departments can be nested through
/// <see cref="ParentDepartmentId"/>; the parent must belong to the same branch.
/// Codes are unique per branch.
/// </summary>
public sealed class Department : TenantEntity
{
    public const int NameMaxLength = 200;
    public const int CodeMaxLength = 32;
    public const int DescriptionMaxLength = 1000;
    public const int MaxDepth = 8;

    private Department()
    {
    }

    private Department(Guid tenantId, Guid branchId, Guid? parentDepartmentId, string name, string code)
        : base(tenantId)
    {
        BranchId = branchId;
        ParentDepartmentId = parentDepartmentId;
        Name = name;
        Code = code;
        IsActive = true;
    }

    public Guid BranchId { get; private set; }

    public Guid? ParentDepartmentId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string Code { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public bool IsActive { get; private set; }

    public Branch? Branch { get; private set; }

    public Department? ParentDepartment { get; private set; }

    public static Department Create(Guid tenantId, Guid branchId, Guid? parentDepartmentId, string name, string code) =>
        new(tenantId, branchId, parentDepartmentId, Guard.NotNullOrWhiteSpace(name, "name", NameMaxLength), NormalizeCode(code));

    public static string NormalizeCode(string code) =>
        Guard.NotNullOrWhiteSpace(code, "code", CodeMaxLength).ToUpperInvariant();

    public void Update(string name, string code, string? description)
    {
        Name = Guard.NotNullOrWhiteSpace(name, "name", NameMaxLength);
        Code = NormalizeCode(code);
        Description = Guard.Optional(description, "description", DescriptionMaxLength);
    }

    public void MoveUnder(Guid? parentDepartmentId)
    {
        if (parentDepartmentId == Id)
        {
            throw new SharedKernel.Exceptions.ValidationException("parentDepartmentId", "A department cannot be its own parent.");
        }

        ParentDepartmentId = parentDepartmentId;
    }

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;
}
