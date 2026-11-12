namespace Solvix.Modules.Identity.Domain;

using Solvix.SharedKernel.Entities;

public enum UserTokenPurpose
{
    EmailVerification = 1,
    PasswordReset = 2,
    MfaChallenge = 3,
}

/// <summary>
/// A single-use, expiring token bound to one user and one purpose (e-mail
/// verification link, password reset link, pending MFA challenge). Platform
/// level, like the user it belongs to. Only the SHA-256 hash is stored.
/// </summary>
public sealed class UserToken : Entity
{
    public const int TokenHashLength = 64;
    public const int DataMaxLength = 256;

    private UserToken()
    {
    }

    private UserToken(Guid userId, UserTokenPurpose purpose, string tokenHash, DateTimeOffset expiresAtUtc, string? data)
    {
        UserId = userId;
        Purpose = purpose;
        TokenHash = tokenHash;
        ExpiresAtUtc = expiresAtUtc;
        Data = data;
    }

    public Guid UserId { get; private set; }

    public User? User { get; private set; }

    public UserTokenPurpose Purpose { get; private set; }

    public string TokenHash { get; private set; } = string.Empty;

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public DateTimeOffset? ConsumedAtUtc { get; private set; }

    /// <summary>Failed attempts to satisfy the challenge this token represents (MFA only).</summary>
    public int Attempts { get; private set; }

    /// <summary>Purpose-specific context fixed when the token was issued, e.g. the tenant an MFA challenge signs in to.</summary>
    public string? Data { get; private set; }

    public static UserToken Issue(Guid userId, UserTokenPurpose purpose, string tokenHash, DateTimeOffset expiresAtUtc, string? data = null) =>
        new(userId, purpose, tokenHash, expiresAtUtc, data);

    public bool IsUsable(DateTimeOffset now) => ConsumedAtUtc is null && now < ExpiresAtUtc;
}

/// <summary>A previous password hash, kept so recent passwords cannot be reused.</summary>
public sealed class PasswordHistoryEntry : Entity
{
    private PasswordHistoryEntry()
    {
    }

    private PasswordHistoryEntry(Guid userId, string passwordHash)
    {
        UserId = userId;
        PasswordHash = passwordHash;
    }

    public Guid UserId { get; private set; }

    public string PasswordHash { get; private set; } = string.Empty;

    public static PasswordHistoryEntry Record(Guid userId, string passwordHash) => new(userId, passwordHash);
}

/// <summary>A one-time MFA recovery code. Stored as a keyed hash; the plain code is shown once at generation.</summary>
public sealed class UserRecoveryCode : Entity
{
    public const int CodeHashLength = 64;

    private UserRecoveryCode()
    {
    }

    private UserRecoveryCode(Guid userId, string codeHash)
    {
        UserId = userId;
        CodeHash = codeHash;
    }

    public Guid UserId { get; private set; }

    public string CodeHash { get; private set; } = string.Empty;

    public DateTimeOffset? UsedAtUtc { get; private set; }

    public static UserRecoveryCode Create(Guid userId, string codeHash) => new(userId, codeHash);

    public void MarkUsed(DateTimeOffset at) => UsedAtUtc = at;
}
