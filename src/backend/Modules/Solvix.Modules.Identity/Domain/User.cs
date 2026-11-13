namespace Solvix.Modules.Identity.Domain;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Guards;

/// <summary>
/// A platform-level person identity. Users are NOT tenant-owned: one person can
/// belong to several tenants through <see cref="TenantMembership"/>. Email is
/// globally unique (case-insensitive). Credentials (password, MFA) belong to the
/// user, never to a tenant, so no tenant administrator can read or reset them.
/// </summary>
public sealed class User : Entity
{
    public const int EmailMaxLength = 320;
    public const int DisplayNameMaxLength = 200;

    private User()
    {
    }

    private User(string email, string displayName, string passwordHash, DateTimeOffset now)
    {
        Email = email;
        NormalizedEmail = NormalizeEmail(email);
        DisplayName = displayName;
        PasswordHash = passwordHash;
        PasswordChangedAtUtc = now;
        IsActive = true;
    }

    public string Email { get; private set; } = string.Empty;

    public string NormalizedEmail { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public string PasswordHash { get; private set; } = string.Empty;

    public DateTimeOffset PasswordChangedAtUtc { get; private set; }

    /// <summary>Platform-level switch. A tenant deactivates a <see cref="TenantMembership"/> instead.</summary>
    public bool IsActive { get; private set; }

    public DateTimeOffset? EmailVerifiedAtUtc { get; private set; }

    public DateTimeOffset? LastLoginAtUtc { get; private set; }

    /// <summary>Consecutive failed credential checks since the last success or lockout. Updated atomically in SQL.</summary>
    public int FailedAccessCount { get; private set; }

    /// <summary>Consecutive lockouts; each one doubles the next lockout's duration.</summary>
    public int LockoutCount { get; private set; }

    public DateTimeOffset? LockoutEndUtc { get; private set; }

    public bool MfaEnabled { get; private set; }

    public DateTimeOffset? MfaEnabledAtUtc { get; private set; }

    /// <summary>Encrypted TOTP secret in use. Never returned by the API after enrolment.</summary>
    public string? MfaSecret { get; private set; }

    /// <summary>Encrypted TOTP secret awaiting confirmation with a first valid code.</summary>
    public string? MfaPendingSecret { get; private set; }

    /// <summary>Last accepted TOTP time step; a code is never accepted twice.</summary>
    public long? MfaLastUsedStep { get; private set; }

    public bool IsEmailVerified => EmailVerifiedAtUtc.HasValue;

    public static User Create(string email, string displayName, string passwordHash, DateTimeOffset now)
    {
        var cleanEmail = Guard.NotNullOrWhiteSpace(email, "email", EmailMaxLength);
        var cleanName = Guard.NotNullOrWhiteSpace(displayName, "displayName", DisplayNameMaxLength);
        Guard.NotNullOrWhiteSpace(passwordHash, "passwordHash");
        return new User(cleanEmail, cleanName, passwordHash, now);
    }

    public static string NormalizeEmail(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        return email.Trim().ToUpperInvariant();
    }

    public bool IsLockedOut(DateTimeOffset now) => LockoutEndUtc is { } end && end > now;

    public bool IsPasswordExpired(DateTimeOffset now, int maxAgeDays) =>
        maxAgeDays > 0 && PasswordChangedAtUtc.AddDays(maxAgeDays) <= now;

    public void RecordLogin(DateTimeOffset at) => LastLoginAtUtc = at;

    public void Rename(string displayName) =>
        DisplayName = Guard.NotNullOrWhiteSpace(displayName, "displayName", DisplayNameMaxLength);

    /// <summary>Replaces the stored hash without counting as a password change (transparent re-hash).</summary>
    public void SetPasswordHash(string passwordHash) =>
        PasswordHash = Guard.NotNullOrWhiteSpace(passwordHash, "passwordHash");

    public void ChangePassword(string passwordHash, DateTimeOffset at)
    {
        SetPasswordHash(passwordHash);
        PasswordChangedAtUtc = at;
    }

    public void MarkEmailVerified(DateTimeOffset at) => EmailVerifiedAtUtc ??= at;

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;

    public void BeginMfaEnrollment(string protectedSecret) =>
        MfaPendingSecret = Guard.NotNullOrWhiteSpace(protectedSecret, "secret");

    public void ConfirmMfaEnrollment(long usedStep, DateTimeOffset at)
    {
        if (MfaPendingSecret is null)
        {
            throw new InvalidOperationException("There is no pending MFA enrolment to confirm.");
        }

        MfaSecret = MfaPendingSecret;
        MfaPendingSecret = null;
        MfaEnabled = true;
        MfaEnabledAtUtc = at;
        MfaLastUsedStep = usedStep;
    }

    public void RecordMfaStep(long step) => MfaLastUsedStep = step;

    public void DisableMfa()
    {
        MfaEnabled = false;
        MfaEnabledAtUtc = null;
        MfaSecret = null;
        MfaPendingSecret = null;
        MfaLastUsedStep = null;
    }
}
