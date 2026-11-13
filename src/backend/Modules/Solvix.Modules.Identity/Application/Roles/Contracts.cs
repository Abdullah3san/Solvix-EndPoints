namespace Solvix.Modules.Identity.Application.Roles;

public sealed record RoleDto(
    Guid Id,
    string Name,
    string? Description,
    bool IsSystem,
    string? SystemKey,
    IReadOnlyList<string> Permissions,
    int MemberCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record SaveRoleRequest(string Name, string? Description, IReadOnlyList<string> Permissions);

public sealed record PermissionDto(string Key, string Group, string Description, IReadOnlyList<string> Implies);
