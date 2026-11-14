namespace Solvix.Modules.Security.Domain;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Guards;

/// <summary>
/// A persistent security issue on one device, identified by (rule, subject). It stays Open while the latest
/// assessment reproduces it, becomes Resolved when a later assessment no longer does, and reopens if it returns.
/// A finding is a <i>detection</i>; changing the device is a separate, approved <see cref="RemediationRequest"/>.
/// </summary>
public sealed class SecurityFinding : TenantEntity
{
    public const int RuleKeyMaxLength = 32;
    public const int SubjectMaxLength = 200;
    public const int TitleMaxLength = 300;
    public const int EvidenceMaxLength = 500;
    public const int MaxAcceptanceDays = 90;

    private SecurityFinding()
    {
    }

    public SecurityFinding(
        Guid tenantId, Guid endpointId, string ruleKey, string subjectKey, Severity severity, string title, string evidence,
        string? remediationActionKey, string? remediationParameter, DateTimeOffset now)
        : base(tenantId)
    {
        EndpointId = endpointId;
        RuleKey = ruleKey;
        SubjectKey = subjectKey;
        Status = FindingStatus.Open;
        Severity = severity;
        Title = Trim(title, TitleMaxLength);
        Evidence = Trim(evidence, EvidenceMaxLength);
        RemediationActionKey = remediationActionKey;
        RemediationParameter = remediationParameter;
        FirstDetectedAtUtc = now;
        LastDetectedAtUtc = now;
    }

    public Guid EndpointId { get; private set; }

    public string RuleKey { get; private set; } = string.Empty;

    /// <summary>Distinguishes several failures of one rule on one device (a drive letter, an account, a product). Empty for device-wide rules.</summary>
    public string SubjectKey { get; private set; } = string.Empty;

    public FindingStatus Status { get; private set; }

    public Severity Severity { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string Evidence { get; private set; } = string.Empty;

    public string? RemediationActionKey { get; private set; }

    public string? RemediationParameter { get; private set; }

    public DateTimeOffset FirstDetectedAtUtc { get; private set; }

    public DateTimeOffset LastDetectedAtUtc { get; private set; }

    public DateTimeOffset? ResolvedAtUtc { get; private set; }

    /// <summary>How many times the finding came back after being resolved.</summary>
    public int RecurrenceCount { get; private set; }

    public Guid? AcceptedByUserId { get; private set; }

    public DateTimeOffset? AcceptedUntilUtc { get; private set; }

    public string? AcceptanceReason { get; private set; }

    /// <summary>True when the finding is open or accepted (i.e. the issue still exists on the device).</summary>
    public bool IsActive => Status != FindingStatus.Resolved;

    /// <summary>Records that the latest assessment reproduced the finding. Returns true if it just became (re)opened.</summary>
    public bool Observe(Severity severity, string title, string evidence, string? actionKey, string? actionParameter, DateTimeOffset now)
    {
        Severity = severity;
        Title = Trim(title, TitleMaxLength);
        Evidence = Trim(evidence, EvidenceMaxLength);
        RemediationActionKey = actionKey;
        RemediationParameter = actionParameter;
        LastDetectedAtUtc = now;

        if (Status == FindingStatus.Resolved)
        {
            Status = FindingStatus.Open;
            ResolvedAtUtc = null;
            RecurrenceCount++;
            return true;
        }

        if (Status == FindingStatus.Accepted && AcceptedUntilUtc is { } until && until <= now)
        {
            Status = FindingStatus.Open;
            AcceptedByUserId = null;
            AcceptedUntilUtc = null;
            AcceptanceReason = null;
            return true;
        }

        return false;
    }

    public void Resolve(DateTimeOffset now)
    {
        if (Status == FindingStatus.Resolved)
        {
            return;
        }

        Status = FindingStatus.Resolved;
        ResolvedAtUtc = now;
        AcceptedByUserId = null;
        AcceptedUntilUtc = null;
        AcceptanceReason = null;
    }

    public void AcceptRisk(Guid userId, string reason, DateTimeOffset until, DateTimeOffset now)
    {
        if (Status == FindingStatus.Resolved)
        {
            throw new ConflictException("A resolved finding has no risk to accept.");
        }

        if (until <= now)
        {
            throw new ValidationException("acceptedUntilUtc", "The acceptance must end in the future.");
        }

        if (until > now.AddDays(MaxAcceptanceDays))
        {
            throw new ValidationException("acceptedUntilUtc", $"A risk can be accepted for at most {MaxAcceptanceDays} days; review it again after that.");
        }

        Status = FindingStatus.Accepted;
        AcceptedByUserId = userId;
        AcceptedUntilUtc = until;
        AcceptanceReason = Guard.NotNullOrWhiteSpace(reason, "reason", 500);
    }

    public void RevokeAcceptance()
    {
        if (Status != FindingStatus.Accepted)
        {
            throw new ConflictException("The finding is not in an accepted state.");
        }

        Status = FindingStatus.Open;
        AcceptedByUserId = null;
        AcceptedUntilUtc = null;
        AcceptanceReason = null;
    }

    private static string Trim(string value, int max) => value.Length <= max ? value : string.Concat(value.AsSpan(0, max - 1), "…");
}
