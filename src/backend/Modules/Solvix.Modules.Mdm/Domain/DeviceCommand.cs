namespace Solvix.Modules.Mdm.Domain;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Exceptions;

/// <summary>
/// A management action for one device. Destructive commands start in
/// <see cref="CommandState.PendingApproval"/> and need a different administrator
/// to approve them before anything is sent to the platform.
/// </summary>
public sealed class DeviceCommand : TenantEntity
{
    public const int ReasonMaxLength = 500;
    public const int ErrorMaxLength = 2000;

    private DeviceCommand()
    {
    }

    private DeviceCommand(Guid tenantId, Guid deviceId, DeviceCommandType type, Guid requestedBy, string? reason, string? parametersJson)
        : base(tenantId)
    {
        DeviceId = deviceId;
        Type = type;
        RequestedByUserId = requestedBy;
        Reason = reason;
        ParametersJson = parametersJson;
    }

    public Guid DeviceId { get; private set; }

    public DeviceCommandType Type { get; private set; }

    public CommandState State { get; private set; }

    public string? Reason { get; private set; }

    /// <summary>Command-specific arguments (for example the app to install), as a JSON object.</summary>
    public string? ParametersJson { get; private set; }

    public Guid RequestedByUserId { get; private set; }

    public Guid? DecidedByUserId { get; private set; }

    public DateTimeOffset? DecidedAtUtc { get; private set; }

    public string? DecisionNote { get; private set; }

    public DateTimeOffset? DispatchedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    /// <summary>Provider correlation: Android operation name; Apple uses the command id as CommandUUID.</summary>
    public string? ProviderReference { get; private set; }

    public string? Error { get; private set; }

    public bool RequiresApproval => MdmCapabilities.RequiresApproval(Type);

    public bool IsTerminal => State is CommandState.Acknowledged or CommandState.Failed or CommandState.Expired
        or CommandState.Rejected or CommandState.Cancelled;

    public static DeviceCommand Create(
        Guid tenantId,
        Guid deviceId,
        DeviceCommandType type,
        Guid requestedBy,
        string? reason,
        string? parametersJson,
        DateTimeOffset now,
        TimeSpan timeToLive)
    {
        var command = new DeviceCommand(tenantId, deviceId, type, requestedBy, Trim(reason, ReasonMaxLength), parametersJson)
        {
            ExpiresAtUtc = now + timeToLive,
        };
        command.State = command.RequiresApproval ? CommandState.PendingApproval : CommandState.Queued;
        return command;
    }

    /// <summary>Records the approval decision. The approver must not be the requester unless self-approval is explicitly allowed.</summary>
    public void Approve(Guid approver, string? note, DateTimeOffset now, bool allowSelfApproval)
    {
        EnsurePendingApproval(now);
        if (approver == RequestedByUserId && !allowSelfApproval)
        {
            throw new ForbiddenException("A destructive command must be approved by a different administrator than the one who requested it.");
        }

        DecidedByUserId = approver;
        DecidedAtUtc = now;
        DecisionNote = Trim(note, ReasonMaxLength);
        State = CommandState.Queued;
    }

    public void Reject(Guid approver, string? note, DateTimeOffset now)
    {
        EnsurePendingApproval(now);
        DecidedByUserId = approver;
        DecidedAtUtc = now;
        DecisionNote = Trim(note, ReasonMaxLength);
        State = CommandState.Rejected;
        CompletedAtUtc = now;
    }

    public void Cancel(DateTimeOffset now)
    {
        if (State is not (CommandState.PendingApproval or CommandState.Queued))
        {
            throw new ConflictException($"A command in state {State} can no longer be cancelled.");
        }

        State = CommandState.Cancelled;
        CompletedAtUtc = now;
    }

    public void MarkSent(string? providerReference, DateTimeOffset now)
    {
        if (State is not (CommandState.Queued or CommandState.Sent))
        {
            throw new ConflictException($"A command in state {State} cannot be sent.");
        }

        State = CommandState.Sent;
        DispatchedAtUtc ??= now;
        ProviderReference = providerReference ?? ProviderReference;
    }

    /// <summary>The device reported it could not act yet (Apple NotNow); the command is re-queued.</summary>
    public void Requeue()
    {
        if (State == CommandState.Sent)
        {
            State = CommandState.Queued;
        }
    }

    public void MarkAcknowledged(DateTimeOffset now)
    {
        if (IsTerminal)
        {
            return;
        }

        State = CommandState.Acknowledged;
        CompletedAtUtc = now;
        Error = null;
    }

    public void MarkFailed(string error, DateTimeOffset now)
    {
        if (IsTerminal)
        {
            return;
        }

        State = CommandState.Failed;
        CompletedAtUtc = now;
        Error = Trim(error, ErrorMaxLength) ?? "Unknown error.";
    }

    public bool ExpireIfDue(DateTimeOffset now)
    {
        if (IsTerminal || now < ExpiresAtUtc)
        {
            return false;
        }

        State = CommandState.Expired;
        CompletedAtUtc = now;
        return true;
    }

    private void EnsurePendingApproval(DateTimeOffset now)
    {
        if (State != CommandState.PendingApproval)
        {
            throw new ConflictException($"The command is not awaiting approval (state: {State}).");
        }

        if (now >= ExpiresAtUtc)
        {
            throw new ConflictException("The approval window for this command has expired. Request it again.");
        }
    }

    private static string? Trim(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
