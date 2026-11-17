namespace Solvix.Agent.Contracts;

/// <summary>
/// <c>enrollment.json</c>, shipped next to <c>Solvix.Agent.exe</c> in the package an administrator
/// downloads. It lets the agent install and enroll itself without a pasted command line.
/// The token is short-lived and use-limited; the agent deletes the file after it has been
/// consumed. It is a separate file (not embedded in the exe) so the exe keeps its code signature.
/// </summary>
public sealed record AgentEnrollmentFile
{
    public const string FileName = "enrollment.json";
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    /// <summary>HTTPS URL of the agent endpoint.</summary>
    public required string ServerUrl { get; init; }

    public required string EnrollmentToken { get; init; }

    /// <summary>SHA-256 pin of the server TLS certificate (only for private/self-signed certificates).</summary>
    public string? ServerCertificateSha256 { get; init; }

    public DateTimeOffset? TokenExpiresAtUtc { get; init; }

    /// <summary>Workspace name, shown to the person running the installer so they know where the device joins.</summary>
    public string? WorkspaceName { get; init; }

    /// <summary>Branch / department the device will be placed in, for display only.</summary>
    public string? Placement { get; init; }
}
