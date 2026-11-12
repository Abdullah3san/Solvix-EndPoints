namespace Solvix.Modules.Identity.Application.Security;

using System.Collections.Frozen;
using Solvix.Modules.Identity.Domain;

/// <summary>The password rules that apply to one account at one moment.</summary>
public sealed record PasswordRules(
    int MinLength,
    bool RequireUppercase,
    bool RequireLowercase,
    bool RequireDigit,
    bool RequireSymbol,
    int HistoryCount,
    int MaxAgeDays)
{
    public const int MaxLength = 256;

    public static PasswordRules From(SecurityPolicySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new PasswordRules(
            settings.PasswordMinLength,
            settings.PasswordRequireUppercase,
            settings.PasswordRequireLowercase,
            settings.PasswordRequireDigit,
            settings.PasswordRequireSymbol,
            settings.PasswordHistoryCount,
            settings.PasswordMaxAgeDays);
    }

    /// <summary>
    /// Combines two rule sets into the stricter of each rule. A user who belongs
    /// to several tenants must satisfy all of them, because the password is one
    /// credential shared by every membership.
    /// </summary>
    public PasswordRules Strictest(PasswordRules other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new PasswordRules(
            Math.Max(MinLength, other.MinLength),
            RequireUppercase || other.RequireUppercase,
            RequireLowercase || other.RequireLowercase,
            RequireDigit || other.RequireDigit,
            RequireSymbol || other.RequireSymbol,
            Math.Max(HistoryCount, other.HistoryCount),
            (MaxAgeDays, other.MaxAgeDays) switch
            {
                (0, var b) => b,
                (var a, 0) => a,
                var (a, b) => Math.Min(a, b),
            });
    }
}

public static class PasswordPolicy
{
    /// <summary>
    /// Passwords that meet any composition rule on paper yet are among the first
    /// an attacker tries. Compared case-insensitively.
    /// </summary>
    private static readonly FrozenSet<string> Common = new[]
    {
        "password123", "password1234", "password12345", "password123!", "passw0rd123", "p@ssw0rd123", "p@ssword123",
        "1234567890", "12345678910", "123456789012", "0987654321", "1q2w3e4r5t", "1q2w3e4r5t6y", "q1w2e3r4t5",
        "qwertyuiop", "qwerty12345", "qwerty123456", "qwertyuiop1", "qwertyuiop123", "asdfghjkl1", "1qaz2wsx3edc",
        "zaq12wsxcde3", "iloveyou123", "iloveyou1234", "welcome123", "welcome1234", "welcome12345", "letmein123",
        "letmein1234", "admin12345", "admin123456", "administrator", "administrator1", "changeme123", "changeme1234",
        "football123", "baseball123", "superman123", "trustno1234", "monkey12345", "dragon12345", "sunshine123",
        "princess123", "abc1234567", "abcd123456", "abcdefghij", "abcdefghij1", "aaaaaaaaaa", "aaaaaaaaaa1",
        "1111111111", "0000000000", "summer2024", "summer2025", "summer2026", "winter2024", "winter2025", "winter2026",
        "spring2025", "spring2026", "autumn2025", "autumn2026", "solvix12345", "solvix123456",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the reasons <paramref name="password"/> is not acceptable; empty when it is.</summary>
    public static IReadOnlyList<string> Validate(string? password, PasswordRules rules, string? email = null, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var errors = new List<string>();
        if (string.IsNullOrEmpty(password))
        {
            errors.Add("A password is required.");
            return errors;
        }

        if (password.Length < rules.MinLength)
        {
            errors.Add($"Must be at least {rules.MinLength} characters.");
        }

        if (password.Length > PasswordRules.MaxLength)
        {
            errors.Add($"Must be at most {PasswordRules.MaxLength} characters.");
        }

        if (rules.RequireUppercase && !password.Any(char.IsUpper))
        {
            errors.Add("Must contain an upper-case letter.");
        }

        if (rules.RequireLowercase && !password.Any(char.IsLower))
        {
            errors.Add("Must contain a lower-case letter.");
        }

        if (rules.RequireDigit && !password.Any(char.IsDigit))
        {
            errors.Add("Must contain a digit.");
        }

        if (rules.RequireSymbol && password.All(char.IsLetterOrDigit))
        {
            errors.Add("Must contain a symbol.");
        }

        if (Common.Contains(password))
        {
            errors.Add("This password is too common. Choose something harder to guess.");
        }

        if (ContainsIdentity(password, email, displayName))
        {
            errors.Add("Must not contain your name or e-mail address.");
        }

        return errors;
    }

    private static bool ContainsIdentity(string password, string? email, string? displayName)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(email))
        {
            var local = email.Split('@')[0];
            parts.Add(email);
            parts.Add(local);
        }

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            parts.Add(displayName.Replace(" ", string.Empty, StringComparison.Ordinal));
        }

        // Very short fragments ("al", "jo") would reject far too many good passwords.
        return parts.Exists(p => p.Length >= 4 && password.Contains(p, StringComparison.OrdinalIgnoreCase));
    }
}
