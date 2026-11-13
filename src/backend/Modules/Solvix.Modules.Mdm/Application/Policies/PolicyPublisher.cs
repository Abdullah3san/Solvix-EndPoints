namespace Solvix.Modules.Mdm.Application.Policies;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Mdm.Application.Commands;
using Solvix.Modules.Mdm.Application.Devices;
using Solvix.Modules.Mdm.Application.Enterprise;
using Solvix.Modules.Mdm.Domain;
using Solvix.Modules.Mdm.Providers;
using Solvix.Modules.Mdm.Security;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Persistence;

public sealed record PolicyPublishOutcome(PolicyDeploymentReport Report, int DevicesNotified);

public interface IPolicyPublisher
{
    Task<PolicyPublishOutcome> PublishAsync(MobilePolicy policy, bool notifyDevices, CancellationToken cancellationToken);

    /// <summary>Publishes only when the current version has not reached the platform yet.</summary>
    Task EnsurePublishedAsync(MobilePolicy policy, CancellationToken cancellationToken);
}

internal sealed class PolicyPublisher(
    ISolvixDbContext db,
    IClock clock,
    IMdmProviderRegistry providers,
    IEnterpriseDirectory enterprises,
    ISecretProtector protector,
    IDeviceCommandQueue commands) : IPolicyPublisher
{
    public async Task EnsurePublishedAsync(MobilePolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.PublishedVersion < policy.Version)
        {
            await PublishAsync(policy, notifyDevices: true, cancellationToken);
        }
    }

    public async Task<PolicyPublishOutcome> PublishAsync(MobilePolicy policy, bool notifyDevices, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var provider = providers.For(policy.Platform);
        var apps = await LoadAppsAsync(policy.Id, cancellationToken);
        var enterprise = await enterprises.GetEnterpriseNameAsync(policy.Platform, cancellationToken);

        var report = await provider.DeployPolicyAsync(
            new PolicyDeployment(policy.Id, policy.Name, policy.Version, policy.Scenario, policy.Settings, apps, enterprise, protector.Unprotect),
            cancellationToken);
        policy.MarkPublished(clock.UtcNow);

        var notified = 0;
        if (notifyDevices)
        {
            var devices = await db.Set<MobileDevice>()
                .Where(d => d.PolicyId == policy.Id && d.State == DeviceState.Enrolled)
                .ToListAsync(cancellationToken);
            foreach (var device in devices)
            {
                // Android devices pick the changed policy up from Google on their own; Apple devices must be told to fetch the profile.
                if (device.Platform == MobilePlatform.Ios)
                {
                    await commands.EnqueueSystemAsync(
                        device, DeviceCommandType.ApplyPolicy, $"Policy '{policy.Name}' v{policy.Version} published.",
                        new CommandParameters(PolicyVersion: policy.Version).ToJson(), cancellationToken);
                }

                DeviceLifecycle.Record(db, device, DeviceEventType.PolicyApplied, $"Policy '{policy.Name}' v{policy.Version} published to the device.");
                notified++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return new PolicyPublishOutcome(report, notified);
    }

    private async Task<IReadOnlyList<PolicyAppSpec>> LoadAppsAsync(Guid policyId, CancellationToken cancellationToken)
    {
        var assignments = await db.Set<MobilePolicyApp>().AsNoTracking().Where(a => a.PolicyId == policyId).ToListAsync(cancellationToken);
        var ids = assignments.Select(a => a.AppId).ToList();
        var catalogue = await db.Set<MobileApp>().AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, cancellationToken);
        return assignments.Where(a => catalogue.ContainsKey(a.AppId))
            .Select(a => new PolicyAppSpec(catalogue[a.AppId].Identifier, catalogue[a.AppId].Name, a.InstallType, catalogue[a.AppId].StoreId, a.ManagedConfigurationJson))
            .ToList();
    }
}
