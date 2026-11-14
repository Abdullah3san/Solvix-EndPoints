namespace Solvix.Modules.Assistant.Security;

using Solvix.SharedKernel.Authorization;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Fleet;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Tenancy;

/// <summary>
/// The assistant's privilege levels, derived from the caller's permissions:
/// <see cref="Member"/> = <c>assistant.use</c>, <see cref="Admin"/> = <c>assistant.manage</c>,
/// <see cref="Owner"/> = <c>assistant.approve_high</c>. Ordered: a higher level includes the lower ones.
/// </summary>
public enum AssistantRole
{
    None = 0,
    Member = 1,
    Admin = 2,
    Owner = 3,
}

/// <summary>The authenticated human on whose behalf the assistant acts. The assistant has no identity or privileges of its own.</summary>
public sealed record AssistantPrincipal(Guid TenantId, Guid UserId, string? Email, AssistantRole Role, IReadOnlySet<string> Permissions)
{
    public AssistantPrincipal(Guid tenantId, Guid userId, string? email, AssistantRole role)
        : this(tenantId, userId, email, role, PermissionsFor(role))
    {
    }

    public FleetAccess ToFleetAccess() => new(TenantId, UserId, Permissions);

    public bool HasAtLeast(AssistantRole minimum) => Role >= minimum;

    public bool Has(string permission) => Permissions.Contains(permission);

    public static AssistantRole RoleFrom(IReadOnlySet<string> permissions) =>
        permissions.Contains(Solvix.SharedKernel.Authorization.Permissions.Assistant.ApproveHigh) ? AssistantRole.Owner
        : permissions.Contains(Solvix.SharedKernel.Authorization.Permissions.Assistant.Manage) ? AssistantRole.Admin
        : permissions.Contains(Solvix.SharedKernel.Authorization.Permissions.Assistant.Use) ? AssistantRole.Member
        : AssistantRole.None;

    /// <summary>Assistant permissions for a level (tests and the demo fleet); real callers carry their full permission set.</summary>
    private static IReadOnlySet<string> PermissionsFor(AssistantRole role) => role switch
    {
        AssistantRole.Owner => new HashSet<string>(PermissionCatalog.AllKeys, StringComparer.Ordinal),
        AssistantRole.Admin => new HashSet<string>(PermissionCatalog.AllKeys.Where(k => k != Solvix.SharedKernel.Authorization.Permissions.Assistant.ApproveHigh), StringComparer.Ordinal),
        AssistantRole.Member => new HashSet<string>(PermissionCatalog.AllKeys.Where(k => k.EndsWith(".view", StringComparison.Ordinal) || k == Solvix.SharedKernel.Authorization.Permissions.Assistant.Use), StringComparer.Ordinal),
        _ => new HashSet<string>(StringComparer.Ordinal),
    };
}

public interface IAssistantPrincipalAccessor
{
    AssistantPrincipal Current { get; }
}

internal sealed class AssistantPrincipalAccessor(ICurrentUser user, ITenantContext tenant) : IAssistantPrincipalAccessor
{
    public AssistantPrincipal Current
    {
        get
        {
            var userId = user.RequireUserId();
            var tenantId = tenant.RequireTenantId();
            var permissions = user.Permissions;
            var role = AssistantPrincipal.RoleFrom(permissions);
            if (role == AssistantRole.None)
            {
                throw new ForbiddenException("Using the AI assistant requires the assistant.use permission.");
            }

            return new AssistantPrincipal(tenantId, userId, user.Email, role, permissions);
        }
    }
}
