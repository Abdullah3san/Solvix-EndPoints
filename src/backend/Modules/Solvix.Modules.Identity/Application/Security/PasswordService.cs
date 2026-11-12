namespace Solvix.Modules.Identity.Application.Security;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvix.Modules.Identity.Configuration;
using Solvix.Modules.Identity.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;

/// <summary>
/// The only place that hashes, verifies and changes passwords. Passwords are
/// hashed with PBKDF2-HMAC-SHA512 (per-password random salt, iteration count
/// from configuration) by ASP.NET Core's <see cref="PasswordHasher{TUser}"/>;
/// the plain text is never stored, logged or returned.
/// </summary>
public interface IPasswordService
{
    string Hash(User user, string password);

    /// <summary>Verifies a password; when the stored hash uses outdated parameters it is upgraded in place.</summary>
    bool Verify(User user, string password);

    /// <summary>Burns the same CPU time as a real verification, so an unknown e-mail is not distinguishable by timing.</summary>
    void VerifyDummy(string password);

    /// <summary>Rules for an account: the platform baseline tightened by every tenant the user is an active member of.</summary>
    Task<PasswordRules> GetRulesAsync(Guid? userId, Guid? additionalTenantId, CancellationToken cancellationToken);

    /// <summary>Validates <paramref name="newPassword"/> against the rules and history, then sets it. Throws <see cref="ValidationException"/>.</summary>
    Task ChangeAsync(User user, string newPassword, string fieldName, Guid? additionalTenantId, CancellationToken cancellationToken);

    /// <summary>Validates a password for an account that does not exist yet.</summary>
    void EnsureAcceptable(string password, PasswordRules rules, string fieldName, string? email, string? displayName);
}

internal sealed class PasswordService(
    ISolvixDbContext db,
    IPasswordHasher<User> hasher,
    IOptions<IdentitySecurityOptions> options,
    IClock clock) : IPasswordService
{
    private static readonly User DummyUser = User.Create("dummy@solvix.invalid", "dummy", "dummy", DateTimeOffset.UnixEpoch);
    private static string? _dummyHash;

    private readonly IdentitySecurityOptions _options = options.Value;

    public string Hash(User user, string password) => hasher.HashPassword(user, password);

    public bool Verify(User user, string password)
    {
        ArgumentNullException.ThrowIfNull(user);
        var result = hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.SetPasswordHash(hasher.HashPassword(user, password));
        }

        return result != PasswordVerificationResult.Failed;
    }

    public void VerifyDummy(string password)
    {
        var hash = _dummyHash ??= hasher.HashPassword(DummyUser, "solvix-dummy-password-never-valid");
        hasher.VerifyHashedPassword(DummyUser, hash, password ?? string.Empty);
    }

    public async Task<PasswordRules> GetRulesAsync(Guid? userId, Guid? additionalTenantId, CancellationToken cancellationToken)
    {
        var rules = PasswordRules.From(_options.Baseline());

        // Membership and policy rows of other tenants are needed here, so the tenant
        // filter is lifted explicitly; callers run this inside a platform scope.
        var tenantIds = new List<Guid>();
        if (userId is { } id)
        {
            tenantIds.AddRange(await db.Set<TenantMembership>()
                .IgnoreQueryFilters()
                .Where(m => m.UserId == id && m.IsActive)
                .Select(m => m.TenantId)
                .ToListAsync(cancellationToken));
        }

        if (additionalTenantId is { } extra)
        {
            tenantIds.Add(extra);
        }

        if (tenantIds.Count == 0)
        {
            return rules;
        }

        var policies = await db.Set<TenantSecurityPolicy>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => tenantIds.Contains(p.TenantId))
            .ToListAsync(cancellationToken);
        return policies.Aggregate(rules, (current, policy) => current.Strictest(PasswordRules.From(policy.ToSettings())));
    }

    public async Task ChangeAsync(User user, string newPassword, string fieldName, Guid? additionalTenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var rules = await GetRulesAsync(user.Id, additionalTenantId, cancellationToken);
        var errors = PasswordPolicy.Validate(newPassword, rules, user.Email, user.DisplayName).ToList();

        if (errors.Count == 0 && rules.HistoryCount > 0)
        {
            var recent = await db.Set<PasswordHistoryEntry>()
                .AsNoTracking()
                .Where(h => h.UserId == user.Id)
                .OrderByDescending(h => h.CreatedAtUtc)
                .Select(h => h.PasswordHash)
                .Take(rules.HistoryCount)
                .ToListAsync(cancellationToken);
            recent.Insert(0, user.PasswordHash);
            if (recent.Take(rules.HistoryCount).Any(hash => hasher.VerifyHashedPassword(user, hash, newPassword) != PasswordVerificationResult.Failed))
            {
                errors.Add($"You cannot reuse any of your last {rules.HistoryCount} passwords.");
            }
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(new Dictionary<string, string[]> { [fieldName] = [.. errors] });
        }

        db.Set<PasswordHistoryEntry>().Add(PasswordHistoryEntry.Record(user.Id, user.PasswordHash));
        user.ChangePassword(hasher.HashPassword(user, newPassword), clock.UtcNow);
    }

    public void EnsureAcceptable(string password, PasswordRules rules, string fieldName, string? email, string? displayName)
    {
        var errors = PasswordPolicy.Validate(password, rules, email, displayName);
        if (errors.Count > 0)
        {
            throw new ValidationException(new Dictionary<string, string[]> { [fieldName] = [.. errors] });
        }
    }
}
