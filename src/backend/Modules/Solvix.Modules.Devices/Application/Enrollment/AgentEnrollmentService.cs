namespace Solvix.Modules.Devices.Application.Enrollment;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvix.Endpoint.Contracts.Agent;
using Solvix.Modules.Devices.Application.Credentials;
using Solvix.Modules.Devices.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Auditing;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public interface IAgentEnrollmentService
{
    Task<EnrollDeviceResponse> EnrollAsync(EnrollDeviceRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Anonymous enrolment. The enrolment token names its own row, which is read in
/// an explicit platform scope (the only point where the tenant is not yet
/// known); everything after the token is verified runs in a fresh scope bound to
/// the token's tenant, so the device is created under the normal isolation rules.
/// </summary>
internal sealed class AgentEnrollmentService(
    ISolvixDbContext db,
    ITenantContextMutator tenantContextMutator,
    ITenantScopeFactory tenantScopes,
    ITenantDirectory tenants,
    IClock clock) : IAgentEnrollmentService
{
    public const int DefaultPollIntervalSeconds = 60;

    public async Task<EnrollDeviceResponse> EnrollAsync(EnrollDeviceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = clock.UtcNow;
        if (!Secrets.TryParse(request.EnrollmentToken, AgentAuth.EnrollmentTokenPrefix, out var tokenId))
        {
            throw new UnauthorizedException("The enrolment token is invalid.");
        }

        EnrollmentToken? token;
        using (tenantContextMutator.BeginPlatformScope())
        {
            token = await db.Set<EnrollmentToken>().IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == tokenId, cancellationToken);
        }

        // One generic message for every failure so a token cannot be probed.
        if (token is null || !Secrets.Verify(request.EnrollmentToken, token.SecretHash) || !token.IsUsable(now))
        {
            throw new UnauthorizedException("The enrolment token is invalid, expired or exhausted.");
        }

        var tenant = await tenants.FindByIdAsync(token.TenantId, cancellationToken);
        if (tenant is not { IsActive: true })
        {
            throw new UnauthorizedException("The enrolment token is invalid, expired or exhausted.");
        }

        await using var scope = tenantScopes.CreateScope(token.TenantId, tenant.Slug);
        var tenantDb = scope.ServiceProvider.GetRequiredService<ISolvixDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditTrail>();

        // Atomic use-count increment: concurrent enrolments cannot exceed MaxUses.
        var claimed = await tenantDb.Set<EnrollmentToken>()
            .Where(t => t.Id == tokenId && t.RevokedAtUtc == null && t.ExpiresAtUtc > now && (t.MaxUses == null || t.UseCount < t.MaxUses))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UseCount, t => t.UseCount + 1), cancellationToken);
        if (claimed == 0)
        {
            throw new UnauthorizedException("The enrolment token is invalid, expired or exhausted.");
        }

        Device? device = null;
        var hardwareId = string.IsNullOrWhiteSpace(request.HardwareId) ? null : request.HardwareId.Trim();
        if (hardwareId is not null)
        {
            device = await tenantDb.Set<Device>().FirstOrDefaultAsync(d => d.HardwareId == hardwareId, cancellationToken);
        }

        var isNew = device is null;
        if (device is null)
        {
            device = Device.Enroll(token.TenantId, request.Hostname, hardwareId, now);
            device.PlaceIn(token.DefaultBranchId, token.DefaultDepartmentId);
            tenantDb.Set<Device>().Add(device);
            if (token.DefaultDeviceGroupId is { } groupId)
            {
                tenantDb.Set<DeviceGroupMember>().Add(new DeviceGroupMember(token.TenantId, groupId, device.Id));
            }
        }
        else
        {
            device.Reactivate(now);
        }

        device.ReportInventory(request.Hostname, request.DnsDomain, request.OsName, request.OsVersion, request.OsBuild, request.AgentVersion);
        var credential = Secrets.NewDeviceCredential(device.Id);
        device.IssueCredential(Secrets.Hash(credential), now);

        audit.Record(
            isNew ? "device.enrolled" : "device.reenrolled",
            nameof(Device),
            device.Id,
            isNew ? $"Device '{device.Hostname}' enrolled with token '{token.Name}'." : $"Device '{device.Hostname}' re-enrolled (same hardware id); previous credential revoked.",
            new { enrollmentTokenId = token.Id, request.AgentVersion, request.OsName, request.OsVersion, request.OsBuild });
        await tenantDb.SaveChangesAsync(cancellationToken);
        await scope.ServiceProvider.GetRequiredService<IDeviceCredentialCache>().InvalidateAsync(device.Id, cancellationToken);

        return new EnrollDeviceResponse(device.Id, token.TenantId, credential, DefaultPollIntervalSeconds);
    }
}
