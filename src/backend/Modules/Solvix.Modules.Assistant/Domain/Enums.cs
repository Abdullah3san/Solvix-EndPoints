namespace Solvix.Modules.Assistant.Domain;

public enum MessageRole
{
    User = 1,
    Assistant = 2,
}

public enum RiskLevel
{
    Low = 1,
    Medium = 2,
    High = 3,
}

public enum ProposalStatus
{
    PendingApproval = 1,
    Executing = 2,
    Succeeded = 3,
    PartiallyFailed = 4,
    Failed = 5,
    Rejected = 6,
    Expired = 7,
}

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

public enum DocumentStatus
{
    Processing = 1,
    Ready = 2,
    Failed = 3,
}

public enum ReportKind
{
    FleetHealth = 1,
    SoftwareInventory = 2,
    Security = 3,
    Performance = 4,
}

public enum AuditStatus
{
    Success = 1,
    Denied = 2,
    Failed = 3,
}
