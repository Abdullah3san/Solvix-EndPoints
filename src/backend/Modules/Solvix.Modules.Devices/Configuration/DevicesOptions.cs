namespace Solvix.Modules.Devices.Configuration;

using System.ComponentModel.DataAnnotations;

/// <summary>Settings of the device management module (<c>Solvix:Devices</c>).</summary>
public sealed class DevicesOptions
{
    public const string SectionName = "Solvix:Devices";

    [Required]
    public DeviceCaOptions CertificateAuthority { get; set; } = new();

    [Required]
    public EnrollmentOptions Enrollment { get; set; } = new();

    [Required]
    public AgentPolicyOptions Agent { get; set; } = new();

    [Required]
    public MonitoringOptions Monitoring { get; set; } = new();

    /// <summary>
    /// Name of the request header carrying the client certificate when TLS is
    /// terminated by a trusted reverse proxy (e.g. <c>X-Client-Cert</c>). Leave
    /// empty when Kestrel terminates mutual TLS itself. NEVER enable this unless
    /// the API is reachable only through a proxy that overwrites the header.
    /// </summary>
    public string? ForwardedClientCertificateHeader { get; set; }

    /// <summary>Releases offered to agents (platform operated).</summary>
    public List<AgentReleaseOptions> Releases { get; set; } = [];
}

/// <summary>
/// The platform device CA that signs device certificates. Production loads a
/// PKCS#12 bundle (path or base64) whose password comes from a secret store.
/// <see cref="AutoGenerate"/> creates a CA on first start and is intended for
/// development and single-node evaluation only.
/// </summary>
public sealed class DeviceCaOptions
{
    /// <summary>PKCS#12 file containing the CA certificate and private key.</summary>
    public string? PfxPath { get; set; }

    /// <summary>Base64 encoded PKCS#12 bundle (alternative to <see cref="PfxPath"/>).</summary>
    public string? PfxBase64 { get; set; }

    public string? PfxPassword { get; set; }

    /// <summary>Generate a CA when none is configured; persisted to <see cref="PfxPath"/> when set.</summary>
    public bool AutoGenerate { get; set; }

    public string SubjectName { get; set; } = "CN=Solvix Device CA, O=Solvix EndPoints";

    [Range(1, 397)]
    public int DeviceCertificateLifetimeDays { get; set; } = 90;

    /// <summary>Agents are asked to renew once the remaining lifetime falls below this.</summary>
    [Range(1, 180)]
    public int RenewalWindowDays { get; set; } = 30;

    public bool IsConfigured => AutoGenerate || !string.IsNullOrWhiteSpace(PfxPath) || !string.IsNullOrWhiteSpace(PfxBase64);
}

public sealed class EnrollmentOptions
{
    [Range(1, 24 * 30)]
    public int DefaultTokenLifetimeHours { get; set; } = 24;

    /// <summary>Upper bound an administrator can choose; keeps enrollment credentials short-lived.</summary>
    [Range(1, 24 * 30)]
    public int MaxTokenLifetimeHours { get; set; } = 24 * 7;

    [Range(1, 100_000)]
    public int MaxTokenUses { get; set; } = 10_000;

    /// <summary>Enrollment attempts per client IP and minute.</summary>
    [Range(1, 100_000)]
    public int RateLimitPerMinute { get; set; } = 30;
}

public sealed class AgentPolicyOptions
{
    /// <summary>Platform default; tenants can override it in their monitoring settings.</summary>
    [Range(15, 3600)]
    public int HeartbeatIntervalSeconds { get; set; } = 60;

    [Range(15, 7 * 24 * 60)]
    public int InventoryIntervalMinutes { get; set; } = 240;

    [Range(15, 7 * 24 * 60)]
    public int UpdateCheckIntervalMinutes { get; set; } = 360;

    public string DefaultUpdateChannel { get; set; } = "stable";
}

public sealed class AgentReleaseOptions
{
    public string Version { get; set; } = string.Empty;

    public string Channel { get; set; } = "stable";

    public Uri? DownloadUrl { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public bool Mandatory { get; set; }

    public DateTimeOffset? PublishedAtUtc { get; set; }

    public Uri? ReleaseNotesUrl { get; set; }
}

/// <summary>
/// Platform settings of real-time monitoring (<c>Solvix:Devices:Monitoring</c>).
/// Per-tenant thresholds live in <c>monitoring_settings</c>; the defaults here
/// apply to tenants that never changed them.
/// </summary>
public sealed class MonitoringOptions
{
    /// <summary>Default "warning" threshold: no contact for this long (seconds).</summary>
    [Range(30, 7 * 24 * 3600)]
    public int DefaultWarningAfterSeconds { get; set; } = 120;

    /// <summary>Default "offline" threshold: no contact for MORE than this long (seconds).</summary>
    [Range(60, 7 * 24 * 3600)]
    public int DefaultOfflineAfterSeconds { get; set; } = 300;

    /// <summary>Run the background presence monitor (offline detection, rollover, retention) on this node.</summary>
    public bool RunMonitor { get; set; } = true;

    [Range(1, 300)]
    public int SweepIntervalSeconds { get; set; } = 15;

    /// <summary>Agent contact timestamps are buffered in memory and written in one batch per tenant this often.</summary>
    [Range(1, 60)]
    public int FlushIntervalSeconds { get; set; } = 5;

    /// <summary>Unchanged network/OS snapshots and agent health reports are re-persisted at most this often.</summary>
    [Range(30, 24 * 3600)]
    public int SignalPersistIntervalSeconds { get; set; } = 300;

    /// <summary>A pause between monitor sweeps longer than this means the platform was not observing (outage/restart).</summary>
    [Range(15, 3600)]
    public int OutageDetectionSeconds { get; set; } = 60;

    /// <summary>
    /// Upper bound of the extra grace given to agents after a platform outage, so
    /// agents still in their retry backoff are not declared offline (agent default max backoff: 900 s).
    /// </summary>
    [Range(0, 24 * 3600)]
    public int MaxRecoveryGraceSeconds { get; set; } = 900;

    [Range(1, 3650)]
    public int EventRetentionDays { get; set; } = 180;

    [Range(31, 3650)]
    public int AvailabilityRetentionDays { get; set; } = 400;

    [Range(10, 100_000)]
    public int SweepBatchSize { get; set; } = 2000;

    /// <summary>Use the Redis backplane for SignalR (required when the API runs on more than one node).</summary>
    public bool RedisBackplane { get; set; }
}
