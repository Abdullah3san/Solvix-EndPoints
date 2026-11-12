namespace Solvix.Modules.Assets.Domain;

using Solvix.SharedKernel.Entities;

public enum AuditActorKind
{
    User = 1,

    /// <summary>The platform itself (automatic matching triggered by an agent).</summary>
    System = 2,
}

/// <summary>
/// Append-only record of every change in asset management. The table rejects
/// UPDATE and TRUNCATE outright and DELETE outside platform scope (trigger), so
/// history cannot be rewritten by the application role.
/// </summary>
public sealed class AssetAuditLogEntry : TenantEntity
{
    public const int ActionMaxLength = 64;
    public const int SummaryMaxLength = 500;

    private AssetAuditLogEntry()
    {
    }

    public AssetAuditLogEntry(
        Guid tenantId, string entityType, Guid entityId, Guid? assetId, string action, string summary,
        string? changesJson, AuditActorKind actorKind, Guid? actorUserId, string? correlationId, DateTimeOffset occurredAtUtc)
        : base(tenantId)
    {
        EntityType = entityType;
        EntityId = entityId;
        AssetId = assetId;
        Action = action;
        Summary = summary.Length <= SummaryMaxLength ? summary : summary[..SummaryMaxLength];
        ChangesJson = changesJson;
        ActorKind = actorKind;
        ActorUserId = actorUserId;
        CorrelationId = correlationId;
        OccurredAtUtc = occurredAtUtc;
    }

    public string EntityType { get; private set; } = string.Empty;

    public Guid EntityId { get; private set; }

    public Guid? AssetId { get; private set; }

    /// <summary>Dotted, stable action code, e.g. <c>asset.transferred</c>.</summary>
    public string Action { get; private set; } = string.Empty;

    public string Summary { get; private set; } = string.Empty;

    /// <summary>{ field: { from, to } } as JSON (jsonb).</summary>
    public string? ChangesJson { get; private set; }

    public AuditActorKind ActorKind { get; private set; }

    public Guid? ActorUserId { get; private set; }

    public string? CorrelationId { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }
}
