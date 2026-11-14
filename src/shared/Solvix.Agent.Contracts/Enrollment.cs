namespace Solvix.Agent.Contracts;

/// <summary>
/// First contact of a new agent. The agent proves possession of a freshly
/// generated, non-exportable private key through the PKCS#10 signing request
/// and presents a short-lived enrollment token issued by a tenant admin.
/// </summary>
public sealed record EnrollmentRequest
{
    public required string EnrollmentToken { get; init; }

    /// <summary>PEM encoded PKCS#10 certificate signing request. Its subject is ignored by the server.</summary>
    public required string CertificateSigningRequestPem { get; init; }

    public required DeviceIdentityInfo Device { get; init; }
}

/// <summary>Stable facts about the machine used for identification and duplicate detection.</summary>
public sealed record DeviceIdentityInfo
{
    /// <summary>SMBIOS system UUID (Win32_ComputerSystemProduct.UUID).</summary>
    public string? HardwareId { get; init; }

    public string? SerialNumber { get; init; }

    public required string Hostname { get; init; }

    public string? DomainName { get; init; }

    public string? Manufacturer { get; init; }

    public string? Model { get; init; }

    public string? OsName { get; init; }

    public string? OsVersion { get; init; }

    public string? OsBuild { get; init; }

    public string? Architecture { get; init; }

    public required string AgentVersion { get; init; }
}

public sealed record EnrollmentResponse
{
    public required Guid DeviceId { get; init; }

    public required Guid TenantId { get; init; }

    /// <summary>PEM encoded device certificate (public part only).</summary>
    public required string CertificatePem { get; init; }

    /// <summary>PEM encoded issuing CA certificate(s).</summary>
    public required IReadOnlyList<string> CaCertificatesPem { get; init; }

    public required DateTimeOffset CertificateNotAfterUtc { get; init; }

    /// <summary><c>true</c> when an existing device record was re-bound to new credentials.</summary>
    public bool ReEnrolled { get; init; }

    public required AgentConfiguration Configuration { get; init; }
}

public sealed record CertificateRenewalRequest
{
    public required string CertificateSigningRequestPem { get; init; }
}

public sealed record CertificateRenewalResponse
{
    public required string CertificatePem { get; init; }

    public required IReadOnlyList<string> CaCertificatesPem { get; init; }

    public required DateTimeOffset CertificateNotAfterUtc { get; init; }
}

/// <summary>
/// Declarative agent settings delivered by the server. The agent clamps every
/// value to safe local bounds; these are schedules, never instructions.
/// </summary>
public sealed record AgentConfiguration
{
    public int HeartbeatIntervalSeconds { get; init; } = 60;

    public int InventoryIntervalMinutes { get; init; } = 240;

    public int UpdateCheckIntervalMinutes { get; init; } = 360;
}
