namespace Solvix.Modules.Security.Application.Common;

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Solvix.Modules.Security.Domain;
using Solvix.Modules.Security.Persistence;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

/// <summary>Who performed an audited action.</summary>
public sealed record AuditActor(AuditActorType Type, Guid? Id, string Label)
{
    public static AuditActor System(string label) => new(AuditActorType.System, null, label);
}

/// <summary>Set by the device gateway so audit entries written while serving an agent name the device.</summary>
public sealed class DeviceActorAccessor
{
    public AuditActor? Actor { get; private set; }

    public void Set(Guid endpointId, string hostname) => Actor = new AuditActor(AuditActorType.Device, endpointId, hostname);
}

public interface IAuditTrail
{
    /// <summary>
    /// Stages an audit entry in the current unit of work. It is committed by the same <c>SaveChanges</c> as the
    /// change it describes, so an action and its audit record succeed or fail together.
    /// </summary>
    void Record(string action, string entityType, Guid? entityId, string summary, object? details = null, AuditActor? actor = null);
}

internal sealed class AuditTrail(
    ISolvixDbContext db,
    ITenantContext tenant,
    ICurrentUser user,
    DeviceActorAccessor deviceActor,
    IClock clock,
    IHttpContextAccessor httpContext) : IAuditTrail
{
    public void Record(string action, string entityType, Guid? entityId, string summary, object? details = null, AuditActor? actor = null)
    {
        var resolved = actor ?? ResolveActor();
        var http = httpContext.HttpContext;
        var json = details is null ? null : JsonSerializer.Serialize(details, JsonColumn.Options);
        db.Set<SecurityAuditEntry>().Add(new SecurityAuditEntry(
            tenant.RequireTenantId(),
            clock.UtcNow,
            resolved.Type,
            resolved.Id,
            resolved.Label,
            action,
            entityType,
            entityId,
            summary,
            json,
            http?.Connection.RemoteIpAddress?.ToString(),
            http?.Items.TryGetValue("X-Correlation-Id", out var correlation) == true ? correlation?.ToString() : http?.TraceIdentifier));
    }

    private AuditActor ResolveActor()
    {
        if (user.IsAuthenticated && user.UserId is { } id)
        {
            return new AuditActor(AuditActorType.User, id, user.Email ?? id.ToString());
        }

        return deviceActor.Actor ?? AuditActor.System("system");
    }
}
