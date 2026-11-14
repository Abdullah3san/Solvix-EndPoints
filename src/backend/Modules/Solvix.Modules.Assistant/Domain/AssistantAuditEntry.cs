namespace Solvix.Modules.Assistant.Domain;

using Solvix.SharedKernel.Entities;

/// <summary>
/// Append-only record of AI requests, tool calls, proposed actions, approvals and execution
/// outcomes. A database trigger rejects UPDATE and DELETE. Content is redacted before it gets here.
/// </summary>
public sealed class AssistantAuditEntry : TenantEntity
{
    public const int KindMaxLength = 60;
    public const int NameMaxLength = 120;
    public const int InputMaxLength = 8000;
    public const int SummaryMaxLength = 2000;

    private AssistantAuditEntry()
    {
    }

    public AssistantAuditEntry(
        Guid tenantId,
        string kind,
        string name,
        AuditStatus status,
        Guid? userId,
        string? userEmail,
        Guid? conversationId,
        Guid? proposalId,
        string? inputJson,
        string? summary,
        long durationMs,
        string? correlationId,
        string? model)
        : base(tenantId)
    {
        Kind = kind;
        Name = name;
        Status = status;
        UserId = userId;
        UserEmail = userEmail;
        ConversationId = conversationId;
        ProposalId = proposalId;
        InputJson = Truncate(inputJson, InputMaxLength);
        Summary = Truncate(summary, SummaryMaxLength);
        DurationMs = durationMs;
        CorrelationId = correlationId;
        Model = model;
    }

    public string Kind { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public AuditStatus Status { get; private set; }

    public Guid? UserId { get; private set; }

    public string? UserEmail { get; private set; }

    public Guid? ConversationId { get; private set; }

    public Guid? ProposalId { get; private set; }

    public string? InputJson { get; private set; }

    public string? Summary { get; private set; }

    public long DurationMs { get; private set; }

    public string? CorrelationId { get; private set; }

    public string? Model { get; private set; }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max] + "…[truncated]";
}
