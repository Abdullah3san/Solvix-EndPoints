namespace Solvix.Modules.Devices.Monitoring;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Solvix.SharedKernel.Authorization;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Web;

/// <summary>
/// Live monitoring updates for console users. Server → client only: clients
/// cannot invoke anything. Each connection joins exactly the group of the
/// tenant in its (validated) access token; the tenant middleware has already
/// verified the membership.
/// </summary>
[Authorize(Policy = Policies.TenantMember)]
[RequirePermission(Permissions.Devices.View)]
public sealed class MonitoringHub : Hub
{
    public const string Route = "/hubs/monitoring";
    public const string UpdateMethod = "monitoringUpdate";

    public static string TenantGroup(Guid tenantId) => "tenant:" + tenantId.ToString("N");

    public override async Task OnConnectedAsync()
    {
        if (!Guid.TryParse(Context.User?.FindFirst(SolvixClaimTypes.TenantId)?.Value, out var tenantId))
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, TenantGroup(tenantId));
        await base.OnConnectedAsync();
    }
}

public interface IMonitoringNotifier
{
    Task PublishAsync(Guid tenantId, MonitoringUpdateDto update, CancellationToken cancellationToken);
}

internal sealed partial class SignalRMonitoringNotifier(IHubContext<MonitoringHub> hub, ILogger<SignalRMonitoringNotifier> logger) : IMonitoringNotifier
{
    public async Task PublishAsync(Guid tenantId, MonitoringUpdateDto update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.StatusChanges.Count == 0 && update.Events.Count == 0 && update.Alerts.Count == 0)
        {
            return;
        }

        try
        {
            await hub.Clients.Group(MonitoringHub.TenantGroup(tenantId)).SendAsync(MonitoringHub.UpdateMethod, update, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Live updates are best effort: state is persisted and dashboards refresh on reconnect.
            LogPublishFailed(logger, ex, tenantId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not publish a live monitoring update for tenant {TenantId}")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception, Guid tenantId);
}
