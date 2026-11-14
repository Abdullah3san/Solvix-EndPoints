namespace Solvix.Modules.Assistant.Domain;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Guards;

public sealed class AssistantTicket : TenantEntity
{
    public const int TitleMaxLength = 200;
    public const int DescriptionMaxLength = 8000;
    public const int CategoryMaxLength = 50;

    private AssistantTicket()
    {
    }

    public AssistantTicket(
        Guid tenantId,
        int number,
        string title,
        string description,
        TicketPriority priority,
        string? category,
        string deviceRefsJson,
        Guid createdByUserId,
        string? createdByEmail,
        bool createdViaAssistant,
        Guid? conversationId)
        : base(tenantId)
    {
        Number = number;
        Title = Guard.NotNullOrWhiteSpace(title, "title", TitleMaxLength);
        Description = Guard.NotNullOrWhiteSpace(description, "description", DescriptionMaxLength);
        Priority = priority;
        Category = Guard.Optional(category, "category", CategoryMaxLength);
        DeviceRefsJson = deviceRefsJson;
        CreatedByUserId = createdByUserId;
        CreatedByEmail = createdByEmail;
        CreatedViaAssistant = createdViaAssistant;
        ConversationId = conversationId;
        Status = TicketStatus.Open;
    }

    public int Number { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    public TicketPriority Priority { get; private set; }

    public string? Category { get; private set; }

    public TicketStatus Status { get; private set; }

    /// <summary>JSON array of <c>{id,name}</c> of related devices.</summary>
    public string DeviceRefsJson { get; private set; } = "[]";

    public Guid CreatedByUserId { get; private set; }

    public string? CreatedByEmail { get; private set; }

    public bool CreatedViaAssistant { get; private set; }

    public Guid? ConversationId { get; private set; }

    public DateTimeOffset? ResolvedAtUtc { get; private set; }

    public string Reference => $"TKT-{Number:D4}";

    public void SetStatus(TicketStatus status, DateTimeOffset now)
    {
        Status = status;
        ResolvedAtUtc = status is TicketStatus.Resolved or TicketStatus.Closed ? now : null;
    }
}

/// <summary>Per-tenant ticket number sequence (incremented with an atomic upsert).</summary>
public sealed class TicketCounter : TenantEntity
{
    private TicketCounter()
    {
    }

    public int LastNumber { get; private set; }
}
