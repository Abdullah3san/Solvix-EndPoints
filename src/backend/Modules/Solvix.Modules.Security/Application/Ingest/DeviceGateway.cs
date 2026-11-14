namespace Solvix.Modules.Security.Application.Ingest;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Solvix.Modules.Security.Application.Common;
using Solvix.Modules.Security.Domain;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

/// <summary>An authenticated device and the services of its tenant-bound scope.</summary>
public sealed record DeviceCall(IIngestService Ingest, ManagedEndpoint Endpoint);

/// <summary>
/// The only door for agents. Authenticates the device key, then runs the call inside a scope bound to that device's
/// tenant (<see cref="ITenantScopeFactory"/>), so tenant filters, write enforcement and PostgreSQL row-level security apply
/// exactly as they do for a signed-in user. A device can therefore only ever reach its own tenant, and the only
/// operations it can reach are the ones exposed through <see cref="DeviceCall"/>.
/// </summary>
public interface IDeviceGateway
{
    Task<T> RunAsync<T>(string? deviceKey, Func<DeviceCall, Task<T>> action, CancellationToken cancellationToken);
}

internal sealed partial class DeviceGateway(ITenantScopeFactory scopes, ILogger<DeviceGateway> logger) : IDeviceGateway
{
    public async Task<T> RunAsync<T>(string? deviceKey, Func<DeviceCall, Task<T>> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (!DeviceKey.TryParse(deviceKey, out var tenantId, out var endpointId, out var secret))
        {
            LogRejected(logger, "malformed key");
            throw new UnauthorizedException("Invalid device credentials.");
        }

        await using var scope = scopes.CreateScope(tenantId);
        var services = scope.ServiceProvider;

        var tenants = services.GetRequiredService<ITenantDirectory>();
        var tenantInfo = await tenants.FindByIdAsync(tenantId, cancellationToken);
        if (tenantInfo is not { IsActive: true })
        {
            LogRejected(logger, "tenant unknown or inactive");
            throw new UnauthorizedException("Invalid device credentials.");
        }

        var db = services.GetRequiredService<ISolvixDbContext>();
        var endpoint = await db.Set<ManagedEndpoint>().FirstOrDefaultAsync(e => e.Id == endpointId, cancellationToken);

        // Verify even when the device does not exist so that response time does not reveal which ids are real.
        var secretMatches = DeviceKey.Verify(endpoint?.KeyHash, secret);
        if (endpoint is null || !secretMatches || endpoint.Status != EndpointStatus.Active)
        {
            LogRejected(logger, endpoint is null ? "unknown device" : !secretMatches ? "bad secret" : "revoked device");
            throw new UnauthorizedException("Invalid device credentials.");
        }

        services.GetRequiredService<DeviceActorAccessor>().Set(endpoint.Id, endpoint.Hostname);
        return await action(new DeviceCall(services.GetRequiredService<IIngestService>(), endpoint));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Device authentication rejected: {Reason}")]
    private static partial void LogRejected(ILogger logger, string reason);
}
