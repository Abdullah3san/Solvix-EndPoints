namespace Solvix.Modules.Devices.Application.Enrollment;

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts;
using Solvix.Modules.Devices.Application.Integration;
using Solvix.Modules.Devices.Configuration;
using Solvix.Modules.Devices.Domain;
using Solvix.Modules.Devices.Monitoring;
using Solvix.Modules.Devices.Security;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Endpoints;
using Solvix.SharedKernel.Billing;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Telemetry;
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
    DeviceRegistrar registrar,
    IDeviceCertificateAuthority certificateAuthority,
    MonitoringSettingsProvider monitoringSettings,
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

            var monitoring = await monitoringSettings.GetAsync(cancellationToken);
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

                    var registered = await registrar.RegisterAsync(
                        new DeviceRegistration(tenantId, publicKey, request.Device, fingerprint, tokenId, token.BranchId, token.DepartmentId, token.DeviceGroupId),
                        now,
                        ct);
                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    await registrar.CompletedAsync(registered, now, ct);
                    return new EnrollmentResponse
                    {
                        DeviceId = registered.Device.Id,
                        TenantId = tenantId,
                        CertificatePem = registered.Certificate.Pem,
                        CaCertificatesPem = [certificateAuthority.CertificatePem],
                        CertificateNotAfterUtc = registered.Certificate.NotAfterUtc,
                        ReEnrolled = registered.ReEnrolled,
                        Configuration = AgentConfigurationFactory.From(settings.Agent, monitoring.Thresholds.HeartbeatIntervalSeconds),
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
}

internal static class AgentConfigurationFactory
{
    public static AgentConfiguration From(AgentPolicyOptions policy, int? heartbeatIntervalSeconds = null) => new()
    {
        HeartbeatIntervalSeconds = heartbeatIntervalSeconds ?? policy.HeartbeatIntervalSeconds,
        InventoryIntervalMinutes = policy.InventoryIntervalMinutes,
        UpdateCheckIntervalMinutes = policy.UpdateCheckIntervalMinutes,
    };
}
