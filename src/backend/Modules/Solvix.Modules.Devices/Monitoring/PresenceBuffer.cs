namespace Solvix.Modules.Devices.Monitoring;

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

/// <summary>
/// Write-coalescing buffer for agent contact timestamps. Every heartbeat would
/// otherwise be one UPDATE; instead the latest contact per device is kept in
/// memory and written in ONE statement per tenant every flush interval (inside a
/// tenant scope, so Row-Level Security applies). Losing the buffer (crash) loses
/// at most one flush interval of contact, which the monitor's tolerance covers.
/// </summary>
public sealed partial class PresenceBuffer(IServiceScopeFactory scopes, ILogger<PresenceBuffer> logger)
{
    private ConcurrentDictionary<Guid, Entry> _entries = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    public int Pending => _entries.Count;

    public void Record(Guid tenantId, Guid deviceId, DateTimeOffset seenAt, bool successful, bool heartbeat)
    {
        _entries.AddOrUpdate(
            deviceId,
            _ => new Entry(tenantId, seenAt, successful ? seenAt : null, heartbeat ? seenAt : null),
            (_, existing) => existing.Merge(seenAt, successful, heartbeat));
    }

    /// <summary>Writes everything buffered so far. Safe to call concurrently (serialised).</summary>
    public async Task<int> FlushAsync(CancellationToken cancellationToken)
    {
        await _flushGate.WaitAsync(cancellationToken);
        try
        {
            var drained = Interlocked.Exchange(ref _entries, new ConcurrentDictionary<Guid, Entry>());
            if (drained.IsEmpty)
            {
                return 0;
            }

            var written = 0;
            foreach (var tenantBatch in drained.GroupBy(e => e.Value.TenantId))
            {
                var batch = tenantBatch.ToList();
                try
                {
                    written += await WriteTenantAsync(tenantBatch.Key, batch, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Keep the data for the next attempt rather than losing contact evidence.
                    foreach (var (deviceId, entry) in batch)
                    {
                        _entries.AddOrUpdate(deviceId, entry, (_, newer) => newer.Merge(entry));
                    }

                    LogFlushFailed(logger, ex, tenantBatch.Key, batch.Count);
                }
            }

            return written;
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private async Task<int> WriteTenantAsync(Guid tenantId, List<KeyValuePair<Guid, Entry>> batch, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextMutator>().SetTenant(tenantId, null);
        var db = scope.ServiceProvider.GetRequiredService<ISolvixDbContext>();

        const string sql =
            """
            UPDATE device_presence AS p SET
                last_seen_at_utc = GREATEST(p.last_seen_at_utc, v.seen),
                last_communication_at_utc = GREATEST(p.last_communication_at_utc, v.communicated),
                last_heartbeat_at_utc = GREATEST(p.last_heartbeat_at_utc, v.heartbeat)
            FROM unnest(@ids, @seen, @communicated, @heartbeat) AS v(id, seen, communicated, heartbeat)
            WHERE p.id = v.id AND p.tenant_id = @tenant
            RETURNING p.id, p.state, v.seen, v.communicated IS NOT NULL
            """;

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(cancellationToken); // runs the tenant session interceptor
        var stale = new List<(Guid Id, DateTimeOffset Seen, bool Successful)>();
        var updated = 0;
        try
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = batch.Select(b => b.Key).ToArray() });
            command.Parameters.Add(new NpgsqlParameter("seen", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = batch.Select(b => b.Value.SeenAt.UtcDateTime).ToArray() });
            command.Parameters.Add(new NpgsqlParameter("communicated", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = batch.Select(b => b.Value.CommunicatedAt?.UtcDateTime).ToArray() });
            command.Parameters.Add(new NpgsqlParameter("heartbeat", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = batch.Select(b => b.Value.HeartbeatAt?.UtcDateTime).ToArray() });
            command.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlDbType.Uuid) { Value = tenantId });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                updated++;
                if (!string.Equals(reader.GetString(1), nameof(ConnectivityState.Online), StringComparison.Ordinal))
                {
                    stale.Add((reader.GetGuid(0), new DateTimeOffset(reader.GetDateTime(2), TimeSpan.Zero), reader.GetBoolean(3)));
                }
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        if (stale.Count > 0)
        {
            // Contact from a device that is (now) Warning/Offline: record the recovery properly.
            var tracker = scope.ServiceProvider.GetRequiredService<PresenceTracker>();
            foreach (var (id, seen, successful) in stale)
            {
                await tracker.RecordContactAsync(id, seen, successful, cancellationToken);
            }
        }

        return updated;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Flushing {Count} buffered device contacts of tenant {TenantId} failed; will retry")]
    private static partial void LogFlushFailed(ILogger logger, Exception exception, Guid tenantId, int count);

    private sealed record Entry(Guid TenantId, DateTimeOffset SeenAt, DateTimeOffset? CommunicatedAt, DateTimeOffset? HeartbeatAt)
    {
        public Entry Merge(DateTimeOffset seenAt, bool successful, bool heartbeat) => new(
            TenantId,
            Later(SeenAt, seenAt),
            successful ? Later(CommunicatedAt, seenAt) : CommunicatedAt,
            heartbeat ? Later(HeartbeatAt, seenAt) : HeartbeatAt);

        public Entry Merge(Entry other) => new(
            TenantId,
            Later(SeenAt, other.SeenAt),
            Later(CommunicatedAt, other.CommunicatedAt),
            Later(HeartbeatAt, other.HeartbeatAt));

        private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

        private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b) =>
            a is null ? b : b is null ? a : a > b ? a : b;
    }
}
