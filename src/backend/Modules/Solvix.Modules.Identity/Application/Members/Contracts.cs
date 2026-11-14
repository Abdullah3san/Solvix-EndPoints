namespace Solvix.Modules.Identity.Application.Members;

using Solvix.SharedKernel.Identity;

public sealed record MemberDto(
    Guid MembershipId,
    Guid UserId,
    string Email,
    string DisplayName,
    TenantRole Role,
    bool IsActive,
    DateTimeOffset JoinedAtUtc);

/// <summary>Adds a member: creates the user if the e-mail is unknown, otherwise links the existing user.</summary>
public sealed record AddMemberRequest(string Email, string DisplayName, string? Password, TenantRole Role);

public sealed record ChangeMemberRoleRequest(TenantRole Role);
