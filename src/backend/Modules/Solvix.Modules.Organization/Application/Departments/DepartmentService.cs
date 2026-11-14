namespace Solvix.Modules.Organization.Application.Departments;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Organization.Domain;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Tenancy;

public interface IDepartmentService
{
    Task<PagedResult<DepartmentDto>> ListAsync(DepartmentListQuery query, CancellationToken cancellationToken);

    Task<IReadOnlyList<DepartmentTreeNode>> TreeAsync(Guid branchId, CancellationToken cancellationToken);

    Task<DepartmentDto> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<DepartmentDto> CreateAsync(CreateDepartmentRequest request, CancellationToken cancellationToken);

    Task<DepartmentDto> UpdateAsync(Guid id, UpdateDepartmentRequest request, CancellationToken cancellationToken);

    Task<DepartmentDto> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed class DepartmentService(ISolvixDbContext db, ITenantContext tenantContext) : IDepartmentService
{
    public async Task<PagedResult<DepartmentDto>> ListAsync(DepartmentListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var departments = db.Set<Department>().AsNoTracking().Include(d => d.Branch).AsQueryable();
        if (query.BranchId is { } branchId)
        {
            departments = departments.Where(d => d.BranchId == branchId);
        }

        if (!query.IncludeInactive)
        {
            departments = departments.Where(d => d.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{query.Search.Trim()}%";
            departments = departments.Where(d => EF.Functions.ILike(d.Name, pattern) || EF.Functions.ILike(d.Code, pattern));
        }

        var total = await departments.LongCountAsync(cancellationToken);
        var items = await departments
            .OrderBy(d => d.Branch!.Name).ThenBy(d => d.Name)
            .Skip(page.Skip).Take(page.SafePageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<DepartmentDto>(items.Select(DepartmentDto.From).ToList(), page.SafePage, page.SafePageSize, total);
    }

    public async Task<IReadOnlyList<DepartmentTreeNode>> TreeAsync(Guid branchId, CancellationToken cancellationToken)
    {
        if (!await db.Set<Branch>().AnyAsync(b => b.Id == branchId, cancellationToken))
        {
            throw new NotFoundException("Branch", branchId);
        }

        var all = await db.Set<Department>().AsNoTracking()
            .Where(d => d.BranchId == branchId)
            .OrderBy(d => d.Name)
            .ToListAsync(cancellationToken);

        var byParent = all.ToLookup(d => d.ParentDepartmentId);
        return Build(null, 0);

        List<DepartmentTreeNode> Build(Guid? parentId, int depth)
        {
            if (depth > Department.MaxDepth)
            {
                return [];
            }

            return byParent[parentId]
                .Select(d => new DepartmentTreeNode(d.Id, d.Name, d.Code, d.IsActive, Build(d.Id, depth + 1)))
                .ToList();
        }
    }

    public async Task<DepartmentDto> GetAsync(Guid id, CancellationToken cancellationToken) =>
        DepartmentDto.From(await FindAsync(id, cancellationToken));

    public async Task<DepartmentDto> CreateAsync(CreateDepartmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenantId = tenantContext.RequireTenantId();

        // Tenant-filtered lookups: a branch/parent id from another tenant simply does not exist here.
        var branch = await db.Set<Branch>().FirstOrDefaultAsync(b => b.Id == request.BranchId, cancellationToken)
            ?? throw new NotFoundException("Branch", request.BranchId);

        if (request.ParentDepartmentId is { } parentId)
        {
            await EnsureParentAsync(parentId, branch.Id, cancellationToken);
        }

        var code = Department.NormalizeCode(request.Code);
        if (await db.Set<Department>().AnyAsync(d => d.BranchId == branch.Id && d.Code == code, cancellationToken))
        {
            throw new ConflictException($"A department with code '{code}' already exists in branch '{branch.Code}'.");
        }

        var department = Department.Create(tenantId, branch.Id, request.ParentDepartmentId, request.Name, code);
        department.Update(request.Name, code, request.Description);
        db.Set<Department>().Add(department);
        await db.SaveChangesAsync(cancellationToken);

        return DepartmentDto.From(await FindAsync(department.Id, cancellationToken));
    }

    public async Task<DepartmentDto> UpdateAsync(Guid id, UpdateDepartmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var department = await FindAsync(id, cancellationToken);
        var code = Department.NormalizeCode(request.Code);
        if (code != department.Code
            && await db.Set<Department>().AnyAsync(d => d.BranchId == department.BranchId && d.Code == code && d.Id != id, cancellationToken))
        {
            throw new ConflictException($"A department with code '{code}' already exists in this branch.");
        }

        if (request.ParentDepartmentId != department.ParentDepartmentId)
        {
            if (request.ParentDepartmentId is { } parentId)
            {
                await EnsureParentAsync(parentId, department.BranchId, cancellationToken);
                await EnsureNoCycleAsync(department.Id, parentId, cancellationToken);
            }

            department.MoveUnder(request.ParentDepartmentId);
        }

        department.Update(request.Name, code, request.Description);
        await db.SaveChangesAsync(cancellationToken);
        return DepartmentDto.From(department);
    }

    public async Task<DepartmentDto> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken)
    {
        var department = await FindAsync(id, cancellationToken);
        if (isActive)
        {
            department.Activate();
        }
        else
        {
            department.Deactivate();
        }

        await db.SaveChangesAsync(cancellationToken);
        return DepartmentDto.From(department);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var department = await FindAsync(id, cancellationToken);
        if (await db.Set<Department>().AnyAsync(d => d.ParentDepartmentId == id, cancellationToken))
        {
            throw new ConflictException("The department still has child departments.");
        }

        db.Set<Department>().Remove(department);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<Department> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Set<Department>().Include(d => d.Branch).FirstOrDefaultAsync(d => d.Id == id, cancellationToken)
        ?? throw new NotFoundException("Department", id);

    private async Task EnsureParentAsync(Guid parentId, Guid branchId, CancellationToken cancellationToken)
    {
        var parent = await db.Set<Department>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == parentId, cancellationToken)
            ?? throw new NotFoundException("Parent department", parentId);
        if (parent.BranchId != branchId)
        {
            throw new ValidationException("parentDepartmentId", "The parent department must belong to the same branch.");
        }
    }

    private async Task EnsureNoCycleAsync(Guid departmentId, Guid newParentId, CancellationToken cancellationToken)
    {
        var cursor = (Guid?)newParentId;
        var hops = 0;
        while (cursor is { } current)
        {
            if (current == departmentId)
            {
                throw new ValidationException("parentDepartmentId", "Moving the department here would create a cycle.");
            }

            if (++hops > Department.MaxDepth)
            {
                throw new ValidationException("parentDepartmentId", $"Department hierarchies are limited to {Department.MaxDepth} levels.");
            }

            cursor = await db.Set<Department>().AsNoTracking()
                .Where(d => d.Id == current)
                .Select(d => d.ParentDepartmentId)
                .FirstOrDefaultAsync(cancellationToken);
        }
    }
}
