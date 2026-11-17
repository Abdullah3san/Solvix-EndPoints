namespace Solvix.Modules.Devices.Application.Enrollment;

using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts;
using Solvix.Modules.Devices.Application.Integration;
using Solvix.Modules.Devices.Configuration;
using Solvix.Modules.Devices.Domain;
using Solvix.Modules.Devices.Monitoring;
using Solvix.Modules.Devices.Security;
using Solvix.SharedKernel.Billing;
using Solvix.SharedKernel.Endpoints;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Telemetry;

/// <summary>What a new or returning device is enrolled with.</summary>
/// <param name="SourceId">The enrollment token or deployment profile that authorised it (kept on the device record).</param>
internal sealed record DeviceRegistration(
    Guid TenantId,
    PublicKey PublicKey,
    DeviceIdentityInfo Identity,
    string? Fingerprint,
    Guid SourceId,
    Guid? BranchId,
    Guid? DepartmentId,
    Guid? GroupId);

internal sealed record RegisteredDevice(Device Device, IssuedDeviceCertificate Certificate, bool ReEnrolled);

/// <summary>
/// The one place that turns an authorised enrollment into a device identity: create the device (or re-bind a
/// known machine), apply its placement, enforce the subscription limits and have the device CA sign its
/// certificate. Both enrollment with a one-time token and administrator approval of a pending enrollment end
/// here, so they behave identically.
/// </summary>
internal sealed partial class DeviceRegistrar(
    ISolvixDbContext db,
    IDeviceCertificateAuthority certificateAuthority,
    PresenceTracker presence,
    PresenceBuffer presenceBuffer,
    EndpointIdentityPublisher identityPublisher,
    IOptions<DevicesOptions> options,
    IDeviceEntitlementProvider entitlements,
    IDeviceUsageReader deviceUsage,
    ILogger<DeviceRegistrar> logger)
{
    /// <summary>Runs inside the caller's transaction; the caller saves and commits.</summary>
    public async Task<RegisteredDevice> RegisterAsync(DeviceRegistration registration, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var tenantId = registration.TenantId;
        var fingerprint = registration.Fingerprint;

        var device = fingerprint is null
            ? null
            : await db.Set<Device>().FirstOrDefaultAsync(d => d.HardwareFingerprint == fingerprint, cancellationToken);
        var reEnrolled = device is not null;
        if (device is null)
        {
            // Plan limits are enforced here, on the server, inside the enrollment transaction.
            await EnforceSubscriptionAsync(tenantId, cancellationToken);
            device = Device.Enroll(tenantId, DevicePlatform.Windows, registration.Identity, fingerprint, registration.SourceId, now);
            device.AssignIfUnassigned(registration.BranchId, registration.DepartmentId);
            db.Set<Device>().Add(device);
            presence.Enroll(device, now);
            if (registration.GroupId is { } groupId)
            {
                db.Set<DeviceGroupMembership>().Add(DeviceGroupMembership.Create(tenantId, groupId, device.Id));
            }
        }
        else
        {
            device.ReEnroll(registration.Identity, registration.SourceId, now);

            // Re-enrolled hardware keeps its record and any existing assignment; the placement
            // only fills it in when the device has none.
            device.AssignIfUnassigned(registration.BranchId, registration.DepartmentId);
            var previous = await db.Set<DeviceCredential>()
                .Where(c => c.DeviceId == device.Id && c.RevokedAtUtc == null)
                .ToListAsync(cancellationToken);
            foreach (var credential in previous)
            {
                credential.Revoke(CredentialRevocationReason.ReEnrolled, now);
            }
        }

        var issued = certificateAuthority.Issue(
            registration.PublicKey,
            tenantId,
            device.Id,
            now.AddMinutes(-5),
            now.AddDays(options.Value.CertificateAuthority.DeviceCertificateLifetimeDays));
        db.Set<DeviceCredential>().Add(DeviceCredential.Create(
            tenantId, device.Id, issued.Thumbprint, issued.SerialNumber, issued.NotBeforeUtc, issued.NotAfterUtc));
        return new RegisteredDevice(device, issued, reEnrolled);
    }

    /// <summary>After the transaction committed: presence bookkeeping and the asset-identity hook (never fail enrollment).</summary>
    public async Task CompletedAsync(RegisteredDevice registered, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registered);
        var device = registered.Device;
        if (registered.ReEnrolled)
        {
            presenceBuffer.Record(device.TenantId, device.Id, now, successful: true, heartbeat: false);
        }

        LogEnrolled(logger, device.Id, device.TenantId, registered.ReEnrolled, registered.Certificate.Thumbprint);

        // Committed: let asset management relate the hardware to a physical asset.
        await identityPublisher.PublishAsync(
            device.Id,
            registered.ReEnrolled ? EndpointIdentityChangeKind.ReEnrolled : EndpointIdentityChangeKind.Enrolled,
            cancellationToken);
    }

    /// <summary>
    /// A NEW device needs an entitled subscription and a free slot. A per-tenant advisory lock, held until the
    /// enrollment transaction ends, serialises concurrent enrollments so two agents cannot both take the last slot.
    /// Re-enrolling a device that already exists never consumes a slot.
    /// </summary>
    private async Task EnforceSubscriptionAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var lockKey = $"solvix:device-quota:{tenantId:N}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", cancellationToken);

        var entitlement = await entitlements.GetAsync(tenantId, cancellationToken);
        if (!entitlement.IsEntitled)
        {
            SolvixMetrics.DeviceEnrollmentsDenied.Add(1, new KeyValuePair<string, object?>("reason", entitlement.DenyCode));
            throw new PaymentRequiredException(entitlement.DenyMessage ?? "The subscription does not allow enrolling devices.", entitlement.DenyCode ?? "subscription_inactive");
        }

        var used = await deviceUsage.CountBillableDevicesAsync(cancellationToken);
        if (used >= entitlement.DeviceLimit)
        {
            SolvixMetrics.DeviceEnrollmentsDenied.Add(1, new KeyValuePair<string, object?>("reason", "device_limit_reached"));
            throw new PaymentRequiredException(
                $"The device limit of the current plan has been reached ({used} of {entitlement.DeviceLimit}). Upgrade the plan or retire unused devices.",
                "device_limit_reached");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Device {DeviceId} enrolled in tenant {TenantId} (re-enrolled: {ReEnrolled}, certificate sha256 {Thumbprint})")]
    private static partial void LogEnrolled(ILogger logger, Guid deviceId, Guid tenantId, bool reEnrolled, string thumbprint);
}
