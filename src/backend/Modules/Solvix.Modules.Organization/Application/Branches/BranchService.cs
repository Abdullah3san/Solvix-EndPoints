namespace Solvix.Modules.Organization.Application.Branches;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Organization.Domain;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Tenancy;

public interface IBranchService
{
    Task<PagedResult<BranchDto>> ListAsync(BranchListQuery query, CancellationToken cancellationToken);

    Task<BranchDto> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<BranchDto> CreateAsync(CreateBranchRequest request, CancellationToken cancellationToken);

    Task<BranchDto> UpdateAsync(Guid id, UpdateBranchRequest request, CancellationToken cancellationToken);

    Task<BranchDto> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed class BranchService(ISolvixDbContext db, ITenantContext tenantContext) : IBranchService
{
    public async Task<PagedResult<BranchDto>> ListAsync(BranchListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var branches = db.Set<Branch>().AsNoTracking();
        if (!query.IncludeInactive)
        {
            branches = branches.Where(b => b.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{query.Search.Trim()}%";
            branches = branches.Where(b => EF.Functions.ILike(b.Name, pattern) || EF.Functions.ILike(b.Code, pattern));
        }

        var total = await branches.LongCountAsync(cancellationToken);
        var items = await branches
            .OrderByDescending(b => b.IsHeadquarters).ThenBy(b => b.Name)
            .Skip(page.Skip).Take(page.SafePageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<BranchDto>(items.Select(BranchDto.From).ToList(), page.SafePage, page.SafePageSize, total);
    }

    public async Task<BranchDto> GetAsync(Guid id, CancellationToken cancellationToken) =>
        BranchDto.From(await FindAsync(id, cancellationToken));

    public async Task<BranchDto> CreateAsync(CreateBranchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenantId = tenantContext.RequireTenantId();
        var code = Branch.NormalizeCode(request.Code);
        if (await db.Set<Branch>().AnyAsync(b => b.Code == code, cancellationToken))
        {
            throw new ConflictException($"A branch with code '{code}' already exists.");
        }

        var branch = Branch.Create(tenantId, request.Name, code);
        branch.Update(request.Name, code, request.Phone, request.Email, request.TimeZone, request.Address?.ToDomain() ?? Address.Empty);
        if (request.IsHeadquarters)
        {
            await ClearHeadquartersAsync(cancellationToken);
            branch.MarkAsHeadquarters();
        }

        db.Set<Branch>().Add(branch);
        await db.SaveChangesAsync(cancellationToken);
        return BranchDto.From(branch);
    }

    public async Task<BranchDto> UpdateAsync(Guid id, UpdateBranchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var branch = await FindAsync(id, cancellationToken);
        var code = Branch.NormalizeCode(request.Code);
        if (code != branch.Code && await db.Set<Branch>().AnyAsync(b => b.Code == code && b.Id != id, cancellationToken))
        {
            throw new ConflictException($"A branch with code '{code}' already exists.");
        }

        branch.Update(request.Name, code, request.Phone, request.Email, request.TimeZone, request.Address?.ToDomain() ?? Address.Empty);
        if (request.IsHeadquarters && !branch.IsHeadquarters)
        {
            await ClearHeadquartersAsync(cancellationToken);
            branch.MarkAsHeadquarters();
        }
        else if (!request.IsHeadquarters && branch.IsHeadquarters)
        {
            branch.UnmarkHeadquarters();
        }

        await db.SaveChangesAsync(cancellationToken);
        return BranchDto.From(branch);
    }

    public async Task<BranchDto> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken)
    {
        var branch = await FindAsync(id, cancellationToken);
        if (isActive)
        {
            branch.Activate();
        }
        else
        {
            branch.Deactivate();
        }

        await db.SaveChangesAsync(cancellationToken);
        return BranchDto.From(branch);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var branch = await FindAsync(id, cancellationToken);
        if (await db.Set<Department>().AnyAsync(d => d.BranchId == id, cancellationToken))
        {
            throw new ConflictException("The branch still has departments. Delete or move them first.");
        }

        db.Set<Branch>().Remove(branch);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<Branch> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Set<Branch>().FirstOrDefaultAsync(b => b.Id == id, cancellationToken)
        ?? throw new NotFoundException("Branch", id);

    private async Task ClearHeadquartersAsync(CancellationToken cancellationToken)
    {
        var current = await db.Set<Branch>().Where(b => b.IsHeadquarters).ToListAsync(cancellationToken);
        foreach (var b in current)
        {
            b.UnmarkHeadquarters();
        }
    }
}
