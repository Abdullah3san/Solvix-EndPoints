namespace Solvix.Modules.Support.Application;

using Solvix.Agent.Contracts;
using Solvix.Modules.Support.Domain;

public sealed record TicketSummaryDto(
    Guid Id,
    long Number,
    string Subject,
    TicketStatus Status,
    TicketPriority Priority,
    TicketSource Source,
    Guid? DeviceId,
    string? DeviceName,
    string? RequesterName,
    Guid? AssignedUserId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record TicketCommentDto(
    Guid Id,
    CommentAuthor Author,
    string? AuthorName,
    string Body,
    bool IsInternal,
    DateTimeOffset CreatedAtUtc);

public sealed record TicketDetailsDto
{
    public required TicketSummaryDto Summary { get; init; }

    public string? Description { get; init; }

    public string? RequesterEmail { get; init; }

    public DateTimeOffset? ResolvedAtUtc { get; init; }

    public IReadOnlyList<TicketCommentDto> Comments { get; init; } = [];
}

public sealed record TicketListQuery(
    TicketStatus? Status,
    TicketPriority? Priority,
    Guid? DeviceId,
    string? Search,
    bool? Open,
    int Page = 1,
    int PageSize = 25);

public sealed record CreateTicketRequest(string Subject, string? Description, TicketPriority Priority, Guid? DeviceId);

/// <summary>Only supplied fields change. <see cref="Unassign"/> clears the assignee.</summary>
public sealed record UpdateTicketRequest(TicketStatus? Status, TicketPriority? Priority, Guid? AssignedUserId, bool Unassign = false);

public sealed record AddTicketCommentRequest(string Body, bool IsInternal = false);

public sealed record ChatMessageDto(long Sequence, ChatSender Sender, string? SenderName, string Body, DateTimeOffset SentAtUtc);

public sealed record SendChatRequest(string Body);

/// <summary>Conversations that have messages, most recent first, for the technician inbox.</summary>
public sealed record ChatConversationDto(Guid DeviceId, string DeviceName, string LastMessage, ChatSender LastSender, DateTimeOffset LastMessageAtUtc, long LastSequence);
