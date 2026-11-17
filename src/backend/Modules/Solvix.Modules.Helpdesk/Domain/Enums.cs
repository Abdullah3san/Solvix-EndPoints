namespace Solvix.Modules.Helpdesk.Domain;

public enum TicketStatus
{
    New = 1,
    Open = 2,
    InProgress = 3,
    WaitingOnRequester = 4,
    Resolved = 5,
    Closed = 6,
}

public enum TicketPriority
{
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4,
}

public enum TicketSource
{
    Web = 1,
    Email = 2,
    Api = 3,
}

public enum TicketEventType
{
    Created = 1,
    StatusChanged = 2,
    Assigned = 3,
    PriorityChanged = 4,
    CategoryChanged = 5,
    Commented = 6,
    InternalNote = 7,
    AttachmentAdded = 8,
    Escalated = 9,
    SlaBreached = 10,
    Reopened = 11,
    Resolved = 12,
    Closed = 13,
    SummaryGenerated = 14,
    DeviceLinked = 15,
}

public enum EscalationTrigger
{
    /// <summary>Percent of the first-response window elapsed with no response.</summary>
    FirstResponseElapsedPercent = 1,

    /// <summary>Percent of the resolution window elapsed while unresolved.</summary>
    ResolutionElapsedPercent = 2,
}

public enum EscalationAction
{
    /// <summary>Mark the ticket as escalated (shows in the escalated queue) and log it.</summary>
    FlagOnly = 1,

    /// <summary>Reassign to a named technician (typically a lead).</summary>
    Reassign = 2,

    /// <summary>Raise the priority by one level.</summary>
    RaisePriority = 3,
}
