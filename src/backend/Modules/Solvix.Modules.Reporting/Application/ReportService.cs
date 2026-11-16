namespace Solvix.Modules.Reporting.Application;

using Microsoft.EntityFrameworkCore;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;

public interface IReportService
{
    IReadOnlyList<ReportInfo> List();

    Task<ReportResult> RunAsync(string key, ReportQuery query, CancellationToken cancellationToken);
}

internal sealed class ReportService(IEnumerable<IReport> reports) : IReportService
{
    public IReadOnlyList<ReportInfo> List() =>
        reports.OrderBy(r => r.Key).Select(r => new ReportInfo(r.Key, r.Title, r.Description, r.Filters)).ToList();

    public Task<ReportResult> RunAsync(string key, ReportQuery query, CancellationToken cancellationToken)
    {
        var report = reports.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase)) ?? throw new NotFoundException("Report", key);
        return report.RunAsync(query, cancellationToken);
    }
}

/// <summary>Records one availability sample per tenant and hour (atomic upsert; safe under concurrency and re-runs).</summary>
internal sealed class AvailabilityRecorder(ISolvixDbContext db, IClock clock)
{
    public async Task RecordAsync(Guid tenantId, int onlineAfterSeconds, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var hour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        var onlineSince = now.AddSeconds(-onlineAfterSeconds);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO availability_snapshots (id, tenant_id, hour_utc, active_devices, online_devices, created_at_utc, updated_at_utc)
            SELECT {Guid.CreateVersion7()}, {tenantId}, {hour},
                   count(*) FILTER (WHERE status = 'Active'),
                   count(*) FILTER (WHERE status = 'Active' AND last_seen_at_utc >= {onlineSince}),
                   {now}, {now}
            FROM devices
            ON CONFLICT (tenant_id, hour_utc) DO UPDATE
                SET active_devices = EXCLUDED.active_devices, online_devices = EXCLUDED.online_devices, updated_at_utc = EXCLUDED.updated_at_utc
            """,
            cancellationToken);
    }
}
