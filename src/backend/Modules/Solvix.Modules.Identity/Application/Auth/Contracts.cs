namespace Solvix.Modules.Identity.Application.Auth;

public sealed record LoginRequest(string Email, string Password, string? TenantSlug);

/// <summary>
/// Result of a password check. Either the sign-in is complete
/// (<see cref="Session"/>), or a second factor is still required
/// (<see cref="Challenge"/>) and no session or token has been issued yet.
/// </summary>
public sealed record LoginResponse(bool MfaRequired, AuthResponse? Session, MfaChallengeDto? Challenge);

public sealed record MfaChallengeDto(string MfaToken, DateTimeOffset ExpiresAtUtc);

/// <summary>Completes a sign-in with either an authenticator code or a recovery code.</summary>
public sealed record MfaVerifyRequest(string MfaToken, string? Code, string? RecoveryCode);

public sealed record RefreshRequest(string RefreshToken);

public sealed record LogoutRequest(string? RefreshToken);

public sealed record SwitchTenantRequest(string TenantSlug);

public sealed record AuthResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAtUtc,
    Guid SessionId,
    UserDto User,
    AuthTenantDto Tenant);

public sealed record UserDto(Guid Id, string Email, string DisplayName);

public sealed record AuthTenantDto(Guid Id, string Slug, string Name);

/// <summary>The caller, the tenant bound to the request, and what the caller may do there right now.</summary>
public sealed record CurrentUserDto(
    UserDto User,
    AuthTenantDto Tenant,
    Guid SessionId,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    bool MfaEnabled,
    bool MfaVerified,
    bool MfaEnrollmentRequired);

public sealed record SessionDto(
    Guid Id,
    Guid TenantId,
    string TenantName,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastSeenAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string? IpAddress,
    string? UserAgent,
    bool MfaVerified,
    bool IsCurrent);
