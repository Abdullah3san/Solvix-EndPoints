namespace Solvix.Modules.Security.Domain;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Guards;

/// <summary>
/// A Windows device enrolled for security posture reporting. Phase 10 owns this registry because no earlier phase
/// provides a device inventory; it authenticates its agent with a per-device key whose SHA-256 hash is stored here.
/// The key itself is shown once at enrolment/rotation and never stored.
/// </summary>
public sealed class ManagedEndpoint : TenantEntity
{
    public const int HostnameMaxLength = 200;
    public const int TextMaxLength = 200;
    public const int KeyHashLength = 32;

    private ManagedEndpoint()
    {
    }

    private ManagedEndpoint(Guid tenantId, string hostname, byte[] keyHash, Guid enrolledByUserId, DateTimeOffset now)
        : base(tenantId)
    {
        Hostname = hostname;
        KeyHash = keyHash;
        KeyIssuedAtUtc = now;
        EnrolledByUserId = enrolledByUserId;
        Status = EndpointStatus.Active;
        Band = ComplianceBand.Unknown;
    }

    public string Hostname { get; private set; } = string.Empty;

    public EndpointStatus Status { get; private set; }

    /// <summary>SHA-256 of the device key secret. Never serialised to any API response.</summary>
    public byte[] KeyHash { get; private set; } = [];

    public DateTimeOffset KeyIssuedAtUtc { get; private set; }

    public Guid EnrolledByUserId { get; private set; }

    public string? OsCaption { get; private set; }

    public string? OsVersion { get; private set; }

    public string? AgentVersion { get; private set; }

    public bool? IsDomainJoined { get; private set; }

    public DateTimeOffset? LastReportAtUtc { get; private set; }

    public Guid? LatestSnapshotId { get; private set; }

    public int? Score { get; private set; }

    public ComplianceBand Band { get; private set; }

    public double? Coverage { get; private set; }

    public int OpenCritical { get; private set; }

    public int OpenHigh { get; private set; }

    public int OpenMedium { get; private set; }

    public int OpenLow { get; private set; }

    public static ManagedEndpoint Enroll(Guid tenantId, string hostname, byte[] keyHash, Guid enrolledByUserId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(keyHash);
        if (keyHash.Length != KeyHashLength)
        {
            throw new ArgumentException("Key hash must be a SHA-256 digest.", nameof(keyHash));
        }

        return new ManagedEndpoint(tenantId, NormalizeHostname(hostname), keyHash, enrolledByUserId, now);
    }

    public static string NormalizeHostname(string hostname) =>
        Guard.NotNullOrWhiteSpace(hostname, "hostname", HostnameMaxLength);

    public void Rename(string hostname) => Hostname = NormalizeHostname(hostname);

    public void RotateKey(byte[] keyHash, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(keyHash);
        if (Status == EndpointStatus.Revoked)
        {
            throw new ConflictException("A revoked device cannot be given a new key. Enrol it again.");
        }

        KeyHash = keyHash;
        KeyIssuedAtUtc = now;
    }

    public void Revoke() => Status = EndpointStatus.Revoked;

    public void RecordReport(PostureSnapshot snapshot, string? hostname, string? osCaption, string? osVersion, string? agentVersion, bool? domainJoined)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        LatestSnapshotId = snapshot.Id;
        LastReportAtUtc = snapshot.ReceivedAtUtc;
        if (!string.IsNullOrWhiteSpace(hostname))
        {
            Hostname = Guard.NotNullOrWhiteSpace(hostname, "hostname", HostnameMaxLength);
        }

        OsCaption = Guard.Optional(osCaption, "osCaption", TextMaxLength);
        OsVersion = Guard.Optional(osVersion, "osVersion", TextMaxLength);
        AgentVersion = Guard.Optional(agentVersion, "agentVersion", 64);
        IsDomainJoined = domainJoined;
    }

    public void ApplyAssessment(int? score, ComplianceBand band, double? coverage, int critical, int high, int medium, int low)
    {
        Score = score;
        Band = band;
        Coverage = coverage;
        OpenCritical = critical;
        OpenHigh = high;
        OpenMedium = medium;
        OpenLow = low;
    }

    public bool IsStale(DateTimeOffset now, int staleAfterDays) =>
        LastReportAtUtc is not { } last || now - last > TimeSpan.FromDays(staleAfterDays);
}
