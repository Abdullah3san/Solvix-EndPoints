namespace Solvix.Modules.Identity.Application.Access;

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Distributed;

/// <summary>
/// Cache keys that must be removed once the current unit of work is durable.
///
/// Removing a key before the change is committed is not enough: a concurrent
/// request can re-read the old row and put it straight back, and a revoked
/// session would then stay usable until the entry expires. So keys are removed
/// a second time after the commit, by the interceptors below.
/// </summary>
internal sealed class AccessCacheInvalidations
{
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    public void Add(string key) => _keys.Add(key);

    public async Task FlushAsync(IDistributedCache cache, CancellationToken cancellationToken)
    {
        if (_keys.Count == 0)
        {
            return;
        }

        var keys = _keys.ToArray();
        _keys.Clear();
        foreach (var key in keys)
        {
            await cache.RemoveAsync(key, cancellationToken);
        }
    }
}

/// <summary>Flushes after a SaveChanges that committed on its own (no surrounding transaction).</summary>
internal sealed class AccessCacheSaveChangesInterceptor(AccessCacheInvalidations pending, IDistributedCache cache) : SaveChangesInterceptor
{
    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context?.Database.CurrentTransaction is null)
        {
            await pending.FlushAsync(cache, cancellationToken);
        }

        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context?.Database.CurrentTransaction is null)
        {
            pending.FlushAsync(cache, CancellationToken.None).GetAwaiter().GetResult();
        }

        return base.SavedChanges(eventData, result);
    }
}

/// <summary>Flushes when an explicit transaction ends.</summary>
internal sealed class AccessCacheTransactionInterceptor(AccessCacheInvalidations pending, IDistributedCache cache) : DbTransactionInterceptor
{
    public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await pending.FlushAsync(cache, cancellationToken);
        await base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
    }

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        pending.FlushAsync(cache, CancellationToken.None).GetAwaiter().GetResult();
        base.TransactionCommitted(transaction, eventData);
    }

    // Nothing changed, but entries were already removed once: removing them again is harmless and keeps the queue empty.
    public override async Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await pending.FlushAsync(cache, cancellationToken);
        await base.TransactionRolledBackAsync(transaction, eventData, cancellationToken);
    }
}
