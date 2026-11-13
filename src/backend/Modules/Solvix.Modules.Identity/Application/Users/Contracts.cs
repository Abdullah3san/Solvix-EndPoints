namespace Solvix.Modules.Identity.Application.Users;

public sealed record RoleSummaryDto(Guid Id, string Name, bool IsSystem);

/// <summary>A member of the current tenant. Credentials are never part of this view.</summary>
public sealed record TenantUserDto(
    Guid UserId,
    Guid MembershipId,
    string Email,
    string DisplayName,
    bool IsActive,
    bool EmailVerified,
    bool MfaEnabled,
    IReadOnlyList<RoleSummaryDto> Roles,
    DateTimeOffset JoinedAtUtc,
    DateTimeOffset? DeactivatedAtUtc);

public sealed record UserListQuery(int Page = 1, int PageSize = 25, string? Search = null, bool IncludeInactive = true);

public sealed record SetUserRolesRequest(IReadOnlyList<Guid> RoleIds);

public sealed record SessionsRevokedDto(int Revoked);
