namespace Solvix.Modules.Audit.Domain;

using Solvix.SharedKernel.Auditing;
using Solvix.SharedKernel.Entities;

/// <summary>
/// One immutable audit record. Rows are insert-only: the database rejects
/// UPDATE, DELETE and TRUNCATE on the table (trigger), and the entity exposes no
/// mutators.
/// </summary>
public sealed class AuditEntry : TenantEntity
{
    public const int ActionMaxLength = 100;
    public const int EntityTypeMaxLength = 100;
    public const int SummaryMaxLength = 1000;
    public const int ActorNameMaxLength = 320;
    public const int CorrelationIdMaxLength = 128;

    private AuditEntry()
    {
    }

    public AuditEntry(
        Guid tenantId,
        DateTimeOffset occurredAtUtc,
        AuditActorType actorType,
        Guid? actorId,
        string? actorName,
        string action,
        string entityType,
        Guid? entityId,
        string summary,
        string? detailsJson,
        string? correlationId)
        : base(tenantId)
    {
        OccurredAtUtc = occurredAtUtc;
        ActorType = actorType;
        ActorId = actorId;
        ActorName = Truncate(actorName, ActorNameMaxLength);
        Action = Truncate(action, ActionMaxLength)!;
        EntityType = Truncate(entityType, EntityTypeMaxLength)!;
        EntityId = entityId;
        Summary = Truncate(summary, SummaryMaxLength)!;
        DetailsJson = detailsJson;
        CorrelationId = Truncate(correlationId, CorrelationIdMaxLength);
    }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public AuditActorType ActorType { get; private set; }

    public Guid? ActorId { get; private set; }

    public string? ActorName { get; private set; }

    /// <summary>Dotted, past-tense verb such as <c>patch.approved</c>.</summary>
    public string Action { get; private set; } = string.Empty;

    public string EntityType { get; private set; } = string.Empty;

    public Guid? EntityId { get; private set; }

    public string Summary { get; private set; } = string.Empty;

    public string? DetailsJson { get; private set; }

    public string? CorrelationId { get; private set; }

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
