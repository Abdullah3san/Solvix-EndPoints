namespace Solvix.Modules.Security.Domain;

/// <summary>
/// Severity of a finding or alert. The rubric is part of the product contract and is
/// served by the API (<c>GET /security/rules/severities</c>) so every number a user sees
/// can be explained.
/// </summary>
public enum Severity
{
    /// <summary>Informational. Never changes a compliance score.</summary>
    Info = 0,

    /// <summary>Hygiene issue; exploitation needs several other failures first.</summary>
    Low = 1,

    /// <summary>A protection is degraded or a policy is not met, but a primary control still works.</summary>
    Medium = 2,

    /// <summary>A primary protection is disabled or a well-known weakness is exposed.</summary>
    High = 3,

    /// <summary>Evidence of active compromise, or exposure to remote exploitation right now.</summary>
    Critical = 4,
}

public enum RuleCategory
{
    Defender = 1,
    Firewall = 2,
    Encryption = 3,
    LocalAdmins = 4,
    RemovableMedia = 5,
    Software = 6,
    Configuration = 7,
    Events = 8,
    Agent = 9,
}

/// <summary>Result of evaluating one check against one device.</summary>
public enum CheckOutcome
{
    Pass = 1,
    Fail = 2,

    /// <summary>
    /// The data needed to decide was not collected (unsupported edition, permission error, agent bug).
    /// Unknown is never treated as a pass and never resolves an existing finding.
    /// </summary>
    Unknown = 3,

    /// <summary>The policy does not require this check, or the device legitimately cannot have the control.</summary>
    NotApplicable = 4,
}

public enum FindingStatus
{
    Open = 1,

    /// <summary>The latest assessment no longer reproduces the finding.</summary>
    Resolved = 2,

    /// <summary>An administrator accepted the risk until <c>AcceptedUntilUtc</c>. Still evaluated; not scored while accepted.</summary>
    Accepted = 3,
}

public enum AlertStatus
{
    New = 1,
    Acknowledged = 2,
    Resolved = 3,
    Dismissed = 4,
}

public enum EndpointStatus
{
    /// <summary>Enrolled; agent may report.</summary>
    Active = 1,

    /// <summary>Key revoked. The agent is rejected and no commands are delivered.</summary>
    Revoked = 2,
}

public enum PolicyStatus
{
    Draft = 1,
    PendingApproval = 2,
    Active = 3,
    Rejected = 4,
    Retired = 5,
}

/// <summary>
/// Whether Solvix may ever ask an agent to change a device. <see cref="DetectOnly"/> is the default
/// and a hard stop: no remediation request can be created or delivered.
/// </summary>
public enum EnforcementMode
{
    /// <summary>Observe and report only.</summary>
    DetectOnly = 1,

    /// <summary>Remediation can be requested, but every action needs approval by a second person.</summary>
    ApprovalRequired = 2,

    /// <summary>As ApprovalRequired, but Low-risk catalogue actions that the policy allows are approved automatically.</summary>
    AutoApproveLowRisk = 3,
}

public enum UsbStoragePolicy
{
    Allow = 0,
    BlockWrite = 1,
    BlockAll = 2,
}

public enum RemediationRisk
{
    Low = 1,
    Medium = 2,
    High = 3,
}

public enum RemediationStatus
{
    PendingApproval = 1,
    Approved = 2,
    Rejected = 3,

    /// <summary>Handed to the agent. The agent has not yet reported a result.</summary>
    Dispatched = 4,
    Succeeded = 5,
    Failed = 6,
    Cancelled = 7,
    Expired = 8,
}

public enum AuditActorType
{
    User = 1,
    System = 2,
    Device = 3,
}

public enum ComplianceBand
{
    /// <summary>No report, or the last report is older than the policy's staleness limit.</summary>
    Unknown = 0,
    Compliant = 1,
    AtRisk = 2,
    NonCompliant = 3,
}
