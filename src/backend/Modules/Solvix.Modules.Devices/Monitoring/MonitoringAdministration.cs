namespace Solvix.Modules.Devices.Monitoring;

using Microsoft.EntityFrameworkCore;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Tenancy;

public interface IMonitoringAdministration
{
    Task<MonitoringSettingsDto> GetSettingsAsync(CancellationToken cancellationToken);

    Task<MonitoringSettingsDto> UpdateSettingsAsync(UpdateMonitoringSettingsRequest request, CancellationToken cancellationToken);

    Task<PagedResult<MonitoringAlertDto>> ListAlertsAsync(AlertQuery query, CancellationToken cancellationToken);

    Task<MonitoringAlertDto> AcknowledgeAlertAsync(Guid alertId, CancellationToken cancellationToken);
}

internal sealed class MonitoringAdministration(
    ISolvixDbContext db,
    ITenantContext tenant,
    ICurrentUser currentUser,
    MonitoringSettingsProvider settingsProvider,
    IClock clock) : IMonitoringAdministration
{
    public async Task<MonitoringSettingsDto> GetSettingsAsync(CancellationToken cancellationToken) =>
        (await settingsProvider.GetAsync(cancellationToken)).ToDto();

    /// <summary>
    /// Takes effect immediately for evaluation on this node (other nodes within the
    /// settings cache lifetime); agents adopt a new heartbeat interval with their
    /// next heartbeat response.
    /// </summary>
    public async Task<MonitoringSettingsDto> UpdateSettingsAsync(UpdateMonitoringSettingsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenantId = tenant.RequireTenantId();
        var row = await db.Set<MonitoringSettings>().FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            row = new MonitoringSettings(tenantId);
            db.Set<MonitoringSettings>().Add(row);
        }

        row.Update(request.HeartbeatIntervalSeconds, request.WarningAfterSeconds, request.OfflineAfterSeconds, request.OfflineAlertsEnabled, request.RecoveryNotificationsEnabled);
        await db.SaveChangesAsync(cancellationToken);
        settingsProvider.Invalidate(tenantId);
        return MonitoringSettingsProvider.From(row).ToDto();
    }

    public async Task<PagedResult<MonitoringAlertDto>> ListAlertsAsync(AlertQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var alerts = db.Set<MonitoringAlert>().AsNoTracking();
        if (query.Status is { } status)
        {
            alerts = alerts.Where(a => a.Status == status);
        }

        if (query.DeviceId is { } deviceId)
        {
            alerts = alerts.Where(a => a.DeviceId == deviceId);
        }

        var total = await alerts.LongCountAsync(cancellationToken);
        var items = await alerts.OrderByDescending(a => a.OpenedAtUtc).ThenByDescending(a => a.Id)
            .Skip(page.Skip).Take(page.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<MonitoringAlertDto>(items.Select(MonitoringAlertDto.From).ToList(), page.SafePage, page.SafePageSize, total);
    }

    public async Task<MonitoringAlertDto> AcknowledgeAlertAsync(Guid alertId, CancellationToken cancellationToken)
    {
        var alert = await db.Set<MonitoringAlert>().FirstOrDefaultAsync(a => a.Id == alertId, cancellationToken)
            ?? throw new NotFoundException("Alert", alertId);
        alert.Acknowledge(currentUser.UserId ?? throw new UnauthorizedException(), clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return MonitoringAlertDto.From(alert);
    }
}
