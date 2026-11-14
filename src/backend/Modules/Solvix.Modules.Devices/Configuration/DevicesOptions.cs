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

    /// <summary>The downloadable agent package (signed exe + enrollment.json) offered on the enrollment page.</summary>
    [Required]
    public AgentPackageOptions AgentPackage { get; set; } = new();

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
    [Range(15, 3600)]
    public int HeartbeatIntervalSeconds { get; set; } = 60;

    [Range(15, 7 * 24 * 60)]
    public int InventoryIntervalMinutes { get; set; } = 240;

    [Range(15, 7 * 24 * 60)]
    public int UpdateCheckIntervalMinutes { get; set; } = 360;

    /// <summary>A device is reported offline after this long without contact.</summary>
    [Range(30, 7 * 24 * 3600)]
    public int OfflineAfterSeconds { get; set; } = 300;

    public string DefaultUpdateChannel { get; set; } = "stable";
}

public sealed class AgentPackageOptions
{
    /// <summary>
    /// Folder on the API host containing the published (and code-signed) <c>Solvix.Agent.exe</c> and its
    /// <c>appsettings.json</c>. The exe is served byte-for-byte so its Authenticode signature stays valid.
    /// </summary>
    public string? Directory { get; set; }

    /// <summary>Public HTTPS URL of the agent endpoint written into <c>enrollment.json</c>.</summary>
    public Uri? PublicServerUrl { get; set; }

    /// <summary>SHA-256 pin of the agent endpoint's TLS certificate; only needed for private/self-signed certificates.</summary>
    public string? ServerCertificateSha256 { get; set; }
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
