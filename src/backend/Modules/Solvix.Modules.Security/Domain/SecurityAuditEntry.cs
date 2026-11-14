namespace Solvix.Modules.Security.Domain;

using Solvix.SharedKernel.Entities;

/// <summary>
/// One immutable line of the security audit trail. The database enforces append-only behaviour (no UPDATE/DELETE
/// policy, a trigger that rejects both, and no UPDATE/DELETE/TRUNCATE privilege for the runtime role).
/// </summary>
public sealed class SecurityAuditEntry : TenantEntity
{
    public const int ActionMaxLength = 80;
    public const int SummaryMaxLength = 500;
    public const int DetailsMaxLength = 4000;

    private SecurityAuditEntry()
    {
    }

    public SecurityAuditEntry(
        Guid tenantId, DateTimeOffset occurredAtUtc, AuditActorType actorType, Guid? actorId, string actorLabel, string action,
        string entityType, Guid? entityId, string summary, string? details, string? sourceIp, string? correlationId)
        : base(tenantId)
    {
        OccurredAtUtc = occurredAtUtc;
        ActorType = actorType;
        ActorId = actorId;
        ActorLabel = Cut(actorLabel, 320);
        Action = Cut(action, ActionMaxLength);
        EntityType = Cut(entityType, 60);
        EntityId = entityId;
        Summary = Cut(summary, SummaryMaxLength);
        Details = details is null ? null : Cut(details, DetailsMaxLength);
        SourceIp = sourceIp is null ? null : Cut(sourceIp, 45);
        CorrelationId = correlationId is null ? null : Cut(correlationId, 128);
    }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public AuditActorType ActorType { get; private set; }

    public Guid? ActorId { get; private set; }

    /// <summary>Snapshot of who acted (e-mail, device name or "system") so the trail stays readable after accounts change.</summary>
    public string ActorLabel { get; private set; } = string.Empty;

    /// <summary>Dotted verb such as <c>policy.approved</c>.</summary>
    public string Action { get; private set; } = string.Empty;

    public string EntityType { get; private set; } = string.Empty;

    public Guid? EntityId { get; private set; }

    public string Summary { get; private set; } = string.Empty;

    /// <summary>Optional JSON with before/after values. Never contains secrets.</summary>
    public string? Details { get; private set; }

    public string? SourceIp { get; private set; }

    public string? CorrelationId { get; private set; }

    private static string Cut(string value, int max) => value.Length <= max ? value : string.Concat(value.AsSpan(0, max - 1), "…");
}
