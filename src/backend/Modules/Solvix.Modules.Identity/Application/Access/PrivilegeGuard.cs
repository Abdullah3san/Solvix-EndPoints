namespace Solvix.Modules.Identity.Application.Access;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Identity.Domain;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

/// <summary>
/// The rules that stop administrative permissions from being turned into more
/// permissions:
///
///  1. Nobody can hand out a permission they do not hold themselves
///     (assigning a role, inviting with a role, creating or editing a role).
///  2. Nobody can manage (re-role, deactivate, remove, sign out) an account
///     that holds a permission they do not hold.
///  3. Nobody can change their own roles or deactivate or remove themselves.
///  4. A tenant always keeps at least one active Super Administrator.
///
/// The caller's permissions are re-read from the database for these checks
/// rather than taken from the per-request cache.
/// </summary>
public interface IPrivilegeGuard
{
    /// <summary>Permissions the caller holds in the current tenant, read fresh from the database.</summary>
    Task<IReadOnlySet<string>> ActorPermissionsAsync(CancellationToken cancellationToken);

    /// <summary>Throws unless the caller holds every permission in <paramref name="permissions"/>.</summary>
    Task EnsureCanGrantAsync(IEnumerable<string> permissions, CancellationToken cancellationToken);

    /// <summary>Throws unless the caller may manage the given member (rules 2 and 3).</summary>
    Task EnsureCanManageAsync(TenantMembership target, CancellationToken cancellationToken);

    /// <summary>Throws when <paramref name="target"/> is the tenant's only active Super Administrator.</summary>
    Task EnsureNotLastSuperAdministratorAsync(TenantMembership target, CancellationToken cancellationToken);

    /// <summary>
    /// Runs <paramref name="action"/> in a transaction that holds a per-tenant lock,
    /// so two administrators changing roles at the same moment are checked one
    /// after the other and cannot jointly remove the last Super Administrator.
    /// </summary>
    Task<T> SerializedAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken);
}

internal sealed class PrivilegeGuard(ISolvixDbContext db, ICurrentUser currentUser, ITenantContext tenantContext) : IPrivilegeGuard
{
    public const string InsufficientPrivilegeCode = "insufficient_privilege";

    public Task<IReadOnlySet<string>> ActorPermissionsAsync(CancellationToken cancellationToken) =>
        PermissionsOfUserAsync(currentUser.RequireUserId(), activeOnly: true, cancellationToken);

    public async Task EnsureCanGrantAsync(IEnumerable<string> permissions, CancellationToken cancellationToken)
    {
        var actor = await ActorPermissionsAsync(cancellationToken);
        var missing = permissions.Where(p => !actor.Contains(p)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            throw new ForbiddenException(
                $"You cannot grant permissions you do not hold yourself ({string.Join(", ", missing)}).",
                InsufficientPrivilegeCode);
        }
    }

    public async Task EnsureCanManageAsync(TenantMembership target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.UserId == currentUser.RequireUserId())
        {
            throw new ForbiddenException("You cannot change your own roles or status. Ask another administrator.", "self_management_forbidden");
        }

        var actor = await ActorPermissionsAsync(cancellationToken);
        var targetPermissions = await PermissionsOfUserAsync(target.UserId, activeOnly: false, cancellationToken);
        if (!targetPermissions.IsSubsetOf(actor))
        {
            throw new ForbiddenException(
                "You cannot manage a user who holds permissions you do not hold yourself.",
                InsufficientPrivilegeCode);
        }
    }

    public async Task EnsureNotLastSuperAdministratorAsync(TenantMembership target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var superAdministrators = await db.Set<TenantMembership>()
            .Where(m => m.IsActive && m.Roles.Any(r => r.Role!.SystemKey == SystemRoles.SuperAdministratorKey))
            .Select(m => m.Id)
            .ToListAsync(cancellationToken);
        if (superAdministrators.Count <= 1 && superAdministrators.Contains(target.Id))
        {
            throw new ConflictException("A workspace must keep at least one active Super Administrator.", "last_super_administrator");
        }
    }

    public async Task<T> SerializedAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        var lockKey = $"solvix:iam:{tenantContext.RequireTenantId()}";
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
            async ct =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", ct);
                var result = await action(ct);
                await transaction.CommitAsync(ct);
                return result;
            },
            cancellationToken);
    }

    private async Task<IReadOnlySet<string>> PermissionsOfUserAsync(Guid userId, bool activeOnly, CancellationToken cancellationToken)
    {
        // Tenant scope: only roles of the current tenant are visible here.
        var permissions = await db.Set<TenantMembership>()
            .Where(m => m.UserId == userId && (!activeOnly || m.IsActive))
            .SelectMany(m => m.Roles)
            .SelectMany(r => r.Role!.Permissions)
            .Select(p => p.Permission)
            .Distinct()
            .ToListAsync(cancellationToken);
        return permissions.ToHashSet(StringComparer.Ordinal);
    }
}
