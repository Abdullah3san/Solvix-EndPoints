namespace Solvix.SharedKernel.Endpoints;

/// <summary>
/// Lets another module (software management) send an allow-listed, signed command to a device
/// through the remote management command broker without depending on that module. The broker
/// still owns signing, sequencing, delivery, re-authorization of the requester at delivery time,
/// expiry, execution timeouts, cancellation and the remote audit trail.
///
/// Dispatching is idempotent on <see cref="DelegatedCommandRequest.ExecutionId"/>: the caller
/// records the id it is about to use first, so a crash between the two writes can never send
/// the same work twice.
/// </summary>
public interface IRemoteCommandDispatcher
{
    /// <summary>Creates (or returns the already created) signed command. Runs in the current tenant scope.</summary>
    Task<RemoteCommandState> DispatchAsync(DelegatedCommandRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<RemoteCommandState>> GetStatesAsync(IReadOnlyCollection<Guid> executionIds, CancellationToken cancellationToken);

    /// <summary>
    /// Cancels a delegated command: immediately when the device has not accepted it yet, otherwise by
    /// queueing a signed cancel. Returns the state after the request; finished commands are left alone.
    /// </summary>
    Task<RemoteCommandState?> CancelAsync(Guid executionId, Guid cancelledBy, CancellationToken cancellationToken);

    /// <summary>A command of <paramref name="deviceId"/> (agent requests), or null when the device has no such command.</summary>
    Task<RemoteCommandState?> FindForDeviceAsync(Guid executionId, Guid deviceId, CancellationToken cancellationToken);
}

/// <summary>
/// A command another module asks the broker to send. Who asked and who approved come from that
/// module's own records (deployment creator and approvers), never from a request body.
/// </summary>
public sealed record DelegatedCommandRequest(
    Guid ExecutionId,
    Guid DeviceId,
    string CommandType,
    IReadOnlyDictionary<string, string> Parameters,
    Guid RequestedBy,
    Guid? ApprovalId,
    Guid? ApprovedBy,
    string? Reason,
    DateTimeOffset ExpiresAtUtc,
    TimeSpan ExecutionTimeout);

public enum RemoteCommandPhase
{
    /// <summary>Signed and waiting for the device, or delivered but not yet accepted.</summary>
    Pending = 1,

    /// <summary>The device verified and accepted the command and is running it.</summary>
    Running = 2,

    Succeeded = 10,
    Failed = 11,
    Cancelled = 12,

    /// <summary>The device did not fetch the command before it expired (offline).</summary>
    Expired = 13,

    /// <summary>Refused by the server at delivery (requester lost access) or by the device (verification).</summary>
    Rejected = 14,

    /// <summary>Accepted but no result arrived in time (device went offline or crashed mid-execution).</summary>
    TimedOut = 15,
}

public sealed record RemoteCommandState(
    Guid ExecutionId,
    Guid DeviceId,
    string DeviceName,
    string CommandType,
    RemoteCommandPhase Phase,
    IReadOnlyDictionary<string, string> Parameters,
    DateTimeOffset? AcknowledgedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int? ExitCode,
    string? Output,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool IsFinal => Phase >= RemoteCommandPhase.Succeeded;
}

/// <summary>
/// Told (after commit, in the device's tenant scope) that a delegated command reached a final
/// state, so its owner can update its own records at once instead of waiting for its next
/// reconciliation pass. Observers must never make the agent request fail.
/// </summary>
public interface IRemoteCommandObserver
{
    /// <summary>Command types (prefix match, e.g. <c>software.</c>) this observer cares about.</summary>
    string CommandTypePrefix { get; }

    Task OnCommandFinishedAsync(RemoteCommandState state, CancellationToken cancellationToken);
}

/// <summary>One application from a device's software inventory, as reported by its agent.</summary>
public sealed record InstalledSoftwareEntry(string Name, string? Version, string? Publisher, string? Scope, DateOnly? InstallDate);

/// <summary>
/// Told (after commit, in the device's tenant scope) whenever a device uploads its inventory.
/// Observers must never make the upload fail: the publisher logs and swallows their errors.
/// </summary>
public interface ISoftwareInventoryObserver
{
    Task OnSoftwareInventoryAsync(Guid deviceId, DateTimeOffset collectedAtUtc, IReadOnlyList<InstalledSoftwareEntry> software, CancellationToken cancellationToken);
}
