namespace Solvix.Modules.Devices.Application.Enrollment;

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts;
using Solvix.Modules.Devices.Configuration;
using Solvix.Modules.Devices.Domain;
using Solvix.Modules.Devices.Monitoring;
using Solvix.Modules.Devices.Security;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

/// <summary>Agent-facing, anonymous half of the approval-based enrollment.</summary>
public interface IPendingEnrollmentIntake
{
    /// <summary>Records a request made with a deployment profile code. No certificate is issued.</summary>
    Task<EnrollmentPendingResponse> SubmitAsync(EnrollmentRequest request, string? sourceAddress, CancellationToken cancellationToken);

    /// <summary>The waiting agent asks whether an administrator has decided.</summary>
    Task<EnrollmentPollResponse> PollAsync(EnrollmentPollRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// A generic installer carries a deployment profile code, which is not a secret. So nothing is issued on
/// the strength of it: the request (identity facts + the public key the device generated) is parked until an
/// administrator approves it. The code can be rotated or the profile disabled at any time, the queue is capped
/// per tenant, requests expire, and polling needs the one-time poll token only the requesting agent holds.
/// </summary>
internal sealed partial class PendingEnrollmentIntakeService(
    ISolvixDbContext db,
    ITenantDirectory tenants,
    ITenantContextMutator tenantMutator,
    IDeviceCertificateAuthority certificateAuthority,
    MonitoringSettingsProvider monitoringSettings,
    IOptions<DevicesOptions> options,
    IClock clock,
    ILogger<PendingEnrollmentIntakeService> logger) : IPendingEnrollmentIntake
{
    public const string InvalidCodeCode = "enrollment_code_invalid";
    public const string QueueFullCode = "enrollment_queue_full";
    private static readonly TimeSpan HousekeepingAge = TimeSpan.FromDays(30);

    public async Task<EnrollmentPendingResponse> SubmitAsync(EnrollmentRequest request, string? sourceAddress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TenantScopedToken.TryParse(DeploymentTokens.ProfilePrefix, request.EnrollmentToken, out var tenantId, out var profileId, out var secret))
        {
            throw Invalid(Guid.Empty, "malformed code");
        }

        try
        {
            var tenant = await tenants.FindByIdAsync(tenantId, cancellationToken);
            if (tenant is not { IsActive: true })
            {
                throw Invalid(profileId, "unknown or inactive tenant");
            }

            tenantMutator.SetTenant(tenantId, tenant.Slug);
            var profile = await db.Set<DeploymentProfile>().FirstOrDefaultAsync(p => p.Id == profileId, cancellationToken);
            if (profile is null || !profile.CodeMatches(secret))
            {
                throw Invalid(profileId, "unknown profile or code mismatch");
            }

            if (!profile.IsEnabled)
            {
                throw Invalid(profileId, "profile disabled");
            }

            // CSR rules are only revealed to callers that hold a valid code.
            var publicKey = CertificateSigningRequests.ParsePublicKey(request.CertificateSigningRequestPem);
            var fingerprint = HardwareFingerprint.Compute(request.Device.HardwareId, request.Device.SerialNumber);
            var settings = options.Value.Enrollment;
            var now = clock.UtcNow;

            // A reinstall of the same computer replaces its earlier open request instead of piling up.
            if (fingerprint is not null)
            {
                await db.Set<PendingEnrollment>()
                    .Where(p => p.Status == PendingEnrollmentStatus.Pending && p.HardwareFingerprint == fingerprint)
                    .ExecuteDeleteAsync(cancellationToken);
            }

            await db.Set<PendingEnrollment>()
                .Where(p => p.CreatedAtUtc < now - HousekeepingAge && (p.Status != PendingEnrollmentStatus.Pending || p.ExpiresAtUtc < now))
                .ExecuteDeleteAsync(cancellationToken);

            var open = await db.Set<PendingEnrollment>().CountAsync(p => p.Status == PendingEnrollmentStatus.Pending && p.ExpiresAtUtc > now, cancellationToken);
            if (open >= settings.MaxPendingPerTenant)
            {
                LogQueueFull(logger, tenantId, open);
                throw new ConflictException("Too many computers are waiting for approval. Ask an administrator to review the pending list.", QueueFullCode);
            }

            Guid? existing = fingerprint is null
                ? null
                : await db.Set<Device>().AsNoTracking().Where(d => d.HardwareFingerprint == fingerprint).Select(d => (Guid?)d.Id).FirstOrDefaultAsync(cancellationToken);

            var pollSecret = RandomNumberGenerator.GetBytes(TenantScopedToken.SecretLength);
            try
            {
                var pending = PendingEnrollment.Create(
                    tenantId,
                    profile.Id,
                    request.Device.Hostname,
                    JsonSerializer.Serialize(request.Device, DevicesJson.Options),
                    fingerprint,
                    publicKey.ExportSubjectPublicKeyInfo(),
                    pollSecret,
                    sourceAddress,
                    existing,
                    now,
                    TimeSpan.FromDays(settings.PendingLifetimeDays));
                db.Set<PendingEnrollment>().Add(pending);
                profile.RecordRequest(now);
                await db.SaveChangesAsync(cancellationToken);
                LogSubmitted(logger, pending.Id, tenantId, profile.Id, pending.Hostname);

                var (pollToken, _) = TenantScopedToken.Create(DeploymentTokens.PollPrefix, tenantId, pending.Id, pollSecret);
                return new EnrollmentPendingResponse
                {
                    PendingEnrollmentId = pending.Id,
                    PollToken = pollToken,
                    PollIntervalSeconds = settings.PollIntervalSeconds,
                    ExpiresAtUtc = pending.ExpiresAtUtc,
                };
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pollSecret);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public async Task<EnrollmentPollResponse> PollAsync(EnrollmentPollRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TenantScopedToken.TryParse(DeploymentTokens.PollPrefix, request.PollToken, out var tenantId, out var pendingId, out var secret))
        {
            throw Invalid(Guid.Empty, "malformed poll token");
        }

        try
        {
            var tenant = await tenants.FindByIdAsync(tenantId, cancellationToken);
            if (tenant is not { IsActive: true })
            {
                throw Invalid(pendingId, "unknown or inactive tenant");
            }

            tenantMutator.SetTenant(tenantId, tenant.Slug);
            var pending = await db.Set<PendingEnrollment>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == pendingId, cancellationToken);
            if (pending is null || !pending.PollSecretMatches(secret))
            {
                throw Invalid(pendingId, "unknown request or poll token mismatch");
            }

            var now = clock.UtcNow;
            var interval = options.Value.Enrollment.PollIntervalSeconds;
            switch (pending.Status)
            {
                case PendingEnrollmentStatus.Rejected:
                    return new EnrollmentPollResponse { Status = EnrollmentPollStatus.Rejected, PollIntervalSeconds = interval, Message = pending.DecisionNote ?? "An administrator rejected this computer." };
                case PendingEnrollmentStatus.Approved when pending.CanRedeliverAt(now):
                    return new EnrollmentPollResponse
                    {
                        Status = EnrollmentPollStatus.Approved,
                        PollIntervalSeconds = interval,
                        Enrollment = await BuildEnrollmentAsync(pending, cancellationToken),
                    };
                case PendingEnrollmentStatus.Approved:
                    return new EnrollmentPollResponse { Status = EnrollmentPollStatus.Expired, PollIntervalSeconds = interval, Message = "The approval was not collected in time. Enroll again." };
                default:
                    return pending.IsExpiredAt(now)
                        ? new EnrollmentPollResponse { Status = EnrollmentPollStatus.Expired, PollIntervalSeconds = interval, Message = "The request was not approved in time. Enroll again." }
                        : new EnrollmentPollResponse { Status = EnrollmentPollStatus.Pending, PollIntervalSeconds = interval };
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private async Task<EnrollmentResponse> BuildEnrollmentAsync(PendingEnrollment pending, CancellationToken cancellationToken)
    {
        var monitoring = await monitoringSettings.GetAsync(cancellationToken);
        using var certificate = X509Certificate2.CreateFromPem(pending.CertificatePem!);
        return new EnrollmentResponse
        {
            DeviceId = pending.DeviceId ?? throw new InvalidOperationException("An approved request has no device."),
            TenantId = pending.TenantId,
            CertificatePem = pending.CertificatePem!,
            CaCertificatesPem = [certificateAuthority.CertificatePem],
            CertificateNotAfterUtc = new DateTimeOffset(certificate.NotAfter.ToUniversalTime()),
            ReEnrolled = pending.ExistingDeviceId is not null,
            Configuration = AgentConfigurationFactory.From(options.Value.Agent, monitoring.Thresholds.HeartbeatIntervalSeconds),
        };
    }

    private UnauthorizedException Invalid(Guid id, string reason)
    {
        LogRejected(logger, id, reason);
        return new UnauthorizedException("The enrollment code is invalid or no longer accepted.", InvalidCodeCode);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Approval-based enrollment rejected for {Id}: {Reason}")]
    private static partial void LogRejected(ILogger logger, Guid id, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Enrollment request {RequestId} from '{Hostname}' queued for approval (tenant {TenantId}, profile {ProfileId})")]
    private static partial void LogSubmitted(ILogger logger, Guid requestId, Guid tenantId, Guid profileId, string hostname);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Approval queue of tenant {TenantId} is full ({Open} open requests)")]
    private static partial void LogQueueFull(ILogger logger, Guid tenantId, int open);
}
