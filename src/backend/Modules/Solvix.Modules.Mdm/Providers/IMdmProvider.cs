namespace Solvix.Modules.Mdm.Providers;

using Solvix.Modules.Mdm.Domain;

/// <summary>
/// One implementation per platform, each speaking that platform's official
/// management protocol (Android Management API, Apple MDM). Providers share
/// this contract only; no logic is shared between them and none is shared
/// with any desktop agent.
/// </summary>
public interface IMdmProvider
{
    MobilePlatform Platform { get; }

    /// <summary>Whether provider credentials/configuration are present. Unconfigured providers reject every call with a clear error.</summary>
    bool IsConfigured { get; }

    /// <summary>Creates the artefact a device or user needs to enroll (QR payload, profile URL).</summary>
    Task<EnrollmentArtifact> CreateEnrollmentAsync(EnrollmentRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Translates a policy for the target scenario and publishes it. The report lists settings that were
    /// deliberately not applied because of the platform or the ownership boundary.
    /// </summary>
    Task<PolicyDeploymentReport> DeployPolicyAsync(PolicyDeployment deployment, CancellationToken cancellationToken);

    /// <summary>Sends a command to the platform for one device.</summary>
    Task<CommandDispatchResult> DispatchCommandAsync(CommandDispatch dispatch, CancellationToken cancellationToken);
}

public sealed record EnrollmentRequest(
    Guid TenantId,
    Guid TokenId,
    string Name,
    ManagementScenario Scenario,
    Guid PolicyId,
    string? EnterpriseName,
    DateTimeOffset ExpiresAtUtc,
    string? Secret);

/// <summary>What the administrator hands to the user.</summary>
public sealed record EnrollmentArtifact
{
    /// <summary>Android: QR code JSON the device setup wizard scans.</summary>
    public string? QrPayload { get; init; }

    /// <summary>Android: the raw enrollment token value (for afw#setup / manual entry).</summary>
    public string? TokenValue { get; init; }

    /// <summary>iOS: URL that serves the enrollment profile.</summary>
    public string? EnrollmentUrl { get; init; }

    /// <summary>Provider-side identifier to correlate later device reports with this token.</summary>
    public string? ProviderReference { get; init; }

    public IReadOnlyList<string> Instructions { get; init; } = [];
}

public sealed record PolicyAppSpec(
    string Identifier,
    string Name,
    AppInstallType InstallType,
    long? StoreId,
    string? ManagedConfigurationJson);

public sealed record PolicyDeployment(
    Guid PolicyId,
    string Name,
    int Version,
    ManagementScenario Scenario,
    MobilePolicySettings Settings,
    IReadOnlyList<PolicyAppSpec> Apps,
    string? EnterpriseName,
    Func<string?, string?> RevealSecret);

public sealed record PolicyDeploymentReport(
    string? ProviderReference,
    IReadOnlyList<string> AppliedSettings,
    IReadOnlyList<string> SkippedSettings)
{
    public static PolicyDeploymentReport Empty { get; } = new(null, [], []);
}

public sealed record CommandDispatch(
    Guid CommandId,
    DeviceCommandType Type,
    MobileDevice Device,
    string? ParametersJson,
    string? EnterpriseName,
    string? RevealedUnlockToken);

/// <summary>
/// Outcome of handing a command to the platform. <see cref="Completed"/> is true when the
/// platform finished synchronously. <see cref="StaysQueued"/> is true when the device pulls commands
/// itself (Apple MDM): the command waits in the queue and is marked sent when the device collects it.
/// Otherwise the command is sent to the platform (Android Management API) and tracked by reference.
/// </summary>
public sealed record CommandDispatchResult(string? ProviderReference, bool Completed = false, string? Error = null, bool StaysQueued = false);
