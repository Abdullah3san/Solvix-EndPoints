namespace Solvix.Modules.Identity.Application.Roles;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Identity.Application.Access;
using Solvix.Modules.Identity.Application.Audit;
using Solvix.Modules.Identity.Domain;
using Solvix.SharedKernel.Authorization;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public interface IRoleService
{
    Task<IReadOnlyList<RoleDto>> ListAsync(CancellationToken cancellationToken);

    Task<RoleDto> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<RoleDto> CreateAsync(SaveRoleRequest request, CancellationToken cancellationToken);

    Task<RoleDto> UpdateAsync(Guid id, SaveRoleRequest request, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);

    IReadOnlyList<PermissionDto> ListPermissions();
}

/// <summary>
/// Roles of the CURRENT tenant. Built-in roles are read-only; custom roles can
/// be created, edited and deleted, but never to contain a permission the editor
/// does not hold: otherwise "may edit roles" would silently mean "may do anything".
/// </summary>
internal sealed class RoleService(
    ISolvixDbContext db,
    IPrivilegeGuard guard,
    IAccessCache accessCache,
    IAuthAuditLog audit,
    ITenantContext tenantContext,
    ICurrentUser currentUser) : IRoleService
{
    public async Task<IReadOnlyList<RoleDto>> ListAsync(CancellationToken cancellationToken)
    {
        var roles = await db.Set<Role>().AsNoTracking().Include(r => r.Permissions).AsSplitQuery().ToListAsync(cancellationToken);
        var counts = await db.Set<MembershipRole>()
            .GroupBy(r => r.RoleId)
            .Select(g => new { RoleId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RoleId, x => x.Count, cancellationToken);

        // Built-in roles first, in catalogue order; then custom roles by name.
        var order = SystemRoles.All.Select((r, i) => (r.Key, i)).ToDictionary(x => x.Key, x => x.i, StringComparer.Ordinal);
        return [.. roles
            .OrderBy(r => r.SystemKey is not null && order.TryGetValue(r.SystemKey, out var i) ? i : int.MaxValue)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => ToDto(r, counts.GetValueOrDefault(r.Id)))];
    }

    public async Task<RoleDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var role = await LoadAsync(id, cancellationToken);
        return ToDto(role, await MemberCountAsync(id, cancellationToken));
    }

    public async Task<RoleDto> CreateAsync(SaveRoleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenantId = tenantContext.RequireTenantId();
        await guard.EnsureCanGrantAsync(PermissionCatalog.Expand(request.Permissions), cancellationToken);
        await EnsureNameIsFreeAsync(request.Name, exceptRoleId: null, cancellationToken);

        var role = Role.CreateCustom(tenantId, request.Name, request.Description, request.Permissions);
        db.Set<Role>().Add(role);
        audit.Record(
            AuditEvents.RoleCreated, AuditOutcome.Success, tenantId,
            detail: $"role={role.Name} permissions=[{string.Join(",", role.PermissionKeys().Order(StringComparer.Ordinal))}]",
            actorUserId: currentUser.UserId);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(role, 0);
    }

    public async Task<RoleDto> UpdateAsync(Guid id, SaveRoleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var role = await LoadAsync(id, cancellationToken);
        role.EnsureCustom();

        // The editor must hold what the role grants today (no editing roles above your level)
        // and what it will grant afterwards (no adding what you do not have).
        var before = role.PermissionKeys();
        await guard.EnsureCanGrantAsync(before.Union(PermissionCatalog.Expand(request.Permissions)), cancellationToken);
        await EnsureNameIsFreeAsync(request.Name, role.Id, cancellationToken);

        role.Update(request.Name, request.Description, request.Permissions);
        audit.Record(
            AuditEvents.RoleUpdated, AuditOutcome.Success, role.TenantId,
            detail: $"role={role.Name} permissions=[{string.Join(",", role.PermissionKeys().Order(StringComparer.Ordinal))}]",
            actorUserId: currentUser.UserId);
        await db.SaveChangesAsync(cancellationToken);

        // Everyone holding the role gets the new permission set on their next request.
        var holders = await db.Set<MembershipRole>()
            .Where(r => r.RoleId == role.Id)
            .Join(db.Set<TenantMembership>(), r => r.MembershipId, m => m.Id, (r, m) => m.UserId)
            .ToListAsync(cancellationToken);
        foreach (var userId in holders)
        {
            await accessCache.InvalidateUserAsync(role.TenantId, userId, cancellationToken);
        }

        return ToDto(role, holders.Count);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var role = await LoadAsync(id, cancellationToken);
        role.EnsureCustom();
        await guard.EnsureCanGrantAsync(role.PermissionKeys(), cancellationToken);

        if (await MemberCountAsync(id, cancellationToken) > 0)
        {
            throw new ConflictException("This role is still assigned to users. Remove it from them first.", "role_in_use");
        }

        var invitationRoles = await db.Set<UserInvitationRole>().Where(r => r.RoleId == id).ToListAsync(cancellationToken);
        if (invitationRoles.Count > 0)
        {
            var invitationIds = invitationRoles.Select(r => r.InvitationId).Distinct().ToList();
            var pending = await db.Set<UserInvitation>()
                .AnyAsync(i => invitationIds.Contains(i.Id) && i.AcceptedAtUtc == null && i.RevokedAtUtc == null, cancellationToken);
            if (pending)
            {
                throw new ConflictException("This role is part of a pending invitation. Revoke the invitation first.", "role_in_use");
            }

            // References from invitations that are already accepted or revoked are history only.
            db.Set<UserInvitationRole>().RemoveRange(invitationRoles);
        }

        db.Set<Role>().Remove(role);
        audit.Record(AuditEvents.RoleDeleted, AuditOutcome.Success, role.TenantId, detail: $"role={role.Name}", actorUserId: currentUser.UserId);
        await db.SaveChangesAsync(cancellationToken);
    }

    public IReadOnlyList<PermissionDto> ListPermissions() =>
        [.. PermissionCatalog.All.Select(p => new PermissionDto(p.Key, p.Group, p.Description, p.Implies))];

    private async Task EnsureNameIsFreeAsync(string name, Guid? exceptRoleId, CancellationToken cancellationToken)
    {
        var normalized = Role.NormalizeName(name);
        if (await db.Set<Role>().AnyAsync(r => r.NormalizedName == normalized && r.Id != exceptRoleId, cancellationToken))
        {
            throw new ConflictException($"A role named '{name.Trim()}' already exists.", "role_name_taken");
        }
    }

    private Task<int> MemberCountAsync(Guid roleId, CancellationToken cancellationToken) =>
        db.Set<MembershipRole>().CountAsync(r => r.RoleId == roleId, cancellationToken);

    private async Task<Role> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Set<Role>().Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
        ?? throw new NotFoundException("Role", id);

    private static RoleDto ToDto(Role role, int memberCount) =>
        new(
            role.Id,
            role.Name,
            role.Description,
            role.IsSystem,
            role.SystemKey,
            [.. role.PermissionKeys().Order(StringComparer.Ordinal)],
            memberCount,
            role.CreatedAtUtc,
            role.UpdatedAtUtc);
}
