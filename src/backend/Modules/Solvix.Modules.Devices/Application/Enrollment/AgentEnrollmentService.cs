namespace Solvix.Modules.Devices.Application.Enrollment;

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts;
using Solvix.Modules.Devices.Configuration;
using Solvix.Modules.Devices.Domain;
using Solvix.Modules.Devices.Security;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public interface IAgentEnrollmentService
{
    Task<EnrollmentResponse> EnrollAsync(EnrollmentRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Turns a valid enrollment token + CSR into a device identity:
///  1. the tenant is taken from the token and the request is bound to it (RLS applies);
///  2. the token is verified (constant-time hash compare), checked for expiry,
///     revocation and remaining uses, and consumed atomically;
///  3. duplicates (same hardware fingerprint in this tenant) re-use the existing
///     record and all of its previous credentials are revoked;
///  4. the device CA signs a client certificate for the CSR's public key.
/// Everything happens in one transaction: a failure never consumes the token.
/// </summary>
internal sealed partial class AgentEnrollmentService(
    ISolvixDbContext db,
    ITenantDirectory tenants,
    ITenantContextMutator tenantMutator,
    IDeviceCertificateAuthority certificateAuthority,
    IOptions<DevicesOptions> options,
    IClock clock,
    ILogger<AgentEnrollmentService> logger) : IAgentEnrollmentService
{
    private const string InvalidTokenCode = "enrollment_token_invalid";
    private const string ExpiredTokenCode = "enrollment_token_expired";

    public async Task<EnrollmentResponse> EnrollAsync(EnrollmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!EnrollmentTokenCodec.TryParse(request.EnrollmentToken, out var tenantId, out var tokenId, out var secret))
        {
            throw Invalid("malformed token");
        }

        try
        {
            var tenant = await tenants.FindByIdAsync(tenantId, cancellationToken);
            if (tenant is not { IsActive: true })
            {
                throw Invalid("unknown or inactive tenant");
            }

            tenantMutator.SetTenant(tenantId, tenant.Slug);
            var now = clock.UtcNow;

            var token = await db.Set<EnrollmentToken>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == tokenId, cancellationToken);
            if (token is null || !token.SecretMatches(secret))
            {
                throw Invalid("unknown token or secret mismatch");
            }

            switch (token.StateAt(now))
            {
                case EnrollmentTokenState.Expired:
                    LogRejected(logger, tokenId, "expired");
                    throw new UnauthorizedException("The enrollment token has expired. Ask an administrator for a new one.", ExpiredTokenCode);
                case EnrollmentTokenState.Revoked:
                    throw Invalid("revoked");
                case EnrollmentTokenState.Exhausted:
                    throw Invalid("no uses left");
                default:
                    break;
            }

            // Validated only after the token so that anonymous callers learn nothing about CSR rules first.
            var publicKey = CertificateSigningRequests.ParsePublicKey(request.CertificateSigningRequestPem);
            var fingerprint = HardwareFingerprint.Compute(request.Device.HardwareId, request.Device.SerialNumber);
            var settings = options.Value;

            var strategy = db.Database.CreateExecutionStrategy();
            var response = await strategy.ExecuteAsync(
                async ct =>
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(ct);

                    var consumed = await db.Set<EnrollmentToken>()
                        .Where(t => t.Id == tokenId && t.RevokedAtUtc == null && t.ExpiresAtUtc > now && t.UseCount < t.MaxUses)
                        .ExecuteUpdateAsync(
                            s => s.SetProperty(t => t.UseCount, t => t.UseCount + 1).SetProperty(t => t.LastUsedAtUtc, now),
                            ct);
                    if (consumed == 0)
                    {
                        throw Invalid("consumed concurrently");
                    }

                    var device = fingerprint is null
                        ? null
                        : await db.Set<Device>().FirstOrDefaultAsync(d => d.HardwareFingerprint == fingerprint, ct);
                    var reEnrolled = device is not null;
                    if (device is null)
                    {
                        device = Device.Enroll(tenantId, DevicePlatform.Windows, request.Device, fingerprint, tokenId, now);
                        db.Set<Device>().Add(device);
                        if (token.DeviceGroupId is { } groupId)
                        {
                            db.Set<DeviceGroupMembership>().Add(DeviceGroupMembership.Create(tenantId, groupId, device.Id));
                        }
                    }
                    else
                    {
                        device.ReEnroll(request.Device, tokenId, now);
                        var previous = await db.Set<DeviceCredential>()
                            .Where(c => c.DeviceId == device.Id && c.RevokedAtUtc == null)
                            .ToListAsync(ct);
                        foreach (var credential in previous)
                        {
                            credential.Revoke(CredentialRevocationReason.ReEnrolled, now);
                        }
                    }

                    var issued = certificateAuthority.Issue(
                        publicKey, tenantId, device.Id, now.AddMinutes(-5), now.AddDays(settings.CertificateAuthority.DeviceCertificateLifetimeDays));
                    db.Set<DeviceCredential>().Add(DeviceCredential.Create(
                        tenantId, device.Id, issued.Thumbprint, issued.SerialNumber, issued.NotBeforeUtc, issued.NotAfterUtc));

                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);

                    LogEnrolled(logger, device.Id, tenantId, reEnrolled, issued.Thumbprint);
                    return new EnrollmentResponse
                    {
                        DeviceId = device.Id,
                        TenantId = tenantId,
                        CertificatePem = issued.Pem,
                        CaCertificatesPem = [certificateAuthority.CertificatePem],
                        CertificateNotAfterUtc = issued.NotAfterUtc,
                        ReEnrolled = reEnrolled,
                        Configuration = AgentConfigurationFactory.From(settings.Agent),
                    };
                },
                cancellationToken);
            return response;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }

        UnauthorizedException Invalid(string reason)
        {
            LogRejected(logger, tokenId, reason);
            return new UnauthorizedException("The enrollment token is invalid.", InvalidTokenCode);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Enrollment rejected for token {TokenId}: {Reason}")]
    private static partial void LogRejected(ILogger logger, Guid tokenId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Device {DeviceId} enrolled in tenant {TenantId} (re-enrolled: {ReEnrolled}, certificate sha256 {Thumbprint})")]
    private static partial void LogEnrolled(ILogger logger, Guid deviceId, Guid tenantId, bool reEnrolled, string thumbprint);
}

internal static class AgentConfigurationFactory
{
    public static AgentConfiguration From(AgentPolicyOptions policy) => new()
    {
        HeartbeatIntervalSeconds = policy.HeartbeatIntervalSeconds,
        InventoryIntervalMinutes = policy.InventoryIntervalMinutes,
        UpdateCheckIntervalMinutes = policy.UpdateCheckIntervalMinutes,
    };
}
