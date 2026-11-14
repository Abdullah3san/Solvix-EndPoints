namespace Solvix.SharedKernel.Fleet;

/// <summary>
/// The closed set of device operations the platform can perform. There is deliberately
/// no "run script" / "run command" member: every operation is a named, parameterised
/// handler implemented by the command module.
/// </summary>
public enum DeviceCommandType
{
    RefreshInventory = 1,
    CollectDiagnostics = 2,
    ClearTempFiles = 3,
    RestartService = 4,
    RestartDevice = 5,
    UpdateSoftware = 6,
    DeploySoftware = 7,
    UninstallSoftware = 8,
}

/// <summary>A fully-typed command. Parameters are identifiers/numbers only, never script text.</summary>
public sealed record DeviceCommandRequest(
    Guid IdempotencyKey,
    DeviceCommandType Type,
    IReadOnlyDictionary<string, string> Parameters,
    IReadOnlyList<Guid> DeviceIds,
    string Reason);

public sealed record DeviceCommandValidation(bool IsAllowed, IReadOnlyList<string> Problems)
{
    public static DeviceCommandValidation Allowed { get; } = new(true, []);

    public static DeviceCommandValidation Denied(params string[] problems) => new(false, problems);
}

public enum DeviceCommandStatus
{
    Queued = 1,
    Succeeded = 2,
    Failed = 3,
    Skipped = 4,
}

public sealed record DeviceCommandOutcome(Guid DeviceId, DeviceCommandStatus Status, string Message);

/// <summary>
/// The only way the assistant can cause a change on devices. The implementation owns the
/// definitive validation (allow-lists, maintenance windows, device ownership, permissions)
/// and is called both when a proposal is created and again immediately before execution.
/// </summary>
public interface IDeviceCommandGateway
{
    bool IsAvailable { get; }

    Task<DeviceCommandValidation> ValidateAsync(FleetAccess access, DeviceCommandRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<DeviceCommandOutcome>> ExecuteAsync(FleetAccess access, DeviceCommandRequest request, CancellationToken cancellationToken);
}
