namespace Solvix.Modules.Identity.Application.Security;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Identity.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Persistence;

/// <summary>Issues and redeems single-use tokens (verification links, reset links, MFA challenges).</summary>
public interface IOneTimeTokenService
{
    /// <summary>Adds a token to the unit of work and returns the secret to deliver. Older tokens of the same purpose stop working.</summary>
    Task<string> IssueAsync(Guid userId, UserTokenPurpose purpose, TimeSpan lifetime, string? data, CancellationToken cancellationToken);

    /// <summary>Finds an unused, unexpired token of the given purpose, with its user.</summary>
    Task<UserToken?> FindUsableAsync(string? token, UserTokenPurpose purpose, CancellationToken cancellationToken);

    /// <summary>Atomically marks the token used. Returns false when another request got there first.</summary>
    Task<bool> TryConsumeAsync(UserToken token, CancellationToken cancellationToken);

    /// <summary>Atomically counts a failed attempt and returns the new total.</summary>
    Task<int> RegisterFailedAttemptAsync(UserToken token, CancellationToken cancellationToken);

    Task<DateTimeOffset?> LastIssuedAtAsync(Guid userId, UserTokenPurpose purpose, CancellationToken cancellationToken);
}

internal sealed class OneTimeTokenService(ISolvixDbContext db, IClock clock) : IOneTimeTokenService
{
    public async Task<string> IssueAsync(Guid userId, UserTokenPurpose purpose, TimeSpan lifetime, string? data, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await db.Set<UserToken>()
            .Where(t => t.UserId == userId && t.Purpose == purpose && t.ConsumedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ConsumedAtUtc, (DateTimeOffset?)now), cancellationToken);

        var token = TokenGenerator.NewToken();
        db.Set<UserToken>().Add(UserToken.Issue(userId, purpose, TokenGenerator.Hash(token), now.Add(lifetime), data));
        return token;
    }

    public async Task<UserToken?> FindUsableAsync(string? token, UserTokenPurpose purpose, CancellationToken cancellationToken)
    {
        if (!TokenGenerator.LooksLikeToken(token))
        {
            return null;
        }

        var hash = TokenGenerator.Hash(token!);
        var found = await db.Set<UserToken>()
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.Purpose == purpose, cancellationToken);
        return found is not null && found.IsUsable(clock.UtcNow) ? found : null;
    }

    public async Task<bool> TryConsumeAsync(UserToken token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        var now = clock.UtcNow;
        var rows = await db.Set<UserToken>()
            .Where(t => t.Id == token.Id && t.ConsumedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ConsumedAtUtc, (DateTimeOffset?)now), cancellationToken);
        return rows == 1;
    }

    public async Task<int> RegisterFailedAttemptAsync(UserToken token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        await db.Set<UserToken>()
            .Where(t => t.Id == token.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Attempts, t => t.Attempts + 1), cancellationToken);
        return await db.Set<UserToken>().AsNoTracking().Where(t => t.Id == token.Id).Select(t => t.Attempts).FirstAsync(cancellationToken);
    }

    public Task<DateTimeOffset?> LastIssuedAtAsync(Guid userId, UserTokenPurpose purpose, CancellationToken cancellationToken) =>
        db.Set<UserToken>()
            .AsNoTracking()
            .Where(t => t.UserId == userId && t.Purpose == purpose)
            .OrderByDescending(t => t.CreatedAtUtc)
            .Select(t => (DateTimeOffset?)t.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
}
