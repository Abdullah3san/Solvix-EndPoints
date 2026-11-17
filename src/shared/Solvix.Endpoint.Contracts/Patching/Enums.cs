namespace Solvix.Endpoint.Contracts.Patching;

using System.Text.Json.Serialization;

/// <summary>Windows Update classification (the WUA "UpdateClassification" category).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UpdateClassification>))]
public enum UpdateClassification
{
    Unknown = 0,
    CriticalUpdates = 1,
    SecurityUpdates = 2,
    DefinitionUpdates = 3,
    UpdateRollups = 4,
    ServicePacks = 5,
    FeaturePacks = 6,
    Tools = 7,
    Drivers = 8,
    Updates = 9,
    Upgrades = 10,
}

/// <summary>Microsoft Security Response Center severity (<c>IUpdate.MsrcSeverity</c>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MsrcSeverity>))]
public enum MsrcSeverity
{
    Unspecified = 0,
    Low = 1,
    Moderate = 2,
    Important = 3,
    Critical = 4,
}

/// <summary><c>InstallationBehavior.RebootBehavior</c> (WUA <c>InstallationRebootBehavior</c>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RebootBehavior>))]
public enum RebootBehavior
{
    NeverReboots = 0,
    AlwaysRequiresReboot = 1,
    CanRequestReboot = 2,
}

/// <summary>WUA <c>OperationResultCode</c>; numeric values match the COM enumeration.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OperationResultCode>))]
public enum OperationResultCode
{
    NotStarted = 0,
    InProgress = 1,
    Succeeded = 2,
    SucceededWithErrors = 3,
    Failed = 4,
    Aborted = 5,
}

/// <summary>WUA <c>UpdateOperation</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UpdateOperation>))]
public enum UpdateOperation
{
    Installation = 1,
    Uninstallation = 2,
}

/// <summary>What a deployment does when an update needs a restart.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RestartBehavior>))]
public enum RestartBehavior
{
    /// <summary>Never restart; the device stays "pending restart" until someone restarts it.</summary>
    Never = 1,

    /// <summary>Restart only inside the deployment's maintenance window (or immediately when it has none).</summary>
    WithinMaintenanceWindow = 2,

    /// <summary>Restart inside the window, and force a restart once the deadline has passed.</summary>
    ForceAtDeadline = 3,
}

[JsonConverter(typeof(JsonStringEnumConverter<PatchJobStage>))]
public enum PatchJobStage
{
    Searching = 1,
    Downloading = 2,
    Installing = 3,
    Verifying = 4,
}

[JsonConverter(typeof(JsonStringEnumConverter<PatchJobOutcome>))]
public enum PatchJobOutcome
{
    /// <summary>Every update installed (possibly pending a restart).</summary>
    Succeeded = 1,

    /// <summary>At least one update installed and at least one failed.</summary>
    PartiallySucceeded = 2,

    /// <summary>No update installed.</summary>
    Failed = 3,

    /// <summary>None of the updates is applicable or all are already installed.</summary>
    NotApplicable = 4,

    /// <summary>The agent stopped before finishing (maintenance window closed, service stopping).</summary>
    Aborted = 5,
}

[JsonConverter(typeof(JsonStringEnumConverter<RestartAckStatus>))]
public enum RestartAckStatus
{
    /// <summary>The restart was initiated with the grace period shown to signed-in users.</summary>
    Initiated = 1,

    /// <summary>The restart could not be initiated.</summary>
    Failed = 2,

    /// <summary>The agent did not restart (window closed or blocked by a local condition).</summary>
    Postponed = 3,
}
