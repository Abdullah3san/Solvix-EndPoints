namespace Solvix.Modules.Mdm.Application.Policies;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Mdm.Domain;
using Solvix.Modules.Mdm.Security;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Tenancy;

public interface IMobilePolicyService
{
    Task<PagedResult<PolicyDto>> ListAsync(MobilePlatform? platform, int page, int pageSize, CancellationToken cancellationToken);

    Task<PolicyDto> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<PolicyDto> CreateAsync(CreatePolicyRequest request, CancellationToken cancellationToken);

    Task<PolicyDto> UpdateAsync(Guid id, UpdatePolicyRequest request, CancellationToken cancellationToken);

    Task<PolicyDto> SetAppsAsync(Guid id, SetPolicyAppsRequest request, CancellationToken cancellationToken);

    /// <summary>Hands the current version to the platform provider and notifies devices that must re-fetch it.</summary>
    Task<PublishPolicyResultDto> PublishAsync(Guid id, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);

    IReadOnlyList<CapabilitiesDto> Capabilities();
}

internal sealed class MobilePolicyService(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    ISecretProtector protector,
    IPolicyPublisher publisher) : IMobilePolicyService
{
    public async Task<PagedResult<PolicyDto>> ListAsync(MobilePlatform? platform, int page, int pageSize, CancellationToken cancellationToken)
    {
        var request = new PageRequest(page, pageSize);
        var query = db.Set<MobilePolicy>().AsNoTracking();
        if (platform is { } p)
        {
            query = query.Where(x => x.Platform == p);
        }

        var total = await query.LongCountAsync(cancellationToken);
        var items = await query.OrderBy(x => x.Name).Skip(request.Skip).Take(request.SafePageSize).ToListAsync(cancellationToken);
        var dtos = new List<PolicyDto>();
        foreach (var item in items)
        {
            dtos.Add(await ToDtoAsync(item, cancellationToken));
        }

        return new PagedResult<PolicyDto>(dtos, request.SafePage, request.SafePageSize, total);
    }

    public async Task<PolicyDto> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await ToDtoAsync(await FindAsync(id, cancellationToken), cancellationToken);

    public async Task<PolicyDto> CreateAsync(CreatePolicyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scenario = new ManagementScenario(request.Platform, request.Mode, request.Ownership).EnsureValid();
        if (await db.Set<MobilePolicy>().AnyAsync(p => p.Name == request.Name.Trim(), cancellationToken))
        {
            throw new ConflictException($"A policy named '{request.Name.Trim()}' already exists.");
        }

        var settings = request.Settings ?? new MobilePolicySettings();
        PolicySettingsSupport.Validate(scenario, settings);
        var stored = PolicySettingsSupport.Protect(settings, null, protector);
        PolicySettingsSupport.EnsureSecrets(stored);

        var policy = MobilePolicy.Create(tenantContext.RequireTenantId(), request.Name, request.Description, scenario, stored);
        db.Set<MobilePolicy>().Add(policy);
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(policy, cancellationToken);
    }

    public async Task<PolicyDto> UpdateAsync(Guid id, UpdatePolicyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var policy = await FindAsync(id, cancellationToken);
        var name = request.Name.Trim();
        if (await db.Set<MobilePolicy>().AnyAsync(p => p.Name == name && p.Id != id, cancellationToken))
        {
            throw new ConflictException($"A policy named '{name}' already exists.");
        }

        var settings = request.Settings ?? new MobilePolicySettings();
        PolicySettingsSupport.Validate(policy.Scenario, settings);
        var stored = PolicySettingsSupport.Protect(settings, policy.Settings, protector);
        PolicySettingsSupport.EnsureSecrets(stored);
        policy.Update(request.Name, request.Description, stored);
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(policy, cancellationToken);
    }

    public async Task<PolicyDto> SetAppsAsync(Guid id, SetPolicyAppsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var policy = await FindAsync(id, cancellationToken);
        var wanted = request.Apps ?? [];
        if (wanted.Select(a => a.AppId).Distinct().Count() != wanted.Count)
        {
            throw new ValidationException("apps", "An application can only be assigned once per policy.");
        }

        var appIds = wanted.Select(a => a.AppId).ToList();
        var catalogue = await db.Set<MobileApp>().Where(a => appIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, cancellationToken);
        var errors = new Dictionary<string, string[]>();
        for (var i = 0; i < wanted.Count; i++)
        {
            var w = wanted[i];
            if (!catalogue.TryGetValue(w.AppId, out var app))
            {
                errors[$"apps[{i}].appId"] = ["Application not found."];
                continue;
            }

            if (app.Platform != policy.Platform)
            {
                errors[$"apps[{i}].appId"] = [$"{app.Name} is an {app.Platform} app and cannot be assigned to an {policy.Platform} policy."];
            }
            else if (w.InstallType == AppInstallType.Kiosk && policy.Mode != ManagementMode.Dedicated)
            {
                errors[$"apps[{i}].installType"] = ["Kiosk is only valid for dedicated devices."];
            }
            else if (policy.Platform == MobilePlatform.Ios && w.InstallType == AppInstallType.ForceInstalled && app.StoreId is null)
            {
                errors[$"apps[{i}].installType"] = ["Silent installation on iOS needs the App Store id of the app."];
            }
            else if (policy.Platform == MobilePlatform.Ios && w.InstallType == AppInstallType.Kiosk)
            {
                errors[$"apps[{i}].installType"] = ["Kiosk is Android-only."];
            }
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        var existing = await db.Set<MobilePolicyApp>().Where(a => a.PolicyId == id).ToListAsync(cancellationToken);
        db.Set<MobilePolicyApp>().RemoveRange(existing.Where(e => !appIds.Contains(e.AppId)));
        for (var i = 0; i < wanted.Count; i++)
        {
            var w = wanted[i];
            var config = PolicySettingsSupport.NormalizeManagedConfiguration(w.ManagedConfigurationJson, $"apps[{i}].managedConfigurationJson");
            var current = existing.FirstOrDefault(e => e.AppId == w.AppId);
            if (current is null)
            {
                db.Set<MobilePolicyApp>().Add(new MobilePolicyApp(tenantContext.RequireTenantId(), id, w.AppId, w.InstallType, config));
            }
            else
            {
                current.Update(w.InstallType, config);
            }
        }

        policy.TouchAssignments();
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(policy, cancellationToken);
    }

    public async Task<PublishPolicyResultDto> PublishAsync(Guid id, CancellationToken cancellationToken)
    {
        var policy = await FindAsync(id, cancellationToken);
        var outcome = await publisher.PublishAsync(policy, notifyDevices: true, cancellationToken);
        return new PublishPolicyResultDto(await ToDtoAsync(policy, cancellationToken), outcome.Report.AppliedSettings, outcome.Report.SkippedSettings, outcome.DevicesNotified);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var policy = await FindAsync(id, cancellationToken);
        if (await db.Set<MobileDevice>().AnyAsync(d => d.PolicyId == id && (d.State == DeviceState.Enrolled || d.State == DeviceState.RemovalPending || d.State == DeviceState.Pending), cancellationToken))
        {
            throw new ConflictException("Devices are still assigned to this policy. Reassign or retire them first.");
        }

        if (await db.Set<EnrollmentToken>().AnyAsync(t => t.PolicyId == id, cancellationToken))
        {
            throw new ConflictException("Enrollment tokens were issued for this policy and are kept for audit. Edit the policy instead of deleting it.");
        }

        // Retired devices keep their history but lose the reference to the policy being deleted.
        foreach (var device in await db.Set<MobileDevice>().Where(d => d.PolicyId == id).ToListAsync(cancellationToken))
        {
            device.AssignPolicy(null);
        }

        db.Set<MobilePolicy>().Remove(policy);
        await db.SaveChangesAsync(cancellationToken);
    }

    public IReadOnlyList<CapabilitiesDto> Capabilities() =>
        MdmCapabilities.All.Select(CapabilitiesDto.From)
            .OrderBy(c => c.Platform).ThenBy(c => c.Mode).ThenBy(c => c.Ownership).ToList();

    private async Task<MobilePolicy> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Set<MobilePolicy>().FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
        ?? throw new NotFoundException("Policy", id);

    private async Task<PolicyDto> ToDtoAsync(MobilePolicy p, CancellationToken cancellationToken)
    {
        var assignments = await db.Set<MobilePolicyApp>().AsNoTracking().Where(a => a.PolicyId == p.Id).ToListAsync(cancellationToken);
        var appIds = assignments.Select(a => a.AppId).ToList();
        var apps = await db.Set<MobileApp>().AsNoTracking().Where(a => appIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, cancellationToken);
        var deviceCount = await db.Set<MobileDevice>().CountAsync(d => d.PolicyId == p.Id && (d.State == DeviceState.Enrolled || d.State == DeviceState.RemovalPending), cancellationToken);
        return new PolicyDto(
            p.Id, p.Name, p.Description, p.Platform, p.Mode, p.Ownership, p.Version, p.PublishedVersion, p.PublishedAtUtc,
            PolicySettingsSupport.Redact(p.Settings),
            assignments.Where(a => apps.ContainsKey(a.AppId))
                .Select(a => new PolicyAppDto(a.AppId, apps[a.AppId].Name, apps[a.AppId].Identifier, a.InstallType, a.ManagedConfigurationJson))
                .OrderBy(a => a.Name).ToList(),
            deviceCount, p.CreatedAtUtc, p.UpdatedAtUtc);
    }
}
