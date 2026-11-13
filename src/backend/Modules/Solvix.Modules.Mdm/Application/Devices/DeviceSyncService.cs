namespace Solvix.Modules.Mdm.Application.Devices;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Solvix.Modules.Mdm.Application.Commands;
using Solvix.Modules.Mdm.Application.Compliance;
using Solvix.Modules.Mdm.Application.Enterprise;
using Solvix.Modules.Mdm.Domain;
using Solvix.Modules.Mdm.Providers;
using Solvix.Modules.Mdm.Providers.Android;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public sealed record SyncResultDto(int Discovered, int Updated, int Removed, int InventoryRequested, int CommandsReconciled, int CommandsExpired, int DevicesEvaluated);

public interface IDeviceSyncService
{
    /// <summary>
    /// Brings the tenant's inventory up to date: pulls Android devices from the Android Management API,
    /// asks Apple devices to report, reconciles command results, expires stale commands and re-evaluates compliance.
    /// </summary>
    Task<SyncResultDto> SyncAsync(CancellationToken cancellationToken);
}

internal sealed class DeviceSyncService(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    IClock clock,
    IAndroidManagementApi android,
    IMdmProviderRegistry providers,
    IEnterpriseDirectory enterprises,
    IDeviceCommandQueue commands,
    IComplianceService compliance,
    ILogger<DeviceSyncService> logger) : IDeviceSyncService
{
    public async Task<SyncResultDto> SyncAsync(CancellationToken cancellationToken)
    {
        int discovered = 0, updated = 0, removed = 0, requested = 0;
        var enterpriseName = await enterprises.GetEnterpriseNameAsync(MobilePlatform.Android, cancellationToken);
        if (enterpriseName is not null && providers.IsConfigured(MobilePlatform.Android))
        {
            (discovered, updated, removed) = await SyncAndroidAsync(enterpriseName, cancellationToken);
        }

        requested = await RequestAppleInventoryAsync(cancellationToken);
        await commands.RepushStaleAppleCommandsAsync(cancellationToken);
        var reconciled = await commands.ReconcileAndroidOperationsAsync(cancellationToken);
        var expired = await commands.ExpireAsync(cancellationToken);
        var evaluated = await compliance.EvaluateAllAsync(cancellationToken);
        return new SyncResultDto(discovered, updated, removed, requested, reconciled, expired, evaluated);
    }

    private async Task<(int Discovered, int Updated, int Removed)> SyncAndroidAsync(string enterpriseName, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var remote = await android.ListDevicesAsync(enterpriseName, cancellationToken);
        var known = await db.Set<MobileDevice>().Where(d => d.Platform == MobilePlatform.Android).ToDictionaryAsync(d => d.ProviderDeviceId, cancellationToken);
        var policies = await db.Set<MobilePolicy>().Where(p => p.Platform == MobilePlatform.Android).ToDictionaryAsync(p => p.Id, cancellationToken);
        int discovered = 0, updated = 0, removed = 0;

        foreach (var info in remote)
        {
            known.TryGetValue(info.Name, out var device);

            if (string.Equals(info.State, "DELETED", StringComparison.Ordinal))
            {
                if (device is { IsActive: true })
                {
                    // Deleted on Google's side (retired or wiped elsewhere): mirror it.
                    DeviceLifecycle.CompleteRemoval(db, device, DeviceCommandType.Retire, now, null, null);
                    removed++;
                }

                continue;
            }

            if (device is null)
            {
                device = await EnrollDiscoveredAsync(info, policies, now, cancellationToken);
                if (device is null)
                {
                    continue;
                }

                discovered++;
            }
            else
            {
                updated++;
            }

            if (!device.IsActive)
            {
                continue;
            }

            device.ApplyInventory(AndroidInventoryMapper.ToInventory(info), now);
            if (device.PolicyId is { } policyId && policies.TryGetValue(policyId, out var policy)
                && info.AppliedPolicyName == AndroidPolicyTranslator.PolicyName(enterpriseName, policyId)
                && policy.PublishedAtUtc is { } publishedAt && info.LastPolicySyncTime >= publishedAt)
            {
                device.MarkPolicyApplied(policy.PublishedVersion);
            }
        }

        var enterprise = await db.Set<MobileEnterprise>().FirstOrDefaultAsync(e => e.Platform == MobilePlatform.Android, cancellationToken);
        enterprise?.MarkSynced(now);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Android sync for {Tenant}: {Discovered} new, {Updated} updated, {Removed} removed", tenantContext.TenantId, discovered, updated, removed);
        return (discovered, updated, removed);
    }

    private async Task<MobileDevice?> EnrollDiscoveredAsync(
        AndroidDeviceInfo info,
        Dictionary<Guid, MobilePolicy> policies,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        EnrollmentToken? token = null;
        if (Guid.TryParseExact(info.EnrollmentTokenData, "N", out var tokenId))
        {
            token = await db.Set<EnrollmentToken>().FirstOrDefaultAsync(t => t.Id == tokenId, cancellationToken);
        }

        var scenario = token?.Scenario ?? AndroidInventoryMapper.InferScenario(info);

        // A device that enrolled with a token must match what the token promised (ownership included).
        if (token is not null && token.Ownership != AndroidInventoryMapper.ToOwnership(info.Ownership) && info.Ownership is not null)
        {
            logger.LogWarning("Device {Device} enrolled with a token for {Expected} but reports {Actual}; using the reported ownership", info.Name, token.Ownership, info.Ownership);
            scenario = scenario with { Ownership = AndroidInventoryMapper.ToOwnership(info.Ownership) };
            if (!scenario.IsValid)
            {
                scenario = AndroidInventoryMapper.InferScenario(info);
            }
        }

        Guid? policyId = token?.PolicyId;
        if (policyId is null && info.AppliedPolicyName is { } applied && applied.Contains("/policies/solvix-", StringComparison.Ordinal)
            && Guid.TryParseExact(applied[(applied.LastIndexOf("solvix-", StringComparison.Ordinal) + 7)..], "N", out var parsed) && policies.ContainsKey(parsed))
        {
            policyId = parsed;
        }

        var device = MobileDevice.CreatePending(tenantContext.RequireTenantId(), scenario, info.Name, token?.Id, policyId);
        device.CompleteEnrollment(now);
        if (token is { } t && t.UseCount < t.MaxUses)
        {
            t.RegisterUse(now);
        }

        db.Set<MobileDevice>().Add(device);
        DeviceLifecycle.Record(db, device, DeviceEventType.Enrolled,
            $"Enrolled as {scenario.Mode} ({scenario.Ownership}){(token is null ? " without a Solvix enrollment token" : $" with '{token.Name}'")}.");
        return device;
    }

    private async Task<int> RequestAppleInventoryAsync(CancellationToken cancellationToken)
    {
        if (!providers.IsConfigured(MobilePlatform.Ios))
        {
            return 0;
        }

        var cutoff = clock.UtcNow.AddMinutes(-10);
        var devices = await db.Set<MobileDevice>()
            .Where(d => d.Platform == MobilePlatform.Ios && d.State == DeviceState.Enrolled)
            .ToListAsync(cancellationToken);
        var count = 0;
        foreach (var device in devices)
        {
            var hasOpen = await db.Set<DeviceCommand>().AnyAsync(
                c => c.DeviceId == device.Id && c.Type == DeviceCommandType.SyncInventory && (c.State == CommandState.Queued || c.State == CommandState.Sent) && c.CreatedAtUtc > cutoff,
                cancellationToken);
            if (hasOpen)
            {
                continue;
            }

            await commands.EnqueueSystemAsync(device, DeviceCommandType.SyncInventory, "Scheduled inventory refresh.", null, cancellationToken);
            count++;
        }

        if (count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return count;
    }
}
