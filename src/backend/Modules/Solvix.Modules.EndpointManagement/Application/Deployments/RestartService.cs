namespace Solvix.Modules.EndpointManagement.Application.Deployments;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Devices.Domain;
using Solvix.Modules.EndpointManagement.Domain.Deployments;
using Solvix.Modules.EndpointManagement.Domain.MaintenanceWindows;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Auditing;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Tenancy;

public interface IRestartService
{
    Task<PagedResult<DeviceRestartDto>> ListAsync(RestartListQuery query, CancellationToken cancellationToken);

    Task<DeviceRestartDto> CreateAsync(CreateRestartRequest request, CancellationToken cancellationToken);

    Task<DeviceRestartDto> CancelAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed class RestartService(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IClock clock,
    IAuditTrail audit) : IRestartService
{
    public async Task<PagedResult<DeviceRestartDto>> ListAsync(RestartListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var rows =
            from r in db.Set<DeviceRestartRequest>().AsNoTracking()
            join d in db.Set<Device>().AsNoTracking() on r.DeviceId equals d.Id
            select new { Restart = r, d.Hostname };
        if (query.DeviceId is { } deviceId)
        {
            rows = rows.Where(x => x.Restart.DeviceId == deviceId);
        }

        if (query.OpenOnly)
        {
            rows = rows.Where(x => x.Restart.Status == RestartRequestStatus.Pending || x.Restart.Status == RestartRequestStatus.Dispatched || x.Restart.Status == RestartRequestStatus.Initiated);
        }

        var total = await rows.LongCountAsync(cancellationToken);
        var items = await rows.OrderByDescending(x => x.Restart.CreatedAtUtc).Skip(page.Skip).Take(page.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<DeviceRestartDto>(items.Select(x => ToDto(x.Restart, x.Hostname)).ToList(), page.SafePage, page.SafePageSize, total);
    }

    public async Task<DeviceRestartDto> CreateAsync(CreateRestartRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var device = await db.Set<Device>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == request.DeviceId && d.Status == DeviceStatus.Active, cancellationToken)
            ?? throw new NotFoundException("Device", request.DeviceId);
        if (request.MaintenanceWindowId is { } windowId && !await db.Set<MaintenanceWindow>().AnyAsync(w => w.Id == windowId, cancellationToken))
        {
            throw new ValidationException("maintenanceWindowId", "Maintenance window not found.");
        }

        if (request.Force && currentUser.Role != TenantRole.Owner)
        {
            throw new ForbiddenException("A restart that may ignore the maintenance window requires a workspace Owner.");
        }

        if (await db.Set<DeviceRestartRequest>().AnyAsync(
                r => r.DeviceId == request.DeviceId && (r.Status == RestartRequestStatus.Pending || r.Status == RestartRequestStatus.Dispatched || r.Status == RestartRequestStatus.Initiated),
                cancellationToken))
        {
            throw new ConflictException("The device already has an open restart request.");
        }

        var now = clock.UtcNow;
        var restart = new DeviceRestartRequest(
            tenantContext.RequireTenantId(),
            request.DeviceId,
            RestartSource.Manual,
            null,
            request.Reason.Trim(),
            request.NotBeforeUtc ?? now,
            request.DeadlineUtc,
            request.MaintenanceWindowId,
            request.Force,
            request.GracePeriodSeconds,
            currentUser.RequireUserId());
        db.Set<DeviceRestartRequest>().Add(restart);
        audit.Record("restart.requested", "Device", request.DeviceId, $"Restart of '{device.Hostname}' requested: {restart.Reason}", request);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(restart, device.Hostname);
    }

    public async Task<DeviceRestartDto> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        var restart = await db.Set<DeviceRestartRequest>().FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new NotFoundException("RestartRequest", id);
        if (restart.Status is not (RestartRequestStatus.Pending or RestartRequestStatus.Dispatched))
        {
            throw new ConflictException($"A {restart.Status} restart cannot be cancelled.");
        }

        restart.Cancel(clock.UtcNow);
        audit.Record("restart.cancelled", "Device", restart.DeviceId, $"Restart request cancelled: {restart.Reason}", new { restartId = id });
        await db.SaveChangesAsync(cancellationToken);
        var hostname = await db.Set<Device>().Where(d => d.Id == restart.DeviceId).Select(d => d.Hostname).FirstAsync(cancellationToken);
        return ToDto(restart, hostname);
    }

    private static DeviceRestartDto ToDto(DeviceRestartRequest r, string hostname) => new(
        r.Id, r.DeviceId, hostname, r.Source, r.DeploymentId, r.Reason, r.Status, r.NotBeforeUtc, r.DeadlineUtc, r.MaintenanceWindowId,
        r.Force, r.GracePeriodSeconds, r.DispatchedAtUtc, r.InitiatedAtUtc, r.CompletedAtUtc, r.Message, r.CreatedAtUtc);
}
