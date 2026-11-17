namespace Solvix.Agent.Core.Configuration;

using Solvix.Agent.Contracts;

/// <summary>
/// Local agent settings (<c>SolvixAgent</c> section). Security-relevant values
/// — server URL, certificate pins, update publisher and download hosts — come
/// only from local configuration written at install time, never from the server.
/// </summary>
public sealed class AgentOptions
{
    public const string SectionName = "SolvixAgent";

    /// <summary>Base URL of the agent endpoint, e.g. <c>https://agents.solvix.example:8443/</c>. HTTPS only.</summary>
    public Uri? ServerUrl { get; set; }

    /// <summary>Directory for state, credentials and logs. Defaults to <c>%ProgramData%\Solvix\Agent</c>.</summary>
    public string? DataDirectory { get; set; }

    /// <summary>
    /// Optional SHA-256 thumbprints of server certificates to trust in addition to
    /// the OS trust store (self-hosted servers with private certificates).
    /// </summary>
    public List<string> TrustedServerCertificateSha256 { get; set; } = [];

    /// <summary>
    /// Name of the local pipe through which people on this computer (tickets, chat) reach the service.
    /// The service owns the device credential; users never touch it.
    /// </summary>
    public string LocalPipeName { get; set; } = "solvix-agent-local";

    public int RequestTimeoutSeconds { get; set; } = 60;

    public RemoteAccessOptions RemoteAccess { get; set; } = new();

    public BackoffOptions Backoff { get; set; } = new();

    public UpdateOptions Updates { get; set; } = new();

    /// <summary>Consecutive authentication failures after which the credential is considered lost.</summary>
    public int MaxAuthenticationFailures { get; set; } = 3;

    /// <summary>Upload a full inventory at least this often even when nothing changed.</summary>
    public int InventoryMaxAgeHours { get; set; } = 24;

    public string ResolveDataDirectory() =>
        !string.IsNullOrWhiteSpace(DataDirectory)
            ? DataDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Solvix", "Agent");
}

public sealed class BackoffOptions
{
    public double InitialSeconds { get; set; } = 5;

    public double MaxSeconds { get; set; } = 900;

    public double Multiplier { get; set; } = 2;

    /// <summary>± fraction of randomisation so a fleet does not reconnect in lock-step.</summary>
    public double JitterRatio { get; set; } = 0.2;
}

public sealed class UpdateOptions
{
    /// <summary>Local kill switch for automatic updates.</summary>
    public bool Enabled { get; set; } = true;

    public string Channel { get; set; } = "stable";

    /// <summary>Hosts packages may be downloaded from (HTTPS only). Empty = updates disabled.</summary>
    public List<string> AllowedDownloadHosts { get; set; } = [];

    /// <summary>SHA-1/SHA-256 thumbprints of the code-signing certificates accepted for MSI packages.</summary>
    public List<string> TrustedPublisherThumbprints { get; set; } = [];

    public long MaxPackageBytes { get; set; } = 200L * 1024 * 1024;
}


/// <summary>
/// Remote command sessions. Off unless the person installing the agent turns it on in the local
/// configuration (<c>agent.json</c>); the server cannot enable it.
/// </summary>
public sealed class RemoteAccessOptions
{
    public RemoteAccessMode Mode { get; set; } = RemoteAccessMode.Disabled;

    /// <summary>Program that provides the command shell. Defaults to PowerShell on Windows and <c>/bin/sh</c> elsewhere.</summary>
    public string? Shell { get; set; }

    /// <summary>Arguments for <see cref="Shell"/> (default: read commands from standard input).</summary>
    public List<string>? ShellArguments { get; set; }

    /// <summary>How long the person at the computer has to answer a consent prompt.</summary>
    public int ConsentTimeoutSeconds { get; set; } = 90;
}
