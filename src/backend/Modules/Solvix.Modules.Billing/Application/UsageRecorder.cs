namespace Solvix.Modules.Billing.Application;

using Microsoft.EntityFrameworkCore;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Persistence;

/// <summary>Records the per-day peak of billable devices (atomic upsert, safe under concurrency).</summary>
internal sealed class UsageRecorder(ISolvixDbContext db, IClock clock)
{
    public async Task RecordAsync(Guid tenantId, int devices, int limit, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO usage_snapshots (id, tenant_id, day, peak_devices, device_limit, recorded_at_utc, created_at_utc, updated_at_utc)
            VALUES ({Guid.CreateVersion7()}, {tenantId}, {day}, {devices}, {limit}, {now}, {now}, {now})
            ON CONFLICT (tenant_id, day) DO UPDATE
                SET peak_devices = GREATEST(usage_snapshots.peak_devices, EXCLUDED.peak_devices),
                    device_limit = EXCLUDED.device_limit,
                    recorded_at_utc = EXCLUDED.recorded_at_utc,
                    updated_at_utc = EXCLUDED.updated_at_utc
            """,
            cancellationToken);
    }
}
