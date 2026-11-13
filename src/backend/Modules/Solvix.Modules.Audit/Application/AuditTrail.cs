namespace Solvix.Modules.Audit.Application;

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Audit.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Auditing;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Tenancy;

internal sealed class AuditTrail(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IHttpContextAccessor httpContextAccessor,
    IClock clock) : IAuditTrail
{
    private const string CorrelationItemKey = "X-Correlation-Id";

    private static readonly JsonSerializerOptions DetailsJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public void Record(string action, string entityType, Guid? entityId, string summary, object? details = null)
    {
        var tenantId = tenantContext.RequireTenantId();
        var http = httpContextAccessor.HttpContext;
        var (actorType, actorId, actorName) = currentUser switch
        {
            { IsAgent: true } => (AuditActorType.Agent, currentUser.DeviceId, http?.User.FindFirst(SolvixClaimTypes.Name)?.Value),
            { UserId: { } userId } => (AuditActorType.User, (Guid?)userId, currentUser.Email),
            _ => (AuditActorType.System, (Guid?)null, (string?)"system"),
        };
        var correlationId = http?.Items.TryGetValue(CorrelationItemKey, out var c) == true ? c?.ToString() : null;

        db.Set<AuditEntry>().Add(new AuditEntry(
            tenantId,
            clock.UtcNow,
            actorType,
            actorId,
            actorName,
            action,
            entityType,
            entityId,
            summary,
            details is null ? null : JsonSerializer.Serialize(details, DetailsJson),
            correlationId));
    }
}

public sealed record AuditEntryDto(
    Guid Id,
    DateTimeOffset OccurredAtUtc,
    AuditActorType ActorType,
    Guid? ActorId,
    string? ActorName,
    string Action,
    string EntityType,
    Guid? EntityId,
    string Summary,
    JsonElement? Details,
    string? CorrelationId);

public sealed record AuditQuery(
    string? Action,
    string? EntityType,
    Guid? EntityId,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    int Page = 1,
    int PageSize = 50);

public interface IAuditQueryService
{
    Task<PagedResult<AuditEntryDto>> ListAsync(AuditQuery query, CancellationToken cancellationToken);
}

internal sealed class AuditQueryService(ISolvixDbContext db) : IAuditQueryService
{
    public async Task<PagedResult<AuditEntryDto>> ListAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var entries = db.Set<AuditEntry>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            // Prefix match so "patch" returns "patch.approved", "patch.declined", ...
            var prefix = query.Action.Trim();
            entries = entries.Where(a => a.Action == prefix || a.Action.StartsWith(prefix + "."));
        }

        if (!string.IsNullOrWhiteSpace(query.EntityType))
        {
            entries = entries.Where(a => a.EntityType == query.EntityType);
        }

        if (query.EntityId is { } entityId)
        {
            entries = entries.Where(a => a.EntityId == entityId);
        }

        if (query.FromUtc is { } from)
        {
            entries = entries.Where(a => a.OccurredAtUtc >= from);
        }

        if (query.ToUtc is { } to)
        {
            entries = entries.Where(a => a.OccurredAtUtc < to);
        }

        var total = await entries.LongCountAsync(cancellationToken);
        var rows = await entries.OrderByDescending(a => a.OccurredAtUtc).ThenByDescending(a => a.Id)
            .Skip(page.Skip).Take(page.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<AuditEntryDto>(
            rows.Select(a => new AuditEntryDto(
                a.Id, a.OccurredAtUtc, a.ActorType, a.ActorId, a.ActorName, a.Action, a.EntityType, a.EntityId, a.Summary,
                a.DetailsJson is null ? null : JsonDocument.Parse(a.DetailsJson).RootElement.Clone(),
                a.CorrelationId)).ToList(),
            page.SafePage,
            page.SafePageSize,
            total);
    }
}
