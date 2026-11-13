namespace Solvix.Agent.Contracts;

public sealed record HeartbeatRequest
{
    public required string AgentVersion { get; init; }

    public required AgentHealthReport Health { get; init; }
}

public enum AgentHealthStatus
{
    Healthy = 1,
    Degraded = 2,
    Unhealthy = 3,
}

public enum HealthIssueSeverity
{
    Info = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>Self-reported state of the agent process and its modules.</summary>
public sealed record AgentHealthReport
{
    public AgentHealthStatus Status { get; init; } = AgentHealthStatus.Healthy;

    public DateTimeOffset StartedAtUtc { get; init; }

    public long UptimeSeconds { get; init; }

    public DateTimeOffset? LastInventoryAtUtc { get; init; }

    public DateTimeOffset? LastSuccessfulContactAtUtc { get; init; }

    /// <summary>Consecutive failed calls before this heartbeat succeeded (reconnection indicator).</summary>
    public int ConsecutiveFailures { get; init; }

    public long WorkingSetBytes { get; init; }

    public DateTimeOffset? CertificateNotAfterUtc { get; init; }

    public string? OsBuild { get; init; }

    public IReadOnlyList<HealthIssue> Issues { get; init; } = [];

    /// <summary>When the operating system last started (derived from the system tick count). Null from agents before 1.1.</summary>
    public DateTimeOffset? SystemBootedAtUtc { get; init; }

    /// <summary>Operating-system health as measured on the device. Independent of agent connectivity.</summary>
    public SystemHealthSnapshot? System { get; init; }

    /// <summary>Local network reachability as observed by the agent. Independent of agent connectivity.</summary>
    public NetworkReachabilitySnapshot? Network { get; init; }
}

/// <summary>
/// Operating-system health signal. A successful heartbeat proves only that the
/// agent process can reach the server; these values describe the machine itself.
/// </summary>
public sealed record SystemHealthSnapshot
{
    public long? TotalMemoryBytes { get; init; }

    public long? AvailableMemoryBytes { get; init; }

    /// <summary>Drive holding the operating system (e.g. <c>C:\</c>).</summary>
    public string? SystemDrive { get; init; }

    public long? SystemDriveTotalBytes { get; init; }

    public long? SystemDriveFreeBytes { get; init; }

    /// <summary>Windows reports that a restart is required (servicing / Windows Update). Null when unknown.</summary>
    public bool? RebootPending { get; init; }

    public int? ProcessCount { get; init; }
}

public enum NetworkCheckKind
{
    Interface = 1,
    Gateway = 2,
    Dns = 3,
    Tcp = 4,
}

/// <summary>
/// Network reachability signal: what the device can reach on its local network.
/// It is collected by built-in probes against locally configured targets only;
/// the server cannot choose probe targets.
/// </summary>
public sealed record NetworkReachabilitySnapshot
{
    public DateTimeOffset CheckedAtUtc { get; init; }

    public IReadOnlyList<NetworkCheck> Checks { get; init; } = [];
}

public sealed record NetworkCheck
{
    public required NetworkCheckKind Kind { get; init; }

    /// <summary>What was probed (adapter name, gateway address, host name or host:port).</summary>
    public required string Target { get; init; }

    public bool Success { get; init; }

    public int? LatencyMs { get; init; }

    public string? Error { get; init; }
}

public sealed record HealthIssue
{
    public required string Component { get; init; }

    public HealthIssueSeverity Severity { get; init; } = HealthIssueSeverity.Warning;

    public required string Message { get; init; }
}

/// <summary>
/// The only data the server sends back on a heartbeat. It contains schedules
/// and well-known boolean hints that map to built-in agent behaviour; it can
/// never carry a command, script or executable payload.
/// </summary>
public sealed record HeartbeatResponse
{
    public required DateTimeOffset ServerTimeUtc { get; init; }

    public required AgentConfiguration Configuration { get; init; }

    /// <summary>An administrator asked for a fresh inventory (built-in collection only).</summary>
    public bool InventoryRequested { get; init; }

    /// <summary>The device certificate is in its renewal window.</summary>
    public bool CertificateRenewalRecommended { get; init; }

    /// <summary>Signed commands are waiting at <see cref="AgentRoutes.Commands"/> (0 for servers without remote management).</summary>
    public int PendingCommands { get; init; }
}

public sealed record UpdateCheckResponse
{
    public bool UpdateAvailable { get; init; }

    public AgentReleaseInfo? Release { get; init; }
}

/// <summary>
/// Release manifest. The agent only installs a package when the URL is HTTPS on
/// a locally allow-listed host, the SHA-256 matches, the package carries a valid
/// Authenticode signature from a locally pinned publisher, and the version is
/// strictly newer than the running one.
/// </summary>
public sealed record AgentReleaseInfo
{
    public required string Version { get; init; }

    public required string Channel { get; init; }

    public required Uri DownloadUrl { get; init; }

    /// <summary>Lower-case hex SHA-256 of the MSI package.</summary>
    public required string Sha256 { get; init; }

    public long SizeBytes { get; init; }

    public bool Mandatory { get; init; }

    public DateTimeOffset? PublishedAtUtc { get; init; }

    public Uri? ReleaseNotesUrl { get; init; }
}
