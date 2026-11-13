namespace Solvix.Modules.Identity.Domain;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Exceptions;

/// <summary>
/// Per-tenant security settings: password rules, session lifetimes and whether
/// multi-factor authentication is mandatory. One row per tenant. A tenant can
/// only tighten the platform baseline, never loosen it.
/// </summary>
public sealed class TenantSecurityPolicy : TenantEntity
{
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 128;
    public const int MaxPasswordHistory = 24;
    public const int MaxPasswordAgeDays = 730;
    public const int MinIdleTimeoutMinutes = 5;
    public const int MaxIdleTimeoutMinutes = 60 * 24 * 30;
    public const int MinAbsoluteLifetimeHours = 1;
    public const int MaxAbsoluteLifetimeHours = 24 * 90;

    private TenantSecurityPolicy()
    {
    }

    private TenantSecurityPolicy(Guid tenantId)
        : base(tenantId)
    {
    }

    public int PasswordMinLength { get; private set; }

    public bool PasswordRequireUppercase { get; private set; }

    public bool PasswordRequireLowercase { get; private set; }

    public bool PasswordRequireDigit { get; private set; }

    public bool PasswordRequireSymbol { get; private set; }

    /// <summary>How many previous passwords may not be reused. 0 disables the check.</summary>
    public int PasswordHistoryCount { get; private set; }

    /// <summary>Days after which a password must be changed. 0 means passwords do not expire.</summary>
    public int PasswordMaxAgeDays { get; private set; }

    public int SessionIdleTimeoutMinutes { get; private set; }

    public int SessionAbsoluteLifetimeHours { get; private set; }

    /// <summary>When set, members must sign in with a second factor to use anything but their own account pages.</summary>
    public bool RequireMfa { get; private set; }

    public static TenantSecurityPolicy CreateDefault(Guid tenantId, SecurityPolicySettings defaults)
    {
        var policy = new TenantSecurityPolicy(tenantId);
        policy.Apply(defaults, floor: null);
        return policy;
    }

    public SecurityPolicySettings ToSettings() => new(
        PasswordMinLength,
        PasswordRequireUppercase,
        PasswordRequireLowercase,
        PasswordRequireDigit,
        PasswordRequireSymbol,
        PasswordHistoryCount,
        PasswordMaxAgeDays,
        SessionIdleTimeoutMinutes,
        SessionAbsoluteLifetimeHours,
        RequireMfa);

    /// <summary>Validates and applies new settings. <paramref name="floor"/> is the platform baseline the tenant may not go below.</summary>
    public void Apply(SecurityPolicySettings settings, SecurityPolicySettings? floor)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = new Dictionary<string, string[]>();
        var minLength = Math.Max(MinPasswordLength, floor?.PasswordMinLength ?? MinPasswordLength);
        Range(errors, "passwordMinLength", settings.PasswordMinLength, minLength, MaxPasswordLength);
        Range(errors, "passwordHistoryCount", settings.PasswordHistoryCount, 0, MaxPasswordHistory);
        Range(errors, "passwordMaxAgeDays", settings.PasswordMaxAgeDays, 0, MaxPasswordAgeDays);
        Range(errors, "sessionIdleTimeoutMinutes", settings.SessionIdleTimeoutMinutes, MinIdleTimeoutMinutes, MaxIdleTimeoutMinutes);
        Range(errors, "sessionAbsoluteLifetimeHours", settings.SessionAbsoluteLifetimeHours, MinAbsoluteLifetimeHours, MaxAbsoluteLifetimeHours);
        if (settings.SessionIdleTimeoutMinutes > settings.SessionAbsoluteLifetimeHours * 60)
        {
            errors.TryAdd("sessionIdleTimeoutMinutes", ["The idle timeout cannot be longer than the absolute session lifetime."]);
        }

        if (floor is not null)
        {
            Floor(errors, "passwordRequireUppercase", settings.PasswordRequireUppercase, floor.PasswordRequireUppercase);
            Floor(errors, "passwordRequireLowercase", settings.PasswordRequireLowercase, floor.PasswordRequireLowercase);
            Floor(errors, "passwordRequireDigit", settings.PasswordRequireDigit, floor.PasswordRequireDigit);
            Floor(errors, "passwordRequireSymbol", settings.PasswordRequireSymbol, floor.PasswordRequireSymbol);
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        PasswordMinLength = settings.PasswordMinLength;
        PasswordRequireUppercase = settings.PasswordRequireUppercase;
        PasswordRequireLowercase = settings.PasswordRequireLowercase;
        PasswordRequireDigit = settings.PasswordRequireDigit;
        PasswordRequireSymbol = settings.PasswordRequireSymbol;
        PasswordHistoryCount = settings.PasswordHistoryCount;
        PasswordMaxAgeDays = settings.PasswordMaxAgeDays;
        SessionIdleTimeoutMinutes = settings.SessionIdleTimeoutMinutes;
        SessionAbsoluteLifetimeHours = settings.SessionAbsoluteLifetimeHours;
        RequireMfa = settings.RequireMfa;
    }

    private static void Range(Dictionary<string, string[]> errors, string name, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            errors[name] = [$"Must be between {min} and {max}."];
        }
    }

    private static void Floor(Dictionary<string, string[]> errors, string name, bool value, bool required)
    {
        if (required && !value)
        {
            errors[name] = ["This requirement is part of the platform baseline and cannot be turned off."];
        }
    }
}

/// <summary>Plain value form of a security policy, used for defaults, updates, caching and API contracts.</summary>
public sealed record SecurityPolicySettings(
    int PasswordMinLength,
    bool PasswordRequireUppercase,
    bool PasswordRequireLowercase,
    bool PasswordRequireDigit,
    bool PasswordRequireSymbol,
    int PasswordHistoryCount,
    int PasswordMaxAgeDays,
    int SessionIdleTimeoutMinutes,
    int SessionAbsoluteLifetimeHours,
    bool RequireMfa);
