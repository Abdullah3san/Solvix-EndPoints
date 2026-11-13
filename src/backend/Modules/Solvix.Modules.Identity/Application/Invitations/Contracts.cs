namespace Solvix.Modules.Identity.Application.Invitations;

using Solvix.Modules.Identity.Application.Account;
using Solvix.Modules.Identity.Application.Users;
using Solvix.Modules.Identity.Domain;

public sealed record CreateInvitationRequest(string Email, string? DisplayName, IReadOnlyList<Guid> RoleIds);

/// <summary>An invitation as administrators see it. The token itself is only ever delivered by e-mail.</summary>
public sealed record InvitationDto(
    Guid Id,
    string Email,
    string? DisplayName,
    InvitationStatus Status,
    IReadOnlyList<RoleSummaryDto> Roles,
    Guid InvitedByUserId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? AcceptedAtUtc);

public sealed record InvitationTokenRequest(string Token);

/// <summary>What the invitee sees before accepting.</summary>
public sealed record InvitationPreviewDto(
    string TenantName,
    string Email,
    string? DisplayName,
    bool AccountExists,
    IReadOnlyList<string> Roles,
    DateTimeOffset ExpiresAtUtc,
    PasswordRulesDto PasswordRules);

/// <summary>
/// Accepts an invitation. <see cref="DisplayName"/> and <see cref="Password"/>
/// are only used when the invited address has no account yet; for an existing
/// account they are ignored and its credentials are left untouched.
/// </summary>
public sealed record AcceptInvitationRequest(string Token, string? DisplayName, string? Password);

public sealed record InvitationAcceptedDto(string TenantSlug, string TenantName, string Email, bool AccountCreated);
