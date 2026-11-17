namespace Solvix.Modules.Devices.Application.Enrollment;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Devices.Domain;
using Solvix.SharedKernel.Auditing;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Directory;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public interface IDeploymentProfileService
{
    /// <summary>Profiles of the workspace; a "Default" profile is created on first use so there is always one to download.</summary>
    Task<IReadOnlyList<DeploymentProfileDto>> ListAsync(CancellationToken cancellationToken);

    Task<DeploymentProfileDto> CreateAsync(SaveDeploymentProfileRequest request, CancellationToken cancellationToken);

    Task<DeploymentProfileDto> UpdateAsync(Guid id, SaveDeploymentProfileRequest request, CancellationToken cancellationToken);

    Task<DeploymentProfileDto> SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken);

    /// <summary>Replaces the enrollment code: installers downloaded earlier stop working.</summary>
    Task<DeploymentProfileDto> RotateCodeAsync(Guid id, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed class DeploymentProfileService(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IOrganizationDirectory organization,
    IAuditTrail audit,
    IClock clock) : IDeploymentProfileService
{
    public const string DefaultProfileName = "Default";

    public async Task<IReadOnlyList<DeploymentProfileDto>> ListAsync(CancellationToken cancellationToken)
    {
        if (!await db.Set<DeploymentProfile>().AnyAsync(cancellationToken))
        {
            await CreateDefaultAsync(cancellationToken);
        }

        var profiles = await db.Set<DeploymentProfile>().AsNoTracking().OrderBy(p => p.Name).ToListAsync(cancellationToken);
        return await ToDtosAsync(profiles, cancellationToken);
    }

    public async Task<DeploymentProfileDto> CreateAsync(SaveDeploymentProfileRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var branchId = await ResolvePlacementAsync(request, cancellationToken);
        await ValidateGroupAsync(request.DeviceGroupId, cancellationToken);
        var profile = DeploymentProfile.Create(
            tenantContext.RequireTenantId(), request.Name, request.Description, branchId, request.DepartmentId, request.DeviceGroupId, currentUser.RequireUserId());
        await EnsureUniqueNameAsync(profile.NormalizedName, null, cancellationToken);
        db.Set<DeploymentProfile>().Add(profile);
        audit.Record("deployment_profile.created", "DeploymentProfile", profile.Id, $"Deployment profile '{profile.Name}' created", new { profile.Name, branchId, request.DepartmentId, request.DeviceGroupId });
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync([profile], cancellationToken))[0];
    }

    public async Task<DeploymentProfileDto> UpdateAsync(Guid id, SaveDeploymentProfileRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profile = await FindAsync(id, cancellationToken);
        var branchId = await ResolvePlacementAsync(request, cancellationToken);
        await ValidateGroupAsync(request.DeviceGroupId, cancellationToken);
        await EnsureUniqueNameAsync(DeploymentProfile.Normalize(request.Name ?? string.Empty), id, cancellationToken);
        profile.Update(request.Name!, request.Description, branchId, request.DepartmentId, request.DeviceGroupId);
        audit.Record("deployment_profile.updated", "DeploymentProfile", id, $"Deployment profile '{profile.Name}' updated", new { profile.Name, branchId, request.DepartmentId, request.DeviceGroupId });
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync([profile], cancellationToken))[0];
    }

    public async Task<DeploymentProfileDto> SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken)
    {
        var profile = await FindAsync(id, cancellationToken);
        profile.SetEnabled(enabled);
        audit.Record(enabled ? "deployment_profile.enabled" : "deployment_profile.disabled", "DeploymentProfile", id, $"Deployment profile '{profile.Name}' {(enabled ? "enabled" : "disabled")}");
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync([profile], cancellationToken))[0];
    }

    public async Task<DeploymentProfileDto> RotateCodeAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await FindAsync(id, cancellationToken);
        profile.RotateCode();
        audit.Record("deployment_profile.code_rotated", "DeploymentProfile", id, $"Enrollment code of '{profile.Name}' rotated; earlier installers no longer work", new { profile.CodeVersion });
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync([profile], cancellationToken))[0];
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await FindAsync(id, cancellationToken);
        audit.Record("deployment_profile.deleted", "DeploymentProfile", id, $"Deployment profile '{profile.Name}' deleted; its open requests were discarded");
        db.Set<DeploymentProfile>().Remove(profile); // requests made through it are removed by the database (cascade)
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task CreateDefaultAsync(CancellationToken cancellationToken)
    {
        db.Set<DeploymentProfile>().Add(DeploymentProfile.Create(
            tenantContext.RequireTenantId(), DefaultProfileName, "Computers without a specific branch or department.", null, null, null, currentUser.RequireUserId()));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another request created it first (unique name): that is fine. The caller only reads from here on.
        }
    }

    private async Task<Guid?> ResolvePlacementAsync(SaveDeploymentProfileRequest request, CancellationToken cancellationToken)
    {
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

        return branchId;
    }

    private async Task ValidateGroupAsync(Guid? groupId, CancellationToken cancellationToken)
    {
        if (groupId is { } id && !await db.Set<DeviceGroup>().AnyAsync(g => g.Id == id, cancellationToken))
        {
            throw new NotFoundException("Device group", id);
        }
    }

    private async Task EnsureUniqueNameAsync(string normalizedName, Guid? exceptId, CancellationToken cancellationToken)
    {
        if (await db.Set<DeploymentProfile>().AnyAsync(p => p.NormalizedName == normalizedName && p.Id != exceptId, cancellationToken))
        {
            throw new ConflictException("A deployment profile with this name already exists.");
        }
    }

    private async Task<DeploymentProfile> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Set<DeploymentProfile>().FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
        ?? throw new NotFoundException("Deployment profile", id);

    private async Task<IReadOnlyList<DeploymentProfileDto>> ToDtosAsync(IReadOnlyList<DeploymentProfile> profiles, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var ids = profiles.Select(p => p.Id).ToList();
        var pending = await db.Set<PendingEnrollment>().AsNoTracking()
            .Where(p => ids.Contains(p.ProfileId) && p.Status == PendingEnrollmentStatus.Pending && p.ExpiresAtUtc > now)
            .GroupBy(p => p.ProfileId)
            .Select(g => new { ProfileId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ProfileId, x => x.Count, cancellationToken);
        var enrolled = await db.Set<Device>().AsNoTracking()
            .Where(d => d.EnrollmentTokenId != null && ids.Contains(d.EnrollmentTokenId.Value))
            .GroupBy(d => d.EnrollmentTokenId!.Value)
            .Select(g => new { ProfileId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ProfileId, x => x.Count, cancellationToken);
        var branches = await organization.FindBranchesAsync(profiles.Select(p => p.BranchId).OfType<Guid>().Distinct().ToList(), cancellationToken);
        var departments = await organization.FindDepartmentsAsync(profiles.Select(p => p.DepartmentId).OfType<Guid>().Distinct().ToList(), cancellationToken);
        var groupIds = profiles.Select(p => p.DeviceGroupId).OfType<Guid>().Distinct().ToList();
        var groups = groupIds.Count == 0
            ? []
            : await db.Set<DeviceGroup>().AsNoTracking().Where(g => groupIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken);

        return profiles.Select(p => new DeploymentProfileDto(
            p.Id,
            p.Name,
            p.Description,
            p.BranchId,
            p.BranchId is { } b && branches.TryGetValue(b, out var branch) ? branch.Name : null,
            p.DepartmentId,
            p.DepartmentId is { } d && departments.TryGetValue(d, out var department) ? department.Name : null,
            p.DeviceGroupId,
            p.DeviceGroupId is { } g && groups.TryGetValue(g, out var groupName) ? groupName : null,
            p.IsEnabled,
            p.CodeVersion,
            pending.GetValueOrDefault(p.Id),
            enrolled.GetValueOrDefault(p.Id),
            p.LastRequestAtUtc,
            p.CreatedAtUtc)).ToList();
    }
}
