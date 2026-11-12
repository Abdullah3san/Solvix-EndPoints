namespace Solvix.Modules.Identity.Domain;

using Solvix.SharedKernel.Authorization;
using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Guards;

/// <summary>
/// A tenant-owned, named set of permissions. Every tenant gets its own copy of
/// the built-in roles (<see cref="SystemKey"/> set, immutable through the API)
/// and may add custom ones. Authorization never looks at the name or the key:
/// only at <see cref="Permissions"/>.
/// </summary>
public sealed class Role : TenantEntity
{
    public const int NameMaxLength = 100;
    public const int DescriptionMaxLength = 500;
    public const int SystemKeyMaxLength = 64;

    private readonly List<RolePermission> _permissions = [];

    private Role()
    {
    }

    private Role(Guid tenantId, string name, string? description, string? systemKey)
        : base(tenantId)
    {
        Name = name;
        NormalizedName = NormalizeName(name);
        Description = description;
        SystemKey = systemKey;
    }

    public string Name { get; private set; } = string.Empty;

    public string NormalizedName { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    /// <summary>Stable identifier of a built-in role (e.g. <c>super_admin</c>); <c>null</c> for custom roles.</summary>
    public string? SystemKey { get; private set; }

    public bool IsSystem => SystemKey is not null;

    public IReadOnlyCollection<RolePermission> Permissions => _permissions;

    public static Role CreateCustom(Guid tenantId, string name, string? description, IEnumerable<string> permissions)
    {
        var role = new Role(
            tenantId,
            Guard.NotNullOrWhiteSpace(name, "name", NameMaxLength),
            Guard.Optional(description, "description", DescriptionMaxLength),
            systemKey: null);
        role.ReplacePermissions(permissions);
        return role;
    }

    public static Role CreateSystem(Guid tenantId, SystemRoleDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var role = new Role(tenantId, definition.Name, definition.Description, definition.Key);
        role.ReplacePermissions(definition.Permissions);
        return role;
    }

    public static string NormalizeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Trim().ToUpperInvariant();
    }

    public IReadOnlySet<string> PermissionKeys() =>
        _permissions.Select(p => p.Permission).ToHashSet(StringComparer.Ordinal);

    /// <summary>Renames and re-scopes a custom role. Built-in roles cannot be edited.</summary>
    public void Update(string name, string? description, IEnumerable<string> permissions)
    {
        EnsureCustom();
        Name = Guard.NotNullOrWhiteSpace(name, "name", NameMaxLength);
        NormalizedName = NormalizeName(Name);
        Description = Guard.Optional(description, "description", DescriptionMaxLength);
        ReplacePermissions(permissions);
    }

    public void EnsureCustom()
    {
        if (IsSystem)
        {
            throw new ConflictException("Built-in roles cannot be changed or deleted. Create a custom role instead.", "system_role_immutable");
        }
    }

    private void ReplacePermissions(IEnumerable<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        var requested = permissions.ToList();
        var unknown = requested.Where(p => !PermissionCatalog.IsKnown(p)).ToList();
        if (unknown.Count > 0)
        {
            throw new ValidationException("permissions", $"Unknown permission(s): {string.Join(", ", unknown)}.");
        }

        var target = PermissionCatalog.Expand(requested);
        _permissions.RemoveAll(p => !target.Contains(p.Permission));
        foreach (var key in target.Order(StringComparer.Ordinal))
        {
            if (_permissions.TrueForAll(p => p.Permission != key))
            {
                _permissions.Add(RolePermission.Create(TenantId, Id, key));
            }
        }
    }
}

public sealed class RolePermission : TenantEntity
{
    public const int PermissionMaxLength = 100;

    private RolePermission()
    {
    }

    private RolePermission(Guid tenantId, Guid roleId, string permission)
        : base(tenantId)
    {
        RoleId = roleId;
        Permission = permission;
    }

    public Guid RoleId { get; private set; }

    public string Permission { get; private set; } = string.Empty;

    public static RolePermission Create(Guid tenantId, Guid roleId, string permission) => new(tenantId, roleId, permission);
}
