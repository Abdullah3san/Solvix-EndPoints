namespace Solvix.Agent.Core.Configuration;

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

    public int RequestTimeoutSeconds { get; set; } = 60;

    /// <summary>Remote administration (Phase 06). Local policy that the server cannot override.</summary>
    public RemoteManagementAgentOptions RemoteManagement { get; set; } = new();

    /// <summary>Software deployment (Phase 07). Local policy that the server cannot override.</summary>
    public SoftwareAgentOptions Software { get; set; } = new();

    public BackoffOptions Backoff { get; set; } = new();

    public UpdateOptions Updates { get; set; } = new();

    /// <summary>Local network reachability probes (targets are local configuration only).</summary>
    public NetworkProbeOptions Network { get; set; } = new();

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

public sealed class NetworkProbeOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Ping the default gateway. Disable on networks whose gateway drops ICMP.</summary>
    public bool PingGateway { get; set; } = true;

    /// <summary>Host names to resolve. Empty = the server host name.</summary>
    public List<string> DnsHostNames { get; set; } = [];

    /// <summary>Internal services to test, as <c>host:port</c> (e.g. a domain controller on 389).</summary>
    public List<string> TcpTargets { get; set; } = [];

    public int TimeoutMilliseconds { get; set; } = 2000;

    /// <summary>Minimum seconds between probe runs; the last result is reused in between.</summary>
    public int IntervalSeconds { get; set; } = 60;
}

public sealed class RemoteManagementAgentOptions
{
    /// <summary>Set to false to refuse all remote commands on this device (the agent still reports inventory and health).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Command types this device refuses regardless of what the server sends (e.g. <c>system.shutdown</c>).</summary>
    public string[] DisabledCommands { get; set; } = [];

    /// <summary>How long one command poll may be held open by the server.</summary>
    public int PollWaitSeconds { get; set; } = 25;
}

public sealed class SoftwareAgentOptions
{
    /// <summary>Set to false to refuse every software installation and removal on this device (detection still answers).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Installer types this device accepts (<c>Msi</c>, <c>Exe</c>).</summary>
    public string[] AllowedInstallerTypes { get; set; } = ["Msi", "Exe"];

    /// <summary>Accept packages that carry no Authenticode signature (they still need the extra approval on the server).</summary>
    public bool AllowUnsignedPackages { get; set; } = true;

    /// <summary>
    /// When not empty, signed packages are only accepted from these signing certificates (SHA-1 or SHA-256
    /// thumbprints), in addition to matching the signer recorded when the package was uploaded.
    /// </summary>
    public List<string> TrustedPublisherThumbprints { get; set; } = [];

    public long MaxPackageBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Named pipe of the Solvix Software Executor service (LocalSystem).</summary>
    public string ExecutorPipeName { get; set; } = "SolvixSoftwareExecutor";

    public int ExecutorConnectTimeoutSeconds { get; set; } = 10;

    /// <summary>Data directory of the executor (SYSTEM and Administrators only). Defaults to <c>%ProgramData%\Solvix\Executor</c>.</summary>
    public string? ExecutorDataDirectory { get; set; }

    public int DownloadAttempts { get; set; } = 4;

    public string ResolveExecutorDirectory() =>
        !string.IsNullOrWhiteSpace(ExecutorDataDirectory)
            ? ExecutorDataDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Solvix", "Executor");
}
