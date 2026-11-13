namespace Solvix.Modules.Devices.Application.Enrollment;

using Microsoft.EntityFrameworkCore;
using Solvix.Endpoint.Contracts.Agent;
using Solvix.Modules.Devices.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;

/// <summary>
/// Supplies the work an agent should pick up on its heartbeat. Implemented by the
/// EndpointManagement module (patch jobs, restarts, configuration revision) so
/// that this module does not depend on it.
/// </summary>
public interface IAgentWorkProvider
{
    Task<AgentWorkResponse> GetWorkAsync(DeviceHeartbeat heartbeat, CancellationToken cancellationToken);
}

/// <summary>What changed on this heartbeat; <see cref="Rebooted"/> drives restart tracking.</summary>
public sealed record DeviceHeartbeat(Guid DeviceId, DateTimeOffset? PreviousBootUtc, DateTimeOffset CurrentBootUtc, DateTimeOffset ReceivedAtUtc)
{
    public bool Rebooted => PreviousBootUtc is { } previous && CurrentBootUtc > previous.AddSeconds(30);
}

public interface IAgentHeartbeatService
{
    Task<AgentWorkResponse> HeartbeatAsync(AgentHeartbeatRequest request, CancellationToken cancellationToken);

    /// <summary>The calling device; throws when the caller is not an active agent of this tenant.</summary>
    Task<Device> RequireCallerAsync(CancellationToken cancellationToken);
}

internal sealed class AgentHeartbeatService(
    ISolvixDbContext db,
    ICurrentUser caller,
    IClock clock,
    IAgentWorkProvider workProvider) : IAgentHeartbeatService
{
    public async Task<AgentWorkResponse> HeartbeatAsync(AgentHeartbeatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var device = await RequireCallerAsync(cancellationToken);
        var previousBoot = device.LastBootUtc;
        var now = clock.UtcNow;
        device.ReportInventory(request.Hostname, request.DnsDomain, request.OsName, request.OsVersion, request.OsBuild, request.AgentVersion);
        device.Touch(now, request.LastBootUtc);
        await db.SaveChangesAsync(cancellationToken);
        return await workProvider.GetWorkAsync(new DeviceHeartbeat(device.Id, previousBoot, request.LastBootUtc, now), cancellationToken);
    }

    public async Task<Device> RequireCallerAsync(CancellationToken cancellationToken)
    {
        var deviceId = caller.DeviceId ?? throw new ForbiddenException("Only endpoint agents can call this endpoint.");
        var device = await db.Set<Device>().FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken)
            ?? throw new ForbiddenException("The device is not enrolled in this tenant.");
        return device.IsActive ? device : throw new ForbiddenException("The device has been retired.");
    }
}
