namespace Solvix.Modules.Devices.Domain;

using System.Security.Cryptography;
using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Exceptions;

public enum PendingEnrollmentStatus
{
    Pending = 1,
    Approved = 2,
    Rejected = 3,
}

/// <summary>
/// A computer that installed a generic installer and is waiting for an administrator. It holds the public key
/// the device generated (the private key never leaves it); no certificate exists until the request is approved.
/// </summary>
public sealed class PendingEnrollment : TenantEntity
{
    public const int HostnameMaxLength = 255;
    public const int NoteMaxLength = 500;
    public const int AddressMaxLength = 64;
    public const int PollRedeliveryHours = 24;

    private PendingEnrollment()
    {
    }

    private PendingEnrollment(
        Guid tenantId,
        Guid profileId,
        string hostname,
        string identityJson,
        string? fingerprint,
        byte[] publicKeySpki,
        byte[] pollSecretHash,
        string? sourceAddress,
        Guid? existingDeviceId,
        DateTimeOffset expiresAtUtc)
        : base(tenantId)
    {
        ProfileId = profileId;
        Hostname = hostname;
        IdentityJson = identityJson;
        HardwareFingerprint = fingerprint;
        PublicKeySpki = publicKeySpki;
        PollSecretHash = pollSecretHash;
        SourceAddress = sourceAddress;
        ExistingDeviceId = existingDeviceId;
        ExpiresAtUtc = expiresAtUtc;
        Status = PendingEnrollmentStatus.Pending;
    }

    public Guid ProfileId { get; private set; }

    public PendingEnrollmentStatus Status { get; private set; }

    public string Hostname { get; private set; } = string.Empty;

    /// <summary><c>DeviceIdentityInfo</c> as submitted by the agent (JSON).</summary>
    public string IdentityJson { get; private set; } = "{}";

    public string? HardwareFingerprint { get; private set; }

    public byte[] PublicKeySpki { get; private set; } = [];

    public byte[] PollSecretHash { get; private set; } = [];

    public string? SourceAddress { get; private set; }

    /// <summary>An existing device with the same hardware: approving re-enrolls it and revokes its old credentials.</summary>
    public Guid? ExistingDeviceId { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public Guid? DecidedByUserId { get; private set; }

    public DateTimeOffset? DecidedAtUtc { get; private set; }

    public string? DecisionNote { get; private set; }

    /// <summary>The device created / re-bound by the approval.</summary>
    public Guid? DeviceId { get; private set; }

    /// <summary>Public certificate, kept so a lost response can be fetched again (see <see cref="PollRedeliveryHours"/>).</summary>
    public string? CertificatePem { get; private set; }

    public static PendingEnrollment Create(
        Guid tenantId,
        Guid profileId,
        string hostname,
        string identityJson,
        string? fingerprint,
        byte[] publicKeySpki,
        ReadOnlySpan<byte> pollSecret,
        string? sourceAddress,
        Guid? existingDeviceId,
        DateTimeOffset now,
        TimeSpan lifetime)
    {
        var host = string.IsNullOrWhiteSpace(hostname) ? "unknown" : hostname.Trim();
        return new PendingEnrollment(
            tenantId,
            profileId,
            host.Length <= HostnameMaxLength ? host : host[..HostnameMaxLength],
            identityJson,
            fingerprint,
            publicKeySpki,
            SHA256.HashData(pollSecret),
            sourceAddress is { Length: > AddressMaxLength } ? sourceAddress[..AddressMaxLength] : sourceAddress,
            existingDeviceId,
            now + lifetime);
    }

    public bool PollSecretMatches(ReadOnlySpan<byte> secret) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(secret), PollSecretHash);

    public bool IsExpiredAt(DateTimeOffset now) => Status == PendingEnrollmentStatus.Pending && now >= ExpiresAtUtc;

    /// <summary>Whether the certificate may still be handed out again to the polling agent.</summary>
    public bool CanRedeliverAt(DateTimeOffset now) =>
        Status == PendingEnrollmentStatus.Approved
        && CertificatePem is not null
        && DecidedAtUtc is { } decided
        && now < decided.AddHours(PollRedeliveryHours);

    public void Approve(Guid userId, Guid deviceId, string certificatePem, string? note, DateTimeOffset now)
    {
        EnsurePending(now);
        Status = PendingEnrollmentStatus.Approved;
        DecidedByUserId = userId;
        DecidedAtUtc = now;
        DecisionNote = Truncate(note);
        DeviceId = deviceId;
        CertificatePem = certificatePem;
    }

    public void Reject(Guid userId, string? note, DateTimeOffset now)
    {
        EnsurePending(now);
        Status = PendingEnrollmentStatus.Rejected;
        DecidedByUserId = userId;
        DecidedAtUtc = now;
        DecisionNote = Truncate(note);
    }

    private void EnsurePending(DateTimeOffset now)
    {
        if (Status != PendingEnrollmentStatus.Pending)
        {
            throw new ConflictException($"This enrollment request has already been {Status.ToString().ToLowerInvariant()}.", "enrollment_request_decided");
        }

        if (now >= ExpiresAtUtc)
        {
            throw new ConflictException("This enrollment request has expired. The computer has to request enrollment again.", "enrollment_request_expired");
        }
    }

    private static string? Truncate(string? note)
    {
        var trimmed = note?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length <= NoteMaxLength ? trimmed : trimmed[..NoteMaxLength];
    }
}
