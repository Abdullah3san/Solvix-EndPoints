namespace Solvix.Modules.Devices.Monitoring;

using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Identity;

/// <summary>
/// Records agent contact for every authenticated agent request ("last seen"),
/// and whether it succeeded ("last successful communication"). Plain contact is
/// buffered, never written synchronously. Heartbeats that succeed are recorded
/// by the heartbeat processing itself.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RecordAgentContactAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var executed = await next();

        var user = context.HttpContext.User;
        if (!TryRead(user, SolvixClaimTypes.DeviceId, out var deviceId) || !TryRead(user, SolvixClaimTypes.TenantId, out var tenantId))
        {
            return;
        }

        var status = executed.Exception is not null && !executed.ExceptionHandled
            ? 500
            : (executed.Result as IStatusCodeActionResult)?.StatusCode ?? context.HttpContext.Response.StatusCode;
        var successful = status is >= 200 and < 400;
        var isHeartbeat = context.ActionDescriptor.EndpointMetadata.OfType<HeartbeatEndpointAttribute>().Any();
        if (isHeartbeat && successful)
        {
            return;
        }

        var services = context.HttpContext.RequestServices;
        var buffer = (PresenceBuffer)services.GetService(typeof(PresenceBuffer))!;
        var clock = (IClock)services.GetService(typeof(IClock))!;
        buffer.Record(tenantId, deviceId, clock.UtcNow, successful, heartbeat: false);
    }

    private static bool TryRead(ClaimsPrincipal user, string claim, out Guid value) =>
        Guid.TryParse(user.FindFirst(claim)?.Value, out value);
}

/// <summary>Marks the heartbeat action, whose successful contact is recorded by heartbeat processing.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class HeartbeatEndpointAttribute : Attribute
{
}
