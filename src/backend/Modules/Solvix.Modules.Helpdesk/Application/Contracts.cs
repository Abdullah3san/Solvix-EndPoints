namespace Solvix.Modules.Helpdesk.Application;

using Solvix.Modules.Helpdesk.Domain;

public sealed record TicketSummaryDto(
    Guid Id,
    long Number,
    string Subject,
    TicketStatus Status,
    TicketPriority Priority,
    TicketSource Source,
    Guid? CategoryId,
    string? CategoryName,
    Guid? SuggestedCategoryId,
    bool NeedsTriage,
    string? RequesterName,
    string? RequesterEmail,
    Guid? AssigneeTechnicianId,
    string? AssigneeName,
    Guid? DeviceId,
    DateTimeOffset? FirstResponseDueAtUtc,
    DateTimeOffset? ResolutionDueAtUtc,
    bool FirstResponseBreached,
    bool ResolutionBreached,
    string SlaState,
    int EscalationLevel,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastActivityAtUtc);

public sealed record CommentDto(Guid Id, string? AuthorName, string? AuthorEmail, bool IsInternal, bool IsFromStaff, string Body, TicketSource Source, DateTimeOffset CreatedAtUtc);

public sealed record AttachmentDto(Guid Id, string FileName, string ContentType, long SizeBytes, string Sha256, DateTimeOffset CreatedAtUtc);

public sealed record ResolutionDto(Guid Id, string Notes, Guid? ResolvedByTechnicianId, DateTimeOffset ResolvedAtUtc, DateTimeOffset? ReopenedAtUtc);

public sealed record TicketEventDto(Guid Id, TicketEventType Type, string? ActorLabel, Guid? ActorUserId, string? FromValue, string? ToValue, string? Detail, DateTimeOffset OccurredAtUtc);

public sealed record TicketDetailsDto(
    TicketSummaryDto Summary,
    string Description,
    string? DeviceName,
    string? AiSummary,
    DateTimeOffset? AiSummaryGeneratedAtUtc,
    string? AiSummarySource,
    int ReopenCount,
    IReadOnlyList<CommentDto> Comments,
    IReadOnlyList<AttachmentDto> Attachments,
    IReadOnlyList<ResolutionDto> Resolutions);

public sealed record TicketListQuery(
    string? Search = null,
    TicketStatus? Status = null,
    TicketPriority? Priority = null,
    Guid? CategoryId = null,
    Guid? AssigneeTechnicianId = null,
    bool? Mine = null,
    bool? Triage = null,
    bool? Escalated = null,
    bool? Overdue = null,
    bool? Unassigned = null,
    bool? Open = null,
    int Page = 1,
    int PageSize = 25);

public sealed record CreateTicketRequest(string? Subject, string? Description, TicketPriority? Priority, Guid? CategoryId, Guid? DeviceId, string? RequesterEmail, string? RequesterName);

public sealed record UpdateTicketRequest(string? Subject, string? Description);

public sealed record ChangeStatusRequest(TicketStatus Status, string? Resolution);

public sealed record AssignTicketRequest(Guid? TechnicianId);

public sealed record CategorizeTicketRequest(Guid? CategoryId);

public sealed record ChangePriorityRequest(TicketPriority Priority);

public sealed record LinkDeviceRequest(Guid? DeviceId);

public sealed record AddCommentRequest(string? Body, bool IsInternal);

public sealed record TicketStatsDto(int Open, int Unassigned, int NeedsTriage, int Escalated, int Overdue, int ResolvedLast7Days, int DueSoon);

// Catalog --------------------------------------------------------------------------------------------------------

public sealed record CategoryDto(Guid Id, string Name, string? Description, bool IsActive, Guid? DefaultTechnicianId, IReadOnlyList<string> Keywords);

public sealed record SaveCategoryRequest(string? Name, string? Description, Guid? DefaultTechnicianId, IReadOnlyList<string>? Keywords, bool? IsActive);

public sealed record TechnicianDto(Guid Id, Guid UserId, string? DisplayName, string? Email, bool IsActive, bool IsAvailable, int? MaxOpenTickets, IReadOnlyList<Guid> CategoryIds, string? Notes, int OpenTickets);

public sealed record CreateTechnicianRequest(Guid UserId, int? MaxOpenTickets, IReadOnlyList<Guid>? CategoryIds, string? Notes);

public sealed record UpdateTechnicianRequest(bool IsActive, bool IsAvailable, int? MaxOpenTickets, IReadOnlyList<Guid>? CategoryIds, string? Notes);

public sealed record SlaTargetDto(TicketPriority Priority, int FirstResponseMinutes, int ResolutionMinutes);

public sealed record SlaPolicyDto(Guid Id, string Name, bool IsDefault, bool IsActive, bool BusinessHoursOnly, string TimeZoneId, int WorkdayStartMinute, int WorkdayEndMinute, int WorkDaysMask, IReadOnlyList<SlaTargetDto> Targets);

public sealed record SaveSlaPolicyRequest(string? Name, bool IsDefault, bool? IsActive, bool BusinessHoursOnly, string? TimeZoneId, int? WorkdayStartMinute, int? WorkdayEndMinute, int? WorkDaysMask, IReadOnlyList<SlaTargetDto>? Targets);

public sealed record EscalationRuleDto(Guid Id, string Name, bool IsActive, EscalationTrigger Trigger, int ThresholdPercent, EscalationAction Action, Guid? TargetTechnicianId, TicketPriority? MinPriority);

public sealed record SaveEscalationRuleRequest(string? Name, bool? IsActive, EscalationTrigger Trigger, int ThresholdPercent, EscalationAction Action, Guid? TargetTechnicianId, TicketPriority? MinPriority);

public sealed record InboundChannelDto(Guid Id, string Name, string? Address, bool IsEnabled, TicketPriority DefaultPriority, IReadOnlyList<string> AllowedSenderDomains, DateTimeOffset? LastReceivedAtUtc);

public sealed record CreatedInboundChannelDto(InboundChannelDto Channel, string Token);

public sealed record SaveInboundChannelRequest(string? Name, string? Address, bool? IsEnabled, TicketPriority? DefaultPriority, IReadOnlyList<string>? AllowedSenderDomains);

// Inbound e-mail -------------------------------------------------------------------------------------------------

public sealed record EmailAddressDto(string? Email, string? Name);

public sealed record InboundAttachmentDto(string? FileName, string? ContentType, string? ContentBase64);

public sealed record InboundEmailRequest(
    string? MessageId,
    string? InReplyTo,
    IReadOnlyList<string>? References,
    EmailAddressDto? From,
    string? Subject,
    string? Text,
    string? Html,
    IReadOnlyDictionary<string, string>? Headers,
    IReadOnlyList<InboundAttachmentDto>? Attachments);

/// <param name="Outcome"><c>created</c>, <c>replied</c>, <c>duplicate</c> or <c>ignored</c>.</param>
public sealed record InboundEmailResult(string Outcome, long? TicketNumber);
