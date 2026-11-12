namespace Solvix.Modules.Identity.Application.Security;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvix.Modules.Identity.Configuration;
using Solvix.Modules.Identity.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Persistence;

/// <summary>
/// Account lockout. Every credential check (password or MFA code) first reserves
/// one of the remaining attempts with a single atomic UPDATE, and only then is
/// the secret verified. Guesses sent in parallel therefore cannot outrun the
/// counter: at most <c>MaxFailedAttempts</c> verifications can ever be in flight.
/// </summary>
public interface ILoginAttemptTracker
{
    /// <summary>Reserves an attempt. False means the account is locked or out of attempts: do not verify anything.</summary>
    Task<bool> TryBeginAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Gives a reserved attempt back when the secret was correct but the sign-in is not finished yet (password accepted, MFA pending).</summary>
    Task ReleaseAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Call after a failed verification. Returns the lockout end when this failure locked the account.</summary>
    Task<DateTimeOffset?> RecordFailureAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Call after a fully successful sign-in: clears the counters.</summary>
    Task RecordSuccessAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Clears a lockout (password reset proves control of the mailbox).</summary>
    Task ClearAsync(Guid userId, CancellationToken cancellationToken);
}

internal sealed class LoginAttemptTracker(ISolvixDbContext db, IClock clock, IOptions<IdentitySecurityOptions> options)
    : ILoginAttemptTracker
{
    private readonly LockoutOptions _lockout = options.Value.Lockout;

    public async Task<bool> TryBeginAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var max = _lockout.MaxFailedAttempts;
        var reserved = await db.Set<User>()
            .Where(u => u.Id == userId
                && (u.LockoutEndUtc == null || u.LockoutEndUtc <= now)
                && u.FailedAccessCount < max)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.FailedAccessCount, u => u.FailedAccessCount + 1), cancellationToken);
        return reserved == 1;
    }

    public Task ReleaseAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Set<User>()
            .Where(u => u.Id == userId && u.FailedAccessCount > 0)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.FailedAccessCount, u => u.FailedAccessCount - 1), cancellationToken);

    public async Task<DateTimeOffset?> RecordFailureAsync(Guid userId, CancellationToken cancellationToken)
    {
        // The attempt was already counted by TryBeginAsync; here we only turn a full counter into a lockout.
        var state = await db.Set<User>()
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.FailedAccessCount, u.LockoutCount })
            .FirstOrDefaultAsync(cancellationToken);
        if (state is null || state.FailedAccessCount < _lockout.MaxFailedAttempts)
        {
            return null;
        }

        var max = _lockout.MaxFailedAttempts;
        var until = clock.UtcNow.Add(_lockout.DurationFor(state.LockoutCount));
        var locked = await db.Set<User>()
            .Where(u => u.Id == userId && u.FailedAccessCount >= max)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(u => u.LockoutEndUtc, (DateTimeOffset?)until)
                    .SetProperty(u => u.LockoutCount, u => u.LockoutCount + 1)
                    .SetProperty(u => u.FailedAccessCount, 0),
                cancellationToken);
        return locked == 1 ? until : null;
    }

    public Task RecordSuccessAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        return db.Set<User>()
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(u => u.FailedAccessCount, 0)
                    .SetProperty(u => u.LockoutCount, 0)
                    .SetProperty(u => u.LockoutEndUtc, (DateTimeOffset?)null)
                    .SetProperty(u => u.LastLoginAtUtc, (DateTimeOffset?)now),
                cancellationToken);
    }

    public Task ClearAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Set<User>()
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(u => u.FailedAccessCount, 0)
                    .SetProperty(u => u.LockoutCount, 0)
                    .SetProperty(u => u.LockoutEndUtc, (DateTimeOffset?)null),
                cancellationToken);
}
