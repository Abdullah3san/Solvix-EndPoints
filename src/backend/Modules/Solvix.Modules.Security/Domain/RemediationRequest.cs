namespace Solvix.Modules.Security.Domain;

using Solvix.Modules.Security.Evaluation;
using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Guards;

/// <summary>
/// A request to change a device. This is the only way Solvix ever causes a change on an endpoint, and it
/// always passes through an approval step: a second person approves (or, for Low-risk catalogue actions and only when
/// the active policy says so, the policy auto-approves). Requests expire if not approved or not collected in time.
/// </summary>
public sealed class RemediationRequest : TenantEntity
{
    public static readonly TimeSpan ApprovalWindow = TimeSpan.FromDays(7);
    public static readonly TimeSpan DeliveryWindow = TimeSpan.FromHours(24);
    public static readonly TimeSpan ResultWindow = TimeSpan.FromMinutes(30);

    private RemediationRequest()
    {
    }

    private RemediationRequest(
        Guid tenantId, Guid endpointId, Guid? findingId, RemediationAction action, string? parameter, string reason,
        Guid? requestedByUserId, DateTimeOffset now)
        : base(tenantId)
    {
        var parameterError = RemediationCatalog.ValidateParameter(action, parameter);
        if (parameterError is not null)
        {
            throw new ValidationException("parameter", parameterError);
        }

        EndpointId = endpointId;
        FindingId = findingId;
        ActionKey = action.Key;
        Parameter = parameter;
        Risk = action.Risk;
        Reason = Guard.NotNullOrWhiteSpace(reason, "reason", 500);
        RequestedByUserId = requestedByUserId;
        RequestedAtUtc = now;
        ExpiresAtUtc = now + ApprovalWindow;
        Status = RemediationStatus.PendingApproval;
    }

    public Guid EndpointId { get; private set; }

    public Guid? FindingId { get; private set; }

    public string ActionKey { get; private set; } = string.Empty;

    public string? Parameter { get; private set; }

    public RemediationRisk Risk { get; private set; }

    public RemediationStatus Status { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    /// <summary>Null when the system (policy) raised the request.</summary>
    public Guid? RequestedByUserId { get; private set; }

    public DateTimeOffset RequestedAtUtc { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    /// <summary>Null when approved automatically by policy.</summary>
    public Guid? DecidedByUserId { get; private set; }

    public DateTimeOffset? DecidedAtUtc { get; private set; }

    public string? DecisionNote { get; private set; }

    public bool AutoApproved { get; private set; }

    public Guid? ApprovedUnderPolicyId { get; private set; }

    public DateTimeOffset? DispatchedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public string? ResultMessage { get; private set; }

    public static RemediationRequest ByUser(Guid tenantId, Guid endpointId, Guid? findingId, RemediationAction action, string? parameter, string reason, Guid userId, DateTimeOffset now) =>
        new(tenantId, endpointId, findingId, action, parameter, reason, userId, now);

    public static RemediationRequest BySystem(Guid tenantId, Guid endpointId, Guid? findingId, RemediationAction action, string? parameter, string reason, DateTimeOffset now) =>
        new(tenantId, endpointId, findingId, action, parameter, reason, null, now);

    /// <summary>
    /// Approval by a person. Self-approval is rejected, and a person cannot approve a request they raised.
    /// </summary>
    public void Approve(Guid approverUserId, string? note, DateTimeOffset now)
    {
        EnsurePending(now);
        if (RequestedByUserId == approverUserId)
        {
            throw new ForbiddenException("You cannot approve a remediation you requested. Ask a different owner or administrator.");
        }

        Status = RemediationStatus.Approved;
        DecidedByUserId = approverUserId;
        DecidedAtUtc = now;
        DecisionNote = Guard.Optional(note, "note", 500);
        ExpiresAtUtc = now + DeliveryWindow;
    }

    /// <summary>Policy-driven approval. Only Low-risk catalogue actions qualify; the domain refuses anything else.</summary>
    public void AutoApprove(Guid policyId, DateTimeOffset now)
    {
        EnsurePending(now);
        if (Risk != RemediationRisk.Low)
        {
            throw new ForbiddenException("Only Low-risk actions can be approved automatically.");
        }

        Status = RemediationStatus.Approved;
        AutoApproved = true;
        ApprovedUnderPolicyId = policyId;
        DecidedAtUtc = now;
        DecisionNote = "Approved automatically by the active policy (Low-risk action).";
        ExpiresAtUtc = now + DeliveryWindow;
    }

    public void Reject(Guid approverUserId, string note, DateTimeOffset now)
    {
        EnsurePending(now);
        Status = RemediationStatus.Rejected;
        DecidedByUserId = approverUserId;
        DecidedAtUtc = now;
        DecisionNote = Guard.NotNullOrWhiteSpace(note, "note", 500);
    }

    public void Cancel(Guid userId, DateTimeOffset now)
    {
        if (Status is not (RemediationStatus.PendingApproval or RemediationStatus.Approved))
        {
            throw new ConflictException($"A request that is {Status} cannot be cancelled.");
        }

        Status = RemediationStatus.Cancelled;
        DecidedByUserId = userId;
        DecidedAtUtc = now;
    }

    /// <summary>Closes a request that is no longer permitted (policy changed, device revoked). Not an approval decision.</summary>
    public void CancelBySystem(string note, DateTimeOffset now)
    {
        if (Status is not (RemediationStatus.PendingApproval or RemediationStatus.Approved))
        {
            return;
        }

        Status = RemediationStatus.Cancelled;
        DecidedAtUtc = now;
        DecisionNote = Guard.Optional(note, "note", 500);
    }

    public void MarkDispatched(DateTimeOffset now)
    {
        if (Status != RemediationStatus.Approved)
        {
            throw new ConflictException($"Only an approved request can be dispatched (current status: {Status}).");
        }

        Status = RemediationStatus.Dispatched;
        DispatchedAtUtc = now;
        ExpiresAtUtc = now + ResultWindow;
    }

    public void Complete(bool succeeded, string? message, DateTimeOffset now)
    {
        if (Status != RemediationStatus.Dispatched)
        {
            throw new ConflictException($"A result can only be recorded for a dispatched request (current status: {Status}).");
        }

        Status = succeeded ? RemediationStatus.Succeeded : RemediationStatus.Failed;
        CompletedAtUtc = now;
        ResultMessage = Guard.Optional(message, "message", 500);
    }

    /// <summary>Marks the request Expired if its current window has passed. Returns true when it changed.</summary>
    public bool ExpireIfDue(DateTimeOffset now)
    {
        if (now < ExpiresAtUtc)
        {
            return false;
        }

        switch (Status)
        {
            case RemediationStatus.PendingApproval:
            case RemediationStatus.Approved:
                Status = RemediationStatus.Expired;
                return true;
            case RemediationStatus.Dispatched:
                // Delivered but never reported back: we cannot know what happened.
                Status = RemediationStatus.Failed;
                CompletedAtUtc = now;
                ResultMessage = "The agent did not report a result in time.";
                return true;
            default:
                return false;
        }
    }

    private void EnsurePending(DateTimeOffset now)
    {
        if (Status != RemediationStatus.PendingApproval)
        {
            throw new ConflictException($"The request is not awaiting approval (current status: {Status}).");
        }

        if (now >= ExpiresAtUtc)
        {
            throw new ConflictException("The request expired before it was decided. Create a new one.");
        }
    }
}
