namespace Solvix.Modules.Identity.Domain;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Guards;

/// <summary>
/// A platform-level person identity. Users are NOT tenant-owned: one person can
/// belong to several tenants through <see cref="TenantMembership"/>. Email is
/// globally unique (case-insensitive).
/// </summary>
public sealed class User : Entity
{
    public const int EmailMaxLength = 320;
    public const int DisplayNameMaxLength = 200;

    private User()
    {
    }

    private User(string email, string displayName, string passwordHash)
    {
        Email = email;
        NormalizedEmail = NormalizeEmail(email);
        DisplayName = displayName;
        PasswordHash = passwordHash;
        IsActive = true;
    }

    public string Email { get; private set; } = string.Empty;

    public string NormalizedEmail { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public string PasswordHash { get; private set; } = string.Empty;

    public bool IsActive { get; private set; }

    public DateTimeOffset? LastLoginAtUtc { get; private set; }

    public static User Create(string email, string displayName, string passwordHash)
    {
        var cleanEmail = Guard.NotNullOrWhiteSpace(email, "email", EmailMaxLength);
        var cleanName = Guard.NotNullOrWhiteSpace(displayName, "displayName", DisplayNameMaxLength);
        Guard.NotNullOrWhiteSpace(passwordHash, "passwordHash");
        return new User(cleanEmail, cleanName, passwordHash);
    }

    public static string NormalizeEmail(string email) => email.Trim().ToUpperInvariant();

    public void RecordLogin(DateTimeOffset at) => LastLoginAtUtc = at;

    public void Rename(string displayName) =>
        DisplayName = Guard.NotNullOrWhiteSpace(displayName, "displayName", DisplayNameMaxLength);

    public void SetPasswordHash(string passwordHash) =>
        PasswordHash = Guard.NotNullOrWhiteSpace(passwordHash, "passwordHash");

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;
}
