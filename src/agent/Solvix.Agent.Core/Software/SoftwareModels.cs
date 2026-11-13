namespace Solvix.Agent.Core.Software;

using System.Text.Json;
using System.Text.Json.Serialization;
using Solvix.Agent.Contracts.RemoteManagement;
using Solvix.Agent.Contracts.Software;
using Solvix.Agent.Core.Configuration;
using Solvix.Agent.Core.Updates;

/// <summary>Installed applications as detection sees them (Uninstall registry keys on Windows).</summary>
public interface IInstalledApplicationSource
{
    IReadOnlyList<DetectedApplication> GetInstalledApplications();
}

/// <summary>Non-Windows development builds: nothing is installed.</summary>
public sealed class EmptyInstalledApplicationSource : IInstalledApplicationSource
{
    public IReadOnlyList<DetectedApplication> GetInstalledApplications() => [];
}

/// <summary>
/// What the unprivileged agent hands to the privileged executor: the signed command exactly as the
/// server signed it, and where it staged the (already verified) package. The executor trusts none of
/// it: it verifies the signature, binding, expiry and replay itself, copies the package into its own
/// directory and verifies the checksum and signature of that copy before running anything.
/// </summary>
public sealed record ExecutorRequest
{
    public required SignedCommand Command { get; init; }

    public string? PackagePath { get; init; }

    /// <summary>The agent's pinned key set and identity; the executor pins them on first use only (trust on first use).</summary>
    public required CommandKeySet Keys { get; init; }

    public required Guid TenantId { get; init; }

    public required Guid DeviceId { get; init; }
}

public sealed record ExecutorResponse
{
    /// <summary>The installer was started.</summary>
    public bool Executed { get; init; }

    public int? ExitCode { get; init; }

    public bool TimedOut { get; init; }

    public bool Cancelled { get; init; }

    /// <summary>Why the executor refused to run anything (see <see cref="SoftwareErrorCodes"/> and <see cref="CommandRejectionCodes"/>).</summary>
    public string? RefusalCode { get; init; }

    public string? RefusalMessage { get; init; }

    /// <summary>Tail of the installer log.</summary>
    public string? Log { get; init; }

    public static ExecutorResponse Refuse(string code, string message) => new() { RefusalCode = code, RefusalMessage = message };
}

/// <summary>Runs installers with elevated rights. Implemented by the named-pipe client to the Solvix Software Executor service.</summary>
public interface ISoftwareExecutor
{
    Task<ExecutorResponse> ExecuteAsync(ExecutorRequest request, CancellationToken cancellationToken);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ExecutorRequest))]
[JsonSerializable(typeof(ExecutorResponse))]
[JsonSerializable(typeof(PinnedIdentity))]
internal sealed partial class SoftwareJsonContext : JsonSerializerContext
{
}

public sealed record PinnedIdentity
{
    public required Guid TenantId { get; init; }

    public required Guid DeviceId { get; init; }
}

/// <summary>Local rules for package types and signatures, applied by the agent and again by the executor.</summary>
public static class PackageTrust
{
    public static (string Code, string Message)? CheckType(SoftwareOperation operation, SoftwareAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return (SoftwareErrorCodes.DisabledLocally, "Software deployment is disabled on this device.");
        }

        return options.AllowedInstallerTypes.Contains(operation.InstallerType.ToString(), StringComparer.OrdinalIgnoreCase)
            ? null
            : (SoftwareErrorCodes.InstallerTypeNotAllowed, $"{operation.InstallerType} installers are not allowed on this device.");
    }

    /// <summary>
    /// A package that was signed when it was uploaded must carry a valid signature from that same signer
    /// (and, when the device pins publishers, from one of those). An unsigned package is accepted only when
    /// the local policy allows unsigned packages.
    /// </summary>
    public static (string Code, string Message)? CheckSignature(string path, SoftwareOperation operation, SoftwareAgentOptions options, IPackageSignatureVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(verifier);
        if (!operation.RequireSignature)
        {
            return options.AllowUnsignedPackages
                ? null
                : (SoftwareErrorCodes.UnsignedNotAllowed, "This device only accepts signed packages.");
        }

        var expected = operation.ExpectedSignerThumbprint!;
        if (options.TrustedPublisherThumbprints.Count > 0
            && verifier.Verify(path, options.TrustedPublisherThumbprints) is { } notPinned)
        {
            return (SoftwareErrorCodes.SignerNotTrusted, notPinned);
        }

        return verifier.Verify(path, [expected]) is { } problem
            ? (SoftwareErrorCodes.SignatureInvalid, problem)
            : null;
    }
}
