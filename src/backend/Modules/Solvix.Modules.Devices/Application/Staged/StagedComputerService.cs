namespace Solvix.Modules.Devices.Application.Staged;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Devices.Domain;
using Solvix.SharedKernel.Directory;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Tenancy;

public interface IStagedComputerService
{
    Task<PagedResult<StagedComputerDto>> ListAsync(StagedComputerListQuery query, CancellationToken cancellationToken);

    Task<StagedComputerSummaryDto> GetSummaryAsync(CancellationToken cancellationToken);

    Task<StagedComputerDto> CreateAsync(CreateStagedComputerRequest request, CancellationToken cancellationToken);

    Task<ImportStagedComputersResult> ImportAsync(ImportStagedComputersRequest request, CancellationToken cancellationToken);

    Task<int> RemoveAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
}

internal sealed class StagedComputerService(ISolvixDbContext db, ITenantContext tenantContext, IOrganizationDirectory organization) : IStagedComputerService
{
    public async Task<PagedResult<StagedComputerDto>> ListAsync(StagedComputerListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var staged = db.Set<StagedComputer>().AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{query.Search.Trim()}%";
            staged = staged.Where(s => EF.Functions.ILike(s.Hostname, pattern));
        }

        if (query.BranchId is { } branchId)
        {
            staged = staged.Where(s => s.BranchId == branchId);
        }

        staged = query.Status switch
        {
            StagedComputerStatus.Pending => staged.Where(s => !Devices().Any(d => d.Hostname.ToUpper() == s.NormalizedHostname)),
            StagedComputerStatus.Enrolled => staged.Where(s => Devices().Any(d => d.Hostname.ToUpper() == s.NormalizedHostname && d.Status == DeviceStatus.Active)),
            StagedComputerStatus.Retired => staged.Where(s =>
                Devices().Any(d => d.Hostname.ToUpper() == s.NormalizedHostname) && !Devices().Any(d => d.Hostname.ToUpper() == s.NormalizedHostname && d.Status == DeviceStatus.Active)),
            _ => staged,
        };

        var total = await staged.LongCountAsync(cancellationToken);
        var rows = await staged.OrderBy(s => s.NormalizedHostname).ThenBy(s => s.Id).Skip(page.Skip).Take(page.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<StagedComputerDto>(await DescribeAsync(rows, cancellationToken), page.SafePage, page.SafePageSize, total);
    }

    public async Task<StagedComputerSummaryDto> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var staged = db.Set<StagedComputer>().AsNoTracking();
        var total = await staged.CountAsync(cancellationToken);
        var pending = await staged.CountAsync(s => !Devices().Any(d => d.Hostname.ToUpper() == s.NormalizedHostname), cancellationToken);
        var enrolled = await staged.CountAsync(s => Devices().Any(d => d.Hostname.ToUpper() == s.NormalizedHostname && d.Status == DeviceStatus.Active), cancellationToken);
        return new StagedComputerSummaryDto(total, pending, enrolled, total - pending - enrolled);
    }

    public async Task<StagedComputerDto> CreateAsync(CreateStagedComputerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (branchId, departmentId) = await ResolvePlacementAsync(request.BranchId, request.DepartmentId, cancellationToken);
        var staged = StagedComputer.Create(tenantContext.RequireTenantId(), request.Hostname, branchId, departmentId, StagedComputerSource.Manual);
        if (await db.Set<StagedComputer>().AnyAsync(s => s.NormalizedHostname == staged.NormalizedHostname, cancellationToken))
        {
            throw new ConflictException($"'{staged.Hostname}' is already in the list.");
        }

        db.Set<StagedComputer>().Add(staged);
        await db.SaveChangesAsync(cancellationToken);
        return (await DescribeAsync([staged], cancellationToken))[0];
    }

    public async Task<ImportStagedComputersResult> ImportAsync(ImportStagedComputersRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (branchId, departmentId) = await ResolvePlacementAsync(request.BranchId, request.DepartmentId, cancellationToken);
        var tenantId = tenantContext.RequireTenantId();

        var rejected = new List<ImportRejectedDto>();
        var candidates = new Dictionary<string, string>(StringComparer.Ordinal); // normalized -> as typed
        var duplicatesInInput = 0;
        foreach (var raw in request.Hostnames)
        {
            var value = (raw ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                continue; // blank lines are not an error
            }

            if (!StagedComputer.IsValidHostname(value))
            {
                rejected.Add(new ImportRejectedDto(value.Length > 80 ? value[..80] + "…" : value, "Not a valid hostname"));
                continue;
            }

            if (!candidates.TryAdd(StagedComputer.Normalize(value), value))
            {
                duplicatesInInput++;
            }
        }

        var keys = candidates.Keys.ToList();
        var existing = keys.Count == 0
            ? []
            : await db.Set<StagedComputer>().AsNoTracking().Where(s => keys.Contains(s.NormalizedHostname)).Select(s => s.NormalizedHostname).ToListAsync(cancellationToken);
        var existingSet = existing.ToHashSet(StringComparer.Ordinal);

        var created = 0;
        foreach (var (normalized, hostname) in candidates)
        {
            if (existingSet.Contains(normalized))
            {
                continue;
            }

            db.Set<StagedComputer>().Add(StagedComputer.Create(tenantId, hostname, branchId, departmentId, StagedComputerSource.Import));
            created++;
        }

        if (created > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return new ImportStagedComputersResult(created, existingSet.Count + duplicatesInInput, rejected);
    }

    public async Task<int> RemoveAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var rows = await db.Set<StagedComputer>().Where(s => ids.Contains(s.Id)).ToListAsync(cancellationToken);
        db.Set<StagedComputer>().RemoveRange(rows);
        await db.SaveChangesAsync(cancellationToken);
        return rows.Count;
    }

    private IQueryable<Device> Devices() => db.Set<Device>().AsNoTracking();

    /// <summary>Same rules as device assignment: a department implies its branch and must belong to the chosen one.</summary>
    private async Task<(Guid? BranchId, Guid? DepartmentId)> ResolvePlacementAsync(Guid? branchId, Guid? departmentId, CancellationToken cancellationToken)
    {
        if (departmentId is { } dep)
        {
            var departments = await organization.FindDepartmentsAsync([dep], cancellationToken);
            if (!departments.TryGetValue(dep, out var department))
            {
                throw new NotFoundException("Department", dep);
            }

            if (branchId is not null && branchId != department.BranchId)
            {
                throw new ValidationException("departmentId", "The department does not belong to the selected branch.");
            }

            branchId = department.BranchId;
        }

        if (branchId is { } b && !(await organization.FindBranchesAsync([b], cancellationToken)).ContainsKey(b))
        {
            throw new NotFoundException("Branch", b);
        }

        return (branchId, departmentId);
    }

    private async Task<IReadOnlyList<StagedComputerDto>> DescribeAsync(IReadOnlyList<StagedComputer> rows, CancellationToken cancellationToken)
    {
        var names = rows.Select(r => r.NormalizedHostname).ToList();
        var devices = names.Count == 0
            ? []
            : await Devices().Where(d => names.Contains(d.Hostname.ToUpper())).Select(d => new { Key = d.Hostname.ToUpper(), d.Id, d.Status, d.EnrolledAtUtc }).ToListAsync(cancellationToken);
        var byName = devices.GroupBy(d => d.Key).ToDictionary(g => g.Key, g => g.OrderBy(d => d.Status).ThenByDescending(d => d.EnrolledAtUtc).First()); // an active device wins

        var branchIds = rows.Where(r => r.BranchId.HasValue).Select(r => r.BranchId!.Value).Distinct().ToList();
        var departmentIds = rows.Where(r => r.DepartmentId.HasValue).Select(r => r.DepartmentId!.Value).Distinct().ToList();
        var branches = branchIds.Count > 0 ? await organization.FindBranchesAsync(branchIds, cancellationToken) : null;
        var departments = departmentIds.Count > 0 ? await organization.FindDepartmentsAsync(departmentIds, cancellationToken) : null;

        return rows.Select(r =>
        {
            byName.TryGetValue(r.NormalizedHostname, out var device);
            var status = device is null ? StagedComputerStatus.Pending : device.Status == DeviceStatus.Active ? StagedComputerStatus.Enrolled : StagedComputerStatus.Retired;
            return new StagedComputerDto(
                r.Id,
                r.Hostname,
                r.BranchId,
                r.BranchId is { } b && branches is not null && branches.TryGetValue(b, out var branch) ? branch.Name : null,
                r.DepartmentId,
                r.DepartmentId is { } d && departments is not null && departments.TryGetValue(d, out var department) ? department.Name : null,
                r.Source,
                status,
                device?.Id,
                device?.EnrolledAtUtc,
                r.CreatedAtUtc);
        }).ToList();
    }
}
