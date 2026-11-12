namespace Solvix.Infrastructure.Caching;

using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

/// <summary>
/// Decorates the Redis cache so that an unavailable cache degrades to a cache
/// miss instead of failing the request. Every consumer of
/// <see cref="IDistributedCache"/> in Solvix treats the cache as an
/// optimisation over the database, never as the source of truth.
/// </summary>
internal sealed partial class ResilientDistributedCache(IDistributedCache inner, ILogger<ResilientDistributedCache> logger) : IDistributedCache
{
    /// <summary>After a failure the cache is skipped entirely for this long, so a Redis outage costs one timeout, not one per call.</summary>
    public static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(15);

    private long _openUntilTicks;

    public bool IsCircuitOpen => Volatile.Read(ref _openUntilTicks) > DateTime.UtcNow.Ticks;

    public byte[]? Get(string key)
    {
        if (IsCircuitOpen)
        {
            return null;
        }

        try
        {
            return inner.Get(key);
        }
        catch (Exception ex) when (IsCacheFailure(ex))
        {
            Trip();
            LogCacheFailure(logger, ex, "GET", key);
            return null;
        }
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        if (IsCircuitOpen)
        {
            return null;
        }

        try
        {
            return await inner.GetAsync(key, token);
        }
        catch (Exception ex) when (IsCacheFailure(ex))
        {
            Trip();
            LogCacheFailure(logger, ex, "GET", key);
            return null;
        }
    }

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        if (IsCircuitOpen)
        {
            return;
        }

        try
        {
            inner.Set(key, value, options);
        }
        catch (Exception ex) when (IsCacheFailure(ex))
        {
            Trip();
            LogCacheFailure(logger, ex, "SET", key);
        }
    }

    public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        if (IsCircuitOpen)
        {
            return;
        }

        try
        {
            await inner.SetAsync(key, value, options, token);
        }
        catch (Exception ex) when (IsCacheFailure(ex))
        {
            Trip();
            LogCacheFailure(logger, ex, "SET", key);
        }
    }

    public void Refresh(string key)
    {
        if (IsCircuitOpen)
        {
            return;
        }

        try
        {
            inner.Refresh(key);
        }
        catch (Exception ex) when (IsCacheFailure(ex))
        {
            Trip();
            LogCacheFailure(logger, ex, "REFRESH", key);
        }
    }

    public async Task RefreshAsync(string key, CancellationToken token = default)
    {
        if (IsCircuitOpen)
        {
            return;
        }

        try
        {
            await inner.RefreshAsync(key, token);
        }
        catch (Exception ex) when (IsCacheFailure(ex))
        {
            Trip();
            LogCacheFailure(logger, ex, "REFRESH", key);
        }
    }

    public void Remove(string key)
    {
        if (IsCircuitOpen)
        {
            return;
        }

        try
        {
            inner.Remove(key);
        }
        catch (Exception ex) when (IsCacheFailure(ex))
        {
            Trip();
            LogCacheFailure(logger, ex, "REMOVE", key);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken token = default)
    {
        if (IsCircuitOpen)
        {
            return;
        }

        try
        {
            await inner.RemoveAsync(key, token);
        }
        catch (Exception ex) when (IsCacheFailure(ex))
        {
            Trip();
            LogCacheFailure(logger, ex, "REMOVE", key);
        }
    }

    private void Trip() => Volatile.Write(ref _openUntilTicks, DateTime.UtcNow.Add(BreakDuration).Ticks);

    private static bool IsCacheFailure(Exception ex) => ex is not OperationCanceledException;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Distributed cache unavailable for {Operation} {Key}; continuing without cache")]
    private static partial void LogCacheFailure(ILogger logger, Exception exception, string operation, string key);
}
