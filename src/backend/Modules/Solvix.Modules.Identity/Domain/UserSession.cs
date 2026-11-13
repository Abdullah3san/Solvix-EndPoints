namespace Solvix.Modules.Identity.Domain;

using Solvix.SharedKernel.Entities;

/// <summary>
/// A sign-in of one user into one tenant. Access tokens carry the session id
/// and are only honoured while the session is active, which is what makes
/// sign-out, revocation and deactivation take effect immediately.
/// </summary>
public sealed class UserSession : TenantEntity
{
    public const int IpAddressMaxLength = 45;
    public const int UserAgentMaxLength = 512;
    public const int RevokedReasonMaxLength = 64;

    private UserSession()
    {
    }

    private UserSession(Guid tenantId, Guid userId, DateTimeOffset now, DateTimeOffset idleExpiresAtUtc, DateTimeOffset expiresAtUtc)
        : base(tenantId)
    {
        UserId = userId;
        LastSeenAtUtc = now;
        IdleExpiresAtUtc = idleExpiresAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public Guid UserId { get; private set; }

    public User? User { get; private set; }

    public DateTimeOffset LastSeenAtUtc { get; private set; }

    /// <summary>Slides forward on every refresh; the session dies when it is not used for this long.</summary>
    public DateTimeOffset IdleExpiresAtUtc { get; private set; }

    /// <summary>Absolute end of the session, fixed at sign-in.</summary>
    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public string? RevokedReason { get; private set; }

    public string? IpAddress { get; private set; }

    public string? UserAgent { get; private set; }

    /// <summary>Whether a second factor was presented for this session.</summary>
    public bool MfaVerified { get; private set; }

    public static UserSession Start(
        Guid tenantId,
        Guid userId,
        DateTimeOffset now,
        TimeSpan idleTimeout,
        TimeSpan absoluteLifetime,
        bool mfaVerified,
        string? ipAddress,
        string? userAgent)
    {
        var expires = now.Add(absoluteLifetime);
        var idle = Min(now.Add(idleTimeout), expires);
        return new UserSession(tenantId, userId, now, idle, expires)
        {
            MfaVerified = mfaVerified,
            IpAddress = Truncate(ipAddress, IpAddressMaxLength),
            UserAgent = Truncate(userAgent, UserAgentMaxLength),
        };
    }

    public bool IsActive(DateTimeOffset now) => RevokedAtUtc is null && now < ExpiresAtUtc && now < IdleExpiresAtUtc;

    /// <summary>Records use of the session and slides the idle window. Returns the new idle expiry.</summary>
    public DateTimeOffset Touch(DateTimeOffset now, TimeSpan idleTimeout, string? ipAddress)
    {
        LastSeenAtUtc = now;
        IdleExpiresAtUtc = Min(now.Add(idleTimeout), ExpiresAtUtc);
        if (!string.IsNullOrWhiteSpace(ipAddress))
        {
            IpAddress = Truncate(ipAddress, IpAddressMaxLength);
        }

        return IdleExpiresAtUtc;
    }

    public void MarkMfaVerified() => MfaVerified = true;

    public bool Revoke(DateTimeOffset now, string reason)
    {
        if (RevokedAtUtc is not null)
        {
            return false;
        }

        RevokedAtUtc = now;
        RevokedReason = Truncate(reason, RevokedReasonMaxLength);
        return true;
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];
}

/// <summary>Why a session ended. Stored as text on the session and in audit events.</summary>
public static class SessionRevocationReasons
{
    public const string SignedOut = "signed_out";
    public const string RevokedByUser = "revoked_by_user";
    public const string RevokedByAdministrator = "revoked_by_administrator";
    public const string TenantSwitched = "tenant_switched";
    public const string PasswordChanged = "password_changed";
    public const string PasswordReset = "password_reset";
    public const string MfaChanged = "mfa_changed";
    public const string MembershipDeactivated = "membership_deactivated";
    public const string MembershipRemoved = "membership_removed";
    public const string RefreshTokenReuse = "refresh_token_reuse";
    public const string AccessLost = "access_lost";
}

/// <summary>
/// One refresh token of a session. Only its SHA-256 hash is stored. A token is
/// used exactly once: redeeming it issues a successor, and presenting an
/// already-used token again revokes the whole session.
/// </summary>
public sealed class RefreshToken : TenantEntity
{
    public const int TokenHashLength = 64;

    private RefreshToken()
    {
    }

    private RefreshToken(Guid tenantId, Guid sessionId, string tokenHash, DateTimeOffset expiresAtUtc, Guid? parentId)
        : base(tenantId)
    {
        SessionId = sessionId;
        TokenHash = tokenHash;
        ExpiresAtUtc = expiresAtUtc;
        ParentId = parentId;
    }

    public Guid SessionId { get; private set; }

    public UserSession? Session { get; private set; }

    public string TokenHash { get; private set; } = string.Empty;

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public DateTimeOffset? ConsumedAtUtc { get; private set; }

    /// <summary>The token this one replaced.</summary>
    public Guid? ParentId { get; private set; }

    public static RefreshToken Issue(Guid tenantId, Guid sessionId, string tokenHash, DateTimeOffset expiresAtUtc, Guid? parentId = null) =>
        new(tenantId, sessionId, tokenHash, expiresAtUtc, parentId);
}
