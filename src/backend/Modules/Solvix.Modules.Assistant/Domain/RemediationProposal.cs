namespace Solvix.Modules.Assistant.Domain;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Exceptions;

/// <summary>
/// An action the assistant proposes on behalf of a signed-in user. It has no effect until a
/// sufficiently privileged human approves it; approval re-validates everything server-side.
/// </summary>
public sealed class RemediationProposal : TenantEntity
{
    public const int TitleMaxLength = 200;
    public const int ActionMaxLength = 60;

    private RemediationProposal()
    {
    }

    public RemediationProposal(
        Guid tenantId,
        Guid? conversationId,
        Guid proposedByUserId,
        string? proposedByEmail,
        string action,
        string title,
        string rationale,
        RiskLevel risk,
        string parametersJson,
        string targetsJson,
        int targetCount,
        string fingerprint,
        DateTimeOffset expiresAtUtc)
        : base(tenantId)
    {
        ConversationId = conversationId;
        ProposedByUserId = proposedByUserId;
        ProposedByEmail = proposedByEmail;
        Action = action;
        Title = title;
        Rationale = rationale;
        Risk = risk;
        ParametersJson = parametersJson;
        TargetsJson = targetsJson;
        TargetCount = targetCount;
        Fingerprint = fingerprint;
        ExpiresAtUtc = expiresAtUtc;
        Status = ProposalStatus.PendingApproval;
    }

    public Guid? ConversationId { get; private set; }

    public Guid ProposedByUserId { get; private set; }

    public string? ProposedByEmail { get; private set; }

    public string Action { get; private set; } = string.Empty;

    public string Title { get; private set; } = string.Empty;

    public string Rationale { get; private set; } = string.Empty;

    public RiskLevel Risk { get; private set; }

    /// <summary>JSON object of validated, typed parameters.</summary>
    public string ParametersJson { get; private set; } = "{}";

    /// <summary>JSON array of <c>{id,name}</c>: devices pinned at proposal time.</summary>
    public string TargetsJson { get; private set; } = "[]";

    public int TargetCount { get; private set; }

    /// <summary>SHA-256 over action + parameters + target ids. Approval executes exactly what was shown.</summary>
    public string Fingerprint { get; private set; } = string.Empty;

    public ProposalStatus Status { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public Guid? DecidedByUserId { get; private set; }

    public string? DecidedByEmail { get; private set; }

    public DateTimeOffset? DecidedAtUtc { get; private set; }

    public string? DecisionNote { get; private set; }

    public DateTimeOffset? ExecutedAtUtc { get; private set; }

    /// <summary>JSON array of per-device outcomes.</summary>
    public string? ResultJson { get; private set; }

    public bool IsTerminal => Status is not (ProposalStatus.PendingApproval or ProposalStatus.Executing);

    public bool IsExpiredAt(DateTimeOffset now) => Status == ProposalStatus.PendingApproval && now >= ExpiresAtUtc;

    public void Expire()
    {
        EnsurePending();
        Status = ProposalStatus.Expired;
    }

    public void Reject(Guid userId, string? email, string? note, DateTimeOffset now)
    {
        EnsurePending();
        Status = ProposalStatus.Rejected;
        Decide(userId, email, note, now);
    }

    /// <summary>Approval: the proposal moves to Executing. Concurrent approvals lose via the xmin concurrency token.</summary>
    public void BeginExecution(Guid userId, string? email, string? note, DateTimeOffset now)
    {
        EnsurePending();
        if (IsExpiredAt(now))
        {
            throw new ConflictException("The proposal has expired.");
        }

        Status = ProposalStatus.Executing;
        Decide(userId, email, note, now);
    }

    public void Complete(ProposalStatus outcome, string resultJson, DateTimeOffset now)
    {
        if (Status != ProposalStatus.Executing)
        {
            throw new ConflictException("The proposal is not executing.");
        }

        if (outcome is not (ProposalStatus.Succeeded or ProposalStatus.PartiallyFailed or ProposalStatus.Failed))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        Status = outcome;
        ResultJson = resultJson;
        ExecutedAtUtc = now;
    }

    private void Decide(Guid userId, string? email, string? note, DateTimeOffset now)
    {
        DecidedByUserId = userId;
        DecidedByEmail = email;
        DecidedAtUtc = now;
        DecisionNote = note is { Length: > 500 } ? note[..500] : note;
    }

    private void EnsurePending()
    {
        if (Status != ProposalStatus.PendingApproval)
        {
            throw new ConflictException($"The proposal is already {Status}.");
        }
    }
}
