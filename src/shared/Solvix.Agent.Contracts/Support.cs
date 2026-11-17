namespace Solvix.Agent.Contracts;

public enum TicketPriority
{
    Low = 1,
    Normal = 2,
    High = 3,
    Urgent = 4,
}

public enum TicketStatus
{
    Open = 1,
    InProgress = 2,
    Resolved = 3,
    Closed = 4,
}

/// <summary>A support request typed by the person using the computer.</summary>
public sealed record SubmitTicketRequest
{
    public const int SubjectMaxLength = 200;
    public const int DescriptionMaxLength = 8000;

    public required string Subject { get; init; }

    public string? Description { get; init; }

    public TicketPriority Priority { get; init; } = TicketPriority.Normal;

    /// <summary>Who is asking (the signed-in Windows user); informational only.</summary>
    public string? RequesterName { get; init; }

    public string? RequesterEmail { get; init; }
}

public sealed record AgentTicket
{
    public required Guid Id { get; init; }

    public required long Number { get; init; }

    public required string Subject { get; init; }

    public required TicketStatus Status { get; init; }

    public required TicketPriority Priority { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record AgentTicketList
{
    public IReadOnlyList<AgentTicket> Tickets { get; init; } = [];
}

public enum ChatSender
{
    /// <summary>The person using the computer (sent through the agent).</summary>
    User = 1,

    /// <summary>A technician using the web console.</summary>
    Technician = 2,
}

public sealed record ChatMessage
{
    public const int BodyMaxLength = 4000;

    /// <summary>Monotonic per-server position; the agent asks for messages after the last one it has seen.</summary>
    public required long Sequence { get; init; }

    public required ChatSender Sender { get; init; }

    public string? SenderName { get; init; }

    public required string Body { get; init; }

    public required DateTimeOffset SentAtUtc { get; init; }
}

public sealed record SendChatMessageRequest
{
    public required string Body { get; init; }

    /// <summary>Who is typing (the signed-in Windows user); shown to the technician.</summary>
    public string? SenderName { get; init; }
}

public sealed record ChatMessageList
{
    public IReadOnlyList<ChatMessage> Messages { get; init; } = [];
}
