namespace Solvix.Modules.Helpdesk.Domain;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Guards;

public sealed class Ticket : TenantEntity
{
    public const int SubjectMaxLength = 300;
    public const int DescriptionMaxLength = 20_000;

    private Ticket()
    {
    }

    public long Number { get; private set; }

    public string Subject { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    public TicketStatus Status { get; private set; } = TicketStatus.New;

    public TicketPriority Priority { get; private set; } = TicketPriority.Medium;

    public TicketSource Source { get; private set; }

    public Guid? CategoryId { get; private set; }

    /// <summary>E-mail intake only: the category the keyword rules would pick. A technician confirms it.</summary>
    public Guid? SuggestedCategoryId { get; private set; }

    public Guid? RequesterUserId { get; private set; }

    public string? RequesterEmail { get; private set; }

    public string? RequesterName { get; private set; }

    public Guid? DeviceId { get; private set; }

    public Guid? AssigneeTechnicianId { get; private set; }

    public Guid? SlaPolicyId { get; private set; }

    public DateTimeOffset? FirstResponseDueAtUtc { get; private set; }

    public DateTimeOffset? ResolutionDueAtUtc { get; private set; }

    public DateTimeOffset? FirstRespondedAtUtc { get; private set; }

    public DateTimeOffset? ResolvedAtUtc { get; private set; }

    public DateTimeOffset? ClosedAtUtc { get; private set; }

    /// <summary>Set while the SLA clock is paused (waiting on the requester).</summary>
    public DateTimeOffset? SlaPausedAtUtc { get; private set; }

    public bool FirstResponseBreached { get; private set; }

    public bool ResolutionBreached { get; private set; }

    public int EscalationLevel { get; private set; }

    public int ReopenCount { get; private set; }

    public DateTimeOffset LastActivityAtUtc { get; private set; }

    /// <summary>Message-Id of the e-mail that created the ticket (de-duplication and reply threading).</summary>
    public string? ExternalMessageId { get; private set; }

    public string? Summary { get; private set; }

    public DateTimeOffset? SummaryGeneratedAtUtc { get; private set; }

    public string? SummarySource { get; private set; }

    public bool NeedsTriage => CategoryId is null && Status != TicketStatus.Closed;

    public bool IsOpen => Status is not (TicketStatus.Resolved or TicketStatus.Closed);

    public static Ticket Create(
        Guid tenantId,
        long number,
        string subject,
        string description,
        TicketPriority priority,
        TicketSource source,
        Guid? categoryId,
        Guid? requesterUserId,
        string? requesterEmail,
        string? requesterName,
        Guid? deviceId,
        string? externalMessageId,
        DateTimeOffset now)
    {
        var ticket = new Ticket
        {
            Number = number,
            Subject = Guard.NotNullOrWhiteSpace(subject, "subject", SubjectMaxLength),
            Description = Guard.NotNullOrWhiteSpace(description, "description", DescriptionMaxLength),
            Priority = priority,
            Source = source,
            CategoryId = categoryId,
            RequesterUserId = requesterUserId,
            RequesterEmail = Guard.Optional(requesterEmail, "requesterEmail", 254),
            RequesterName = Guard.Optional(requesterName, "requesterName", 200),
            DeviceId = deviceId,
            ExternalMessageId = externalMessageId,
            LastActivityAtUtc = now,
        };
        ticket.AssignTenant(tenantId);
        return ticket;
    }

    public void ApplySla(SlaPolicy? policy, DateTimeOffset now)
    {
        if (policy is null || policy.TargetFor(Priority) is not { } target)
        {
            SlaPolicyId = policy?.Id;
            FirstResponseDueAtUtc = null;
            ResolutionDueAtUtc = null;
            return;
        }

        var calendar = policy.Calendar();
        SlaPolicyId = policy.Id;
        FirstResponseDueAtUtc = FirstRespondedAtUtc is null ? calendar.Add(CreatedAtUtc == default ? now : CreatedAtUtc, target.FirstResponseMinutes) : FirstResponseDueAtUtc;
        ResolutionDueAtUtc = calendar.Add(CreatedAtUtc == default ? now : CreatedAtUtc, target.ResolutionMinutes);
    }

    public void SetSuggestedCategory(Guid? categoryId) => SuggestedCategoryId = categoryId;

    public void SetCategory(Guid? categoryId, DateTimeOffset now)
    {
        CategoryId = categoryId;
        SuggestedCategoryId = null;
        Touch(now);
    }

    public void SetPriority(TicketPriority priority, DateTimeOffset now)
    {
        Priority = priority;
        Touch(now);
    }

    public void LinkDevice(Guid? deviceId, DateTimeOffset now)
    {
        DeviceId = deviceId;
        Touch(now);
    }

    public void Assign(Guid? technicianId, DateTimeOffset now)
    {
        AssigneeTechnicianId = technicianId;
        if (technicianId is not null && Status == TicketStatus.New)
        {
            Status = TicketStatus.Open;
        }

        Touch(now);
    }

    public void UpdateContent(string subject, string description, DateTimeOffset now)
    {
        Subject = Guard.NotNullOrWhiteSpace(subject, "subject", SubjectMaxLength);
        Description = Guard.NotNullOrWhiteSpace(description, "description", DescriptionMaxLength);
        Touch(now);
    }

    /// <summary>A staff reply (public comment) counts as the first response.</summary>
    public void RecordStaffResponse(DateTimeOffset now)
    {
        FirstRespondedAtUtc ??= now;
        if (FirstResponseDueAtUtc is { } due && FirstRespondedAtUtc > due)
        {
            FirstResponseBreached = true;
        }

        Touch(now);
    }

    /// <summary>Moves the ticket through its lifecycle, pausing/resuming the SLA clock and tracking (re)opening.</summary>
    public void ChangeStatus(TicketStatus next, DateTimeOffset now, SlaCalendar? calendar)
    {
        if (next == Status)
        {
            return;
        }

        if (next == TicketStatus.New)
        {
            throw new ConflictException("A ticket cannot go back to New.", "invalid_transition");
        }

        var previous = Status;

        if (next == TicketStatus.WaitingOnRequester)
        {
            if (previous is TicketStatus.Resolved or TicketStatus.Closed)
            {
                throw new ConflictException("Reopen the ticket before waiting on the requester.", "invalid_transition");
            }

            SlaPausedAtUtc ??= now;
        }
        else if (SlaPausedAtUtc is { } pausedAt)
        {
            ResumeSla(pausedAt, now, calendar);
        }

        if (next is TicketStatus.Resolved)
        {
            ResolvedAtUtc = now;
            if (ResolutionDueAtUtc is { } due && now > due)
            {
                ResolutionBreached = true;
            }
        }
        else if (next == TicketStatus.Closed)
        {
            ClosedAtUtc = now;
            ResolvedAtUtc ??= now;
        }
        else if (previous is TicketStatus.Resolved or TicketStatus.Closed)
        {
            ReopenCount++;
            ResolvedAtUtc = null;
            ClosedAtUtc = null;
        }

        Status = next;
        Touch(now);
    }

    public bool MarkFirstResponseBreached()
    {
        if (FirstResponseBreached)
        {
            return false;
        }

        FirstResponseBreached = true;
        return true;
    }

    public bool MarkResolutionBreached()
    {
        if (ResolutionBreached)
        {
            return false;
        }

        ResolutionBreached = true;
        return true;
    }

    public void Escalate(DateTimeOffset now)
    {
        EscalationLevel++;
        Touch(now);
    }

    public void SetSummary(string summary, string source, DateTimeOffset now)
    {
        Summary = summary.Length <= 4000 ? summary : summary[..4000];
        SummaryGeneratedAtUtc = now;
        SummarySource = source;
    }

    private void ResumeSla(DateTimeOffset pausedAt, DateTimeOffset now, SlaCalendar? calendar)
    {
        // Time left at the moment of pausing is carried over, so waiting on the requester never burns the SLA.
        var cal = calendar ?? SlaCalendar.AlwaysOn;
        if (ResolutionDueAtUtc is { } resolutionDue)
        {
            ResolutionDueAtUtc = cal.Add(now, cal.BetweenExact(pausedAt, resolutionDue));
        }

        if (FirstRespondedAtUtc is null && FirstResponseDueAtUtc is { } responseDue)
        {
            FirstResponseDueAtUtc = cal.Add(now, cal.BetweenExact(pausedAt, responseDue));
        }

        SlaPausedAtUtc = null;
    }

    private void Touch(DateTimeOffset now) => LastActivityAtUtc = now;
}

public sealed class TicketComment : TenantEntity
{
    public const int BodyMaxLength = 20_000;

    private TicketComment()
    {
    }

    public Guid TicketId { get; private set; }

    public Guid? AuthorUserId { get; private set; }

    public string? AuthorName { get; private set; }

    public string? AuthorEmail { get; private set; }

    public bool IsInternal { get; private set; }

    public bool IsFromStaff { get; private set; }

    public string Body { get; private set; } = string.Empty;

    public TicketSource Source { get; private set; }

    public string? ExternalMessageId { get; private set; }

    public static TicketComment Create(Guid tenantId, Guid ticketId, Guid? authorUserId, string? authorName, string? authorEmail, bool isInternal, bool isFromStaff, string body, TicketSource source, string? externalMessageId)
    {
        var comment = new TicketComment
        {
            TicketId = ticketId,
            AuthorUserId = authorUserId,
            AuthorName = Guard.Optional(authorName, "authorName", 200),
            AuthorEmail = Guard.Optional(authorEmail, "authorEmail", 254),
            IsInternal = isInternal,
            IsFromStaff = isFromStaff,
            Body = Guard.NotNullOrWhiteSpace(body, "body", BodyMaxLength),
            Source = source,
            ExternalMessageId = externalMessageId,
        };
        comment.AssignTenant(tenantId);
        return comment;
    }
}

public sealed class TicketAttachment : TenantEntity
{
    private TicketAttachment()
    {
    }

    public Guid TicketId { get; private set; }

    public Guid? CommentId { get; private set; }

    public string FileName { get; private set; } = string.Empty;

    public string ContentType { get; private set; } = string.Empty;

    public long SizeBytes { get; private set; }

    public string Sha256 { get; private set; } = string.Empty;

    public Guid? UploadedByUserId { get; private set; }

    public static TicketAttachment Create(Guid tenantId, Guid ticketId, Guid? commentId, string fileName, string contentType, long sizeBytes, string sha256, Guid? uploadedBy)
    {
        var attachment = new TicketAttachment
        {
            TicketId = ticketId,
            CommentId = commentId,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            Sha256 = sha256,
            UploadedByUserId = uploadedBy,
        };
        attachment.AssignTenant(tenantId);
        return attachment;
    }
}

/// <summary>Attachment bytes, kept apart so listing tickets never loads them. Stored in the database: RLS-isolated and covered by the DB backup.</summary>
public sealed class TicketAttachmentContent : TenantEntity
{
    private TicketAttachmentContent()
    {
    }

    public Guid AttachmentId { get; private set; }

    public byte[] Data { get; private set; } = [];

    public static TicketAttachmentContent Create(Guid tenantId, Guid attachmentId, byte[] data)
    {
        var content = new TicketAttachmentContent { AttachmentId = attachmentId, Data = data };
        content.AssignTenant(tenantId);
        return content;
    }
}

/// <summary>Append-only audit trail of everything that happens to a ticket.</summary>
public sealed class TicketEvent : TenantEntity
{
    private TicketEvent()
    {
    }

    public Guid TicketId { get; private set; }

    public TicketEventType Type { get; private set; }

    public Guid? ActorUserId { get; private set; }

    /// <summary>Display label for non-user actors ("system", "e-mail intake", requester address).</summary>
    public string? ActorLabel { get; private set; }

    public string? FromValue { get; private set; }

    public string? ToValue { get; private set; }

    public string? Detail { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public static TicketEvent Create(Guid tenantId, Guid ticketId, TicketEventType type, Guid? actorUserId, string? actorLabel, string? from, string? to, string? detail, DateTimeOffset now)
    {
        var e = new TicketEvent
        {
            TicketId = ticketId,
            Type = type,
            ActorUserId = actorUserId,
            ActorLabel = Trim(actorLabel, 200),
            FromValue = Trim(from, 200),
            ToValue = Trim(to, 200),
            Detail = Trim(detail, 2000),
            OccurredAtUtc = now,
        };
        e.AssignTenant(tenantId);
        return e;
    }

    private static string? Trim(string? value, int max) => value is null ? null : (value.Length <= max ? value : value[..max]);
}

/// <summary>One closed resolution cycle: a ticket can be resolved, reopened and resolved again.</summary>
public sealed class TicketResolution : TenantEntity
{
    private TicketResolution()
    {
    }

    public Guid TicketId { get; private set; }

    public Guid? ResolvedByUserId { get; private set; }

    public Guid? ResolvedByTechnicianId { get; private set; }

    public string Notes { get; private set; } = string.Empty;

    public DateTimeOffset ResolvedAtUtc { get; private set; }

    public DateTimeOffset? ReopenedAtUtc { get; private set; }

    public static TicketResolution Create(Guid tenantId, Guid ticketId, Guid? userId, Guid? technicianId, string notes, DateTimeOffset now)
    {
        var r = new TicketResolution
        {
            TicketId = ticketId,
            ResolvedByUserId = userId,
            ResolvedByTechnicianId = technicianId,
            Notes = Guard.NotNullOrWhiteSpace(notes, "resolution", 5000),
            ResolvedAtUtc = now,
        };
        r.AssignTenant(tenantId);
        return r;
    }

    public void MarkReopened(DateTimeOffset now) => ReopenedAtUtc ??= now;
}

public sealed class AppliedEscalation : TenantEntity
{
    private AppliedEscalation()
    {
    }

    public Guid TicketId { get; private set; }

    public Guid RuleId { get; private set; }

    public DateTimeOffset AppliedAtUtc { get; private set; }

    public static AppliedEscalation Create(Guid tenantId, Guid ticketId, Guid ruleId, DateTimeOffset now)
    {
        var a = new AppliedEscalation { TicketId = ticketId, RuleId = ruleId, AppliedAtUtc = now };
        a.AssignTenant(tenantId);
        return a;
    }
}
