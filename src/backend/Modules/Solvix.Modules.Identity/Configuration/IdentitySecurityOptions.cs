namespace Solvix.Modules.Identity.Configuration;

using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using Solvix.Modules.Identity.Domain;

/// <summary>Platform-wide identity settings (<c>Solvix:Identity</c>). Tenants refine some of these in their security policy.</summary>
public sealed class IdentitySecurityOptions
{
    public const string SectionName = "Solvix:Identity";

    /// <summary>
    /// Secret used to derive the keys that encrypt MFA secrets and hash recovery
    /// codes at rest. Must be stable across deployments and different from the JWT key.
    /// </summary>
    [Required]
    [MinLength(32, ErrorMessage = "Solvix:Identity:DataProtectionKey must be at least 32 characters.")]
    public string DataProtectionKey { get; set; } = string.Empty;

    /// <summary>Public base URL of the web application; links in e-mails point here.</summary>
    [Required]
    [Url]
    public string WebBaseUrl { get; set; } = "http://localhost:3000";

    /// <summary>Name shown in authenticator apps.</summary>
    [Required]
    public string MfaIssuer { get; set; } = "Solvix";

    /// <summary>When true, an account cannot sign in until its e-mail address is verified.</summary>
    public bool RequireEmailVerification { get; set; } = true;

    [Range(1, 168)]
    public int EmailVerificationTokenHours { get; set; } = 24;

    [Range(5, 1440)]
    public int PasswordResetTokenMinutes { get; set; } = 60;

    [Range(1, 30)]
    public int InvitationDays { get; set; } = 7;

    [Range(1, 30)]
    public int MfaChallengeMinutes { get; set; } = 5;

    /// <summary>Wrong codes tolerated for one MFA challenge before it is discarded.</summary>
    [Range(1, 10)]
    public int MfaChallengeMaxAttempts { get; set; } = 5;

    /// <summary>Minimum time between two e-mails of the same kind to the same account.</summary>
    [Range(0, 3600)]
    public int EmailCooldownSeconds { get; set; } = 60;

    /// <summary>
    /// Window in which an already-rotated refresh token is still accepted, to
    /// tolerate concurrent refreshes from the same browser. Outside it, reuse
    /// revokes the session.
    /// </summary>
    [Range(0, 60)]
    public int RefreshTokenReuseGraceSeconds { get; set; } = 10;

    [ValidateObjectMembers]
    public LockoutOptions Lockout { get; set; } = new();

    [ValidateObjectMembers]
    public PasswordBaselineOptions Password { get; set; } = new();

    [ValidateObjectMembers]
    public SessionDefaultOptions Session { get; set; } = new();

    /// <summary>The settings a new tenant starts with, and the floor no tenant may go below.</summary>
    public SecurityPolicySettings Baseline() => new(
        Password.MinLength,
        Password.RequireUppercase,
        Password.RequireLowercase,
        Password.RequireDigit,
        Password.RequireSymbol,
        Password.HistoryCount,
        PasswordMaxAgeDays: 0,
        Session.IdleTimeoutMinutes,
        Session.AbsoluteLifetimeHours,
        RequireMfa: false);
}

public sealed class LockoutOptions
{
    /// <summary>Consecutive failed credential checks before the account is locked.</summary>
    [Range(3, 20)]
    public int MaxFailedAttempts { get; set; } = 5;

    /// <summary>Length of the first lockout. Each further consecutive lockout doubles it.</summary>
    [Range(1, 1440)]
    public int DurationMinutes { get; set; } = 15;

    [Range(1, 10080)]
    public int MaxDurationMinutes { get; set; } = 1440;

    public TimeSpan DurationFor(int previousLockouts)
    {
        var factor = Math.Pow(2, Math.Clamp(previousLockouts, 0, 16));
        var minutes = Math.Min(DurationMinutes * factor, Math.Max(DurationMinutes, MaxDurationMinutes));
        return TimeSpan.FromMinutes(minutes);
    }
}

public sealed class PasswordBaselineOptions
{
    [Range(TenantSecurityPolicy.MinPasswordLength, TenantSecurityPolicy.MaxPasswordLength)]
    public int MinLength { get; set; } = 10;

    public bool RequireUppercase { get; set; }

    public bool RequireLowercase { get; set; } = true;

    public bool RequireDigit { get; set; } = true;

    public bool RequireSymbol { get; set; }

    [Range(0, TenantSecurityPolicy.MaxPasswordHistory)]
    public int HistoryCount { get; set; } = 5;

    /// <summary>PBKDF2-HMAC-SHA512 iterations. The floor follows current OWASP guidance.</summary>
    [Range(210_000, 5_000_000)]
    public int HashIterations { get; set; } = 210_000;
}

public sealed class SessionDefaultOptions
{
    [Range(TenantSecurityPolicy.MinIdleTimeoutMinutes, TenantSecurityPolicy.MaxIdleTimeoutMinutes)]
    public int IdleTimeoutMinutes { get; set; } = 480;

    [Range(TenantSecurityPolicy.MinAbsoluteLifetimeHours, TenantSecurityPolicy.MaxAbsoluteLifetimeHours)]
    public int AbsoluteLifetimeHours { get; set; } = 168;
}
