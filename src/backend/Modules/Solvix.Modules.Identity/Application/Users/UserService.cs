namespace Solvix.Modules.Identity.Application.Users;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Identity.Application.Access;
using Solvix.Modules.Identity.Application.Audit;
using Solvix.Modules.Identity.Application.Auth;
using Solvix.Modules.Identity.Application.Sessions;
using Solvix.Modules.Identity.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Tenancy;

public interface IUserService
{
    Task<PagedResult<TenantUserDto>> ListAsync(UserListQuery query, CancellationToken cancellationToken);

    Task<TenantUserDto> GetAsync(Guid userId, CancellationToken cancellationToken);

    Task<TenantUserDto> SetRolesAsync(Guid userId, SetUserRolesRequest request, CancellationToken cancellationToken);

    Task<TenantUserDto> DeactivateAsync(Guid userId, CancellationToken cancellationToken);

    Task<TenantUserDto> ActivateAsync(Guid userId, CancellationToken cancellationToken);

    Task RemoveAsync(Guid userId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SessionDto>> ListSessionsAsync(Guid userId, CancellationToken cancellationToken);

    Task<SessionsRevokedDto> RevokeSessionsAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>
/// Administration of the members of the CURRENT tenant. Everything here runs in
/// the tenant scope of the request: a user id from another tenant simply does
/// not resolve (404). What a tenant controls is the membership: its roles, its
/// active flag and its sessions in this tenant. The account's password and MFA
/// belong to the person and cannot be touched from here.
/// </summary>
internal sealed class UserService(
    ISolvixDbContext db,
    IPrivilegeGuard guard,
    ISessionManager sessions,
    IAccessCache accessCache,
    IAuthAuditLog audit,
    ITenantContext tenantContext,
    ITenantDirectory tenantDirectory,
    ICurrentUser currentUser,
    IClock clock) : IUserService
{
    public async Task<PagedResult<TenantUserDto>> ListAsync(UserListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var memberships = db.Set<TenantMembership>().AsNoTracking();
        if (!query.IncludeInactive)
        {
            memberships = memberships.Where(m => m.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToUpperInvariant();
            memberships = memberships.Where(m => m.User!.NormalizedEmail.Contains(term) || m.User.DisplayName.ToUpper().Contains(term));
        }

        var total = await memberships.LongCountAsync(cancellationToken);
        var items = await memberships
            .Include(m => m.User)
            .Include(m => m.Roles).ThenInclude(r => r.Role)
            .OrderBy(m => m.CreatedAtUtc)
            .Skip(page.Skip)
            .Take(page.SafePageSize)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
        return new PagedResult<TenantUserDto>([.. items.Select(ToDto)], page.SafePage, page.SafePageSize, total);
    }

    public async Task<TenantUserDto> GetAsync(Guid userId, CancellationToken cancellationToken) =>
        ToDto(await LoadAsync(userId, cancellationToken));

    public Task<TenantUserDto> SetRolesAsync(Guid userId, SetUserRolesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return guard.SerializedAsync(
            async ct =>
            {
                var membership = await LoadAsync(userId, ct);
                await guard.EnsureCanManageAsync(membership, ct);

                var requested = request.RoleIds.Distinct().ToList();
                var roles = await db.Set<Role>().Include(r => r.Permissions).Where(r => requested.Contains(r.Id)).ToListAsync(ct);
                if (roles.Count != requested.Count)
                {
                    // Also what a role id of another tenant looks like from here: it does not exist.
                    throw new ValidationException("roleIds", "One or more roles do not exist.");
                }

                // Rule 1: the new assignment may not contain anything the actor lacks.
                await guard.EnsureCanGrantAsync(roles.SelectMany(r => r.PermissionKeys()), ct);

                var losesSuperAdministrator =
                    membership.Roles.Any(r => r.Role!.SystemKey == SystemRoles.SuperAdministratorKey)
                    && roles.TrueForAll(r => r.SystemKey != SystemRoles.SuperAdministratorKey);
                if (losesSuperAdministrator)
                {
                    await guard.EnsureNotLastSuperAdministratorAsync(membership, ct);
                }

                var before = string.Join(",", membership.Roles.Select(r => r.Role!.Name).Order(StringComparer.Ordinal));
                membership.SetRoles(requested);
                var after = string.Join(",", roles.Select(r => r.Name).Order(StringComparer.Ordinal));
                audit.Record(
                    AuditEvents.UserRolesChanged,
                    AuditOutcome.Success,
                    membership.TenantId,
                    membership.UserId,
                    membership.User!.Email,
                    detail: $"from=[{before}] to=[{after}]",
                    actorUserId: currentUser.UserId);
                await db.SaveChangesAsync(ct);
                await accessCache.InvalidateUserAsync(membership.TenantId, membership.UserId, ct);
                return ToDto(await LoadAsync(userId, ct));
            },
            cancellationToken);
    }

    public Task<TenantUserDto> DeactivateAsync(Guid userId, CancellationToken cancellationToken) =>
        guard.SerializedAsync(
            async ct =>
            {
                var membership = await LoadAsync(userId, ct);
                await guard.EnsureCanManageAsync(membership, ct);
                if (!membership.IsActive)
                {
                    return ToDto(membership);
                }

                await guard.EnsureNotLastSuperAdministratorAsync(membership, ct);
                membership.Deactivate(clock.UtcNow);
                var revoked = await sessions.RevokeAllAsync(
                    membership.UserId, membership.TenantId, exceptSessionId: null, SessionRevocationReasons.MembershipDeactivated, ct);
                audit.Record(
                    AuditEvents.UserDeactivated,
                    AuditOutcome.Success,
                    membership.TenantId,
                    membership.UserId,
                    membership.User!.Email,
                    detail: $"sessions_revoked={revoked}",
                    actorUserId: currentUser.UserId);
                await db.SaveChangesAsync(ct);
                await accessCache.InvalidateUserAsync(membership.TenantId, membership.UserId, ct);
                return ToDto(membership);
            },
            cancellationToken);

    public Task<TenantUserDto> ActivateAsync(Guid userId, CancellationToken cancellationToken) =>
        guard.SerializedAsync(
            async ct =>
            {
                var membership = await LoadAsync(userId, ct);
                await guard.EnsureCanManageAsync(membership, ct);
                if (membership.IsActive)
                {
                    return ToDto(membership);
                }

                // Reactivating hands the member's existing roles back to them, which is a grant like any other.
                await guard.EnsureCanGrantAsync(membership.Roles.SelectMany(r => r.Role!.PermissionKeys()), ct);
                membership.Activate();
                audit.Record(
                    AuditEvents.UserActivated,
                    AuditOutcome.Success,
                    membership.TenantId,
                    membership.UserId,
                    membership.User!.Email,
                    actorUserId: currentUser.UserId);
                await db.SaveChangesAsync(ct);
                await accessCache.InvalidateUserAsync(membership.TenantId, membership.UserId, ct);
                return ToDto(membership);
            },
            cancellationToken);

    public Task RemoveAsync(Guid userId, CancellationToken cancellationToken) =>
        guard.SerializedAsync(
            async ct =>
            {
                var membership = await LoadAsync(userId, ct);
                await guard.EnsureCanManageAsync(membership, ct);
                if (membership.IsActive)
                {
                    await guard.EnsureNotLastSuperAdministratorAsync(membership, ct);
                }

                var revoked = await sessions.RevokeAllAsync(
                    membership.UserId, membership.TenantId, exceptSessionId: null, SessionRevocationReasons.MembershipRemoved, ct);
                db.Set<TenantMembership>().Remove(membership);
                audit.Record(
                    AuditEvents.UserRemoved,
                    AuditOutcome.Success,
                    membership.TenantId,
                    membership.UserId,
                    membership.User!.Email,
                    detail: $"sessions_revoked={revoked}",
                    actorUserId: currentUser.UserId);
                await db.SaveChangesAsync(ct);
                await accessCache.InvalidateUserAsync(membership.TenantId, membership.UserId, ct);
                return true;
            },
            cancellationToken);

    public async Task<IReadOnlyList<SessionDto>> ListSessionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var membership = await LoadAsync(userId, cancellationToken);
        var tenantId = tenantContext.RequireTenantId();
        var tenant = await tenantDirectory.FindByIdAsync(tenantId, cancellationToken);
        var now = clock.UtcNow;

        // Tenant scope: only this user's sessions in THIS tenant are visible; their sessions elsewhere are not.
        var items = await db.Set<UserSession>()
            .AsNoTracking()
            .Where(s => s.UserId == membership.UserId && s.RevokedAtUtc == null && s.ExpiresAtUtc > now && s.IdleExpiresAtUtc > now)
            .OrderByDescending(s => s.LastSeenAtUtc)
            .ToListAsync(cancellationToken);
        return [.. items.Select(s => new SessionDto(
            s.Id, s.TenantId, tenant?.Name ?? string.Empty, s.CreatedAtUtc, s.LastSeenAtUtc, s.ExpiresAtUtc,
            s.IpAddress, s.UserAgent, s.MfaVerified, s.Id == currentUser.SessionId))];
    }

    public async Task<SessionsRevokedDto> RevokeSessionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var membership = await LoadAsync(userId, cancellationToken);
        if (membership.UserId != currentUser.RequireUserId())
        {
            await guard.EnsureCanManageAsync(membership, cancellationToken);
        }

        var revoked = await sessions.RevokeAllAsync(
            membership.UserId, membership.TenantId, exceptSessionId: null, SessionRevocationReasons.RevokedByAdministrator, cancellationToken);
        audit.Record(
            AuditEvents.SessionsRevokedByAdministrator,
            AuditOutcome.Success,
            membership.TenantId,
            membership.UserId,
            membership.User!.Email,
            detail: $"sessions_revoked={revoked}",
            actorUserId: currentUser.UserId);
        await db.SaveChangesAsync(cancellationToken);
        return new SessionsRevokedDto(revoked);
    }

    private async Task<TenantMembership> LoadAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.Set<TenantMembership>()
            .Include(m => m.User)
            .Include(m => m.Roles).ThenInclude(r => r.Role).ThenInclude(role => role!.Permissions)
            .AsSplitQuery()
            .FirstOrDefaultAsync(m => m.UserId == userId, cancellationToken)
        ?? throw new NotFoundException("User", userId);

    private static TenantUserDto ToDto(TenantMembership m)
    {
        var user = m.User!;
        return new TenantUserDto(
            user.Id,
            m.Id,
            user.Email,
            user.DisplayName,
            m.IsActive,
            user.IsEmailVerified,
            user.MfaEnabled,
            [.. m.Roles.Where(r => r.Role is not null).Select(r => new RoleSummaryDto(r.RoleId, r.Role!.Name, r.Role.IsSystem)).OrderBy(r => r.Name, StringComparer.Ordinal)],
            m.CreatedAtUtc,
            m.DeactivatedAtUtc);
    }
}
