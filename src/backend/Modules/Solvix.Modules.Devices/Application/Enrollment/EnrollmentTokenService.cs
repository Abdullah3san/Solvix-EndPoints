namespace Solvix.Modules.Devices.Application.Enrollment;

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvix.Modules.Devices.Configuration;
using Solvix.Modules.Devices.Domain;
using Solvix.Modules.Devices.Security;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Directory;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Tenancy;

public interface IEnrollmentTokenService
{
    Task<PagedResult<EnrollmentTokenDto>> ListAsync(EnrollmentTokenListQuery query, CancellationToken cancellationToken);

    Task<CreatedEnrollmentTokenDto> CreateAsync(CreateEnrollmentTokenRequest request, CancellationToken cancellationToken);

    Task<EnrollmentTokenDto> RevokeAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed class EnrollmentTokenService(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IOrganizationDirectory organization,
    IOptions<DevicesOptions> options,
    IClock clock) : IEnrollmentTokenService
{
    public async Task<PagedResult<EnrollmentTokenDto>> ListAsync(EnrollmentTokenListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var now = clock.UtcNow;
        var tokens = db.Set<EnrollmentToken>().AsNoTracking();
        if (!query.IncludeInactive)
        {
            tokens = tokens.Where(t => t.RevokedAtUtc == null && t.ExpiresAtUtc > now && t.UseCount < t.MaxUses);
        }

        var total = await tokens.LongCountAsync(cancellationToken);
        var items = await tokens
            .OrderByDescending(t => t.CreatedAtUtc)
            .Skip(page.Skip).Take(page.SafePageSize)
            .ToListAsync(cancellationToken);
        var names = await NamesAsync(items, cancellationToken);
        return new PagedResult<EnrollmentTokenDto>(
            items.Select(t => ToDto(t, names, now)).ToList(), page.SafePage, page.SafePageSize, total);
    }

    public async Task<CreatedEnrollmentTokenDto> CreateAsync(CreateEnrollmentTokenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenantId = tenantContext.RequireTenantId();
        var settings = options.Value.Enrollment;
        var lifetime = request.LifetimeHours ?? settings.DefaultTokenLifetimeHours;
        if (lifetime < 1 || lifetime > settings.MaxTokenLifetimeHours)
        {
            throw new ValidationException("lifetimeHours", $"Must be between 1 and {settings.MaxTokenLifetimeHours} hours.");
        }

        var maxUses = request.MaxUses ?? 1;
        if (maxUses < 1 || maxUses > settings.MaxTokenUses)
        {
            throw new ValidationException("maxUses", $"Must be between 1 and {settings.MaxTokenUses}.");
        }

        if (request.DeviceGroupId is { } groupId && !await db.Set<DeviceGroup>().AnyAsync(g => g.Id == groupId, cancellationToken))
        {
            throw new NotFoundException("Device group", groupId);
        }

        // Placement: a department implies its branch; both must exist in this tenant (lookups run under RLS).
        var branchId = request.BranchId;
        if (request.DepartmentId is { } departmentId)
        {
            var departments = await organization.FindDepartmentsAsync([departmentId], cancellationToken);
            if (!departments.TryGetValue(departmentId, out var department))
            {
                throw new NotFoundException("Department", departmentId);
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

        var now = clock.UtcNow;
        var tokenId = Guid.CreateVersion7();
        var (plaintext, secret) = EnrollmentTokenCodec.Create(tenantId, tokenId);
        try
        {
            var token = EnrollmentToken.Create(
                tenantId, tokenId, request.Name, secret, now.AddHours(lifetime), maxUses, currentUser.RequireUserId(), request.DeviceGroupId, branchId, request.DepartmentId);
            db.Set<EnrollmentToken>().Add(token);
            await db.SaveChangesAsync(cancellationToken);
            var names = await NamesAsync([token], cancellationToken);
            return new CreatedEnrollmentTokenDto(ToDto(token, names, now), plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public async Task<EnrollmentTokenDto> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        var token = await db.Set<EnrollmentToken>().FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new NotFoundException("Enrollment token", id);
        var now = clock.UtcNow;
        token.Revoke(now);
        await db.SaveChangesAsync(cancellationToken);
        var names = await NamesAsync([token], cancellationToken);
        return ToDto(token, names, now);
    }

    private async Task<TokenNames> NamesAsync(IReadOnlyCollection<EnrollmentToken> tokens, CancellationToken cancellationToken)
    {
        var groupIds = tokens.Select(t => t.DeviceGroupId).OfType<Guid>().Distinct().ToList();
        var groups = groupIds.Count == 0
            ? []
            : await db.Set<DeviceGroup>().AsNoTracking().Where(g => groupIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken);
        var branches = await organization.FindBranchesAsync(tokens.Select(t => t.BranchId).OfType<Guid>().Distinct().ToList(), cancellationToken);
        var departments = await organization.FindDepartmentsAsync(tokens.Select(t => t.DepartmentId).OfType<Guid>().Distinct().ToList(), cancellationToken);
        return new TokenNames(groups, branches, departments);
    }

    private static EnrollmentTokenDto ToDto(EnrollmentToken t, TokenNames names, DateTimeOffset now) =>
        new(
            t.Id,
            t.Name,
            t.StateAt(now),
            t.ExpiresAtUtc,
            t.MaxUses,
            t.UseCount,
            t.LastUsedAtUtc,
            t.RevokedAtUtc,
            t.DeviceGroupId,
            t.DeviceGroupId is { } g && names.Groups.TryGetValue(g, out var groupName) ? groupName : null,
            t.BranchId,
            t.BranchId is { } b && names.Branches.TryGetValue(b, out var branch) ? branch.Name : null,
            t.DepartmentId,
            t.DepartmentId is { } d && names.Departments.TryGetValue(d, out var department) ? department.Name : null,
            t.CreatedByUserId,
            t.CreatedAtUtc);

    private sealed record TokenNames(
        IReadOnlyDictionary<Guid, string> Groups,
        IReadOnlyDictionary<Guid, BranchSummary> Branches,
        IReadOnlyDictionary<Guid, DepartmentSummary> Departments);
}
