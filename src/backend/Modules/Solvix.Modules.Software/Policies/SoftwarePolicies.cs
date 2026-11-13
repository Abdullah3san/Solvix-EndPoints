namespace Solvix.Modules.Software.Policies;

using Microsoft.EntityFrameworkCore;
using Solvix.Agent.Contracts.Software;
using Solvix.Modules.Software.Auditing;
using Solvix.Modules.Software.Domain;
using Solvix.SharedKernel.Endpoints;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public sealed record DeploymentPolicyDto(
    bool AllowExeInstallers,
    bool RequireSignedPackages,
    IReadOnlyList<string> TrustedPublisherThumbprints,
    bool RequireApproval,
    int LargeDeploymentThreshold,
    bool UninstallIsSensitive,
    bool ServerTargetsAreSensitive,
    int ApprovalTtlHours)
{
    internal static DeploymentPolicyDto From(SoftwareDeploymentPolicy p) => new(
        p.AllowExeInstallers, p.RequireSignedPackages, p.TrustedPublisherThumbprints, p.RequireApproval, p.LargeDeploymentThreshold,
        p.UninstallIsSensitive, p.ServerTargetsAreSensitive, p.ApprovalTtlHours);
}

public sealed record UpdateDeploymentPolicyRequest(
    bool AllowExeInstallers,
    bool RequireSignedPackages,
    IReadOnlyList<string>? TrustedPublisherThumbprints,
    bool RequireApproval,
    int LargeDeploymentThreshold,
    bool UninstallIsSensitive,
    bool ServerTargetsAreSensitive,
    int ApprovalTtlHours);

public sealed record RestrictionRuleDto(
    Guid Id,
    string Name,
    string? Description,
    RestrictionRuleType RuleType,
    string NamePattern,
    string? Publisher,
    string? MinimumVersion,
    IReadOnlyList<Guid>? DeviceGroupIds,
    bool Enabled)
{
    internal static RestrictionRuleDto From(SoftwareRestrictionRule r) =>
        new(r.Id, r.Name, r.Description, r.RuleType, r.NamePattern, r.Publisher, r.MinimumVersion, r.DeviceGroupIds, r.Enabled);
}

public sealed record SaveRestrictionRuleRequest(
    string Name,
    string? Description,
    RestrictionRuleType RuleType,
    string NamePattern,
    string? Publisher,
    string? MinimumVersion,
    IReadOnlyList<Guid>? DeviceGroupIds,
    bool Enabled = true);

public sealed record DeploymentGroupDto(
    Guid Id,
    string Name,
    string? Description,
    IReadOnlyList<Guid> DeviceIds,
    IReadOnlyList<Guid> DeviceGroupIds,
    IReadOnlyList<Guid> BranchIds,
    IReadOnlyList<Guid> ExcludedDeviceIds,
    int ResolvedDeviceCount)
{
    internal static DeploymentGroupDto From(DeploymentGroup g, int resolved) =>
        new(g.Id, g.Name, g.Description, g.DeviceIds, g.DeviceGroupIds, g.BranchIds, g.ExcludedDeviceIds, resolved);
}

public sealed record SaveDeploymentGroupRequest(
    string Name,
    string? Description,
    IReadOnlyList<Guid>? DeviceIds,
    IReadOnlyList<Guid>? DeviceGroupIds,
    IReadOnlyList<Guid>? BranchIds,
    IReadOnlyList<Guid>? ExcludedDeviceIds);

/// <summary>A device that violates a restriction rule.</summary>
public sealed record ComplianceViolation(
    Guid RuleId,
    string RuleName,
    RestrictionRuleType RuleType,
    Guid DeviceId,
    string DeviceName,
    string? ApplicationName,
    string? InstalledVersion,
    string Problem);

public sealed record ComplianceReport(int DevicesEvaluated, int CompliantDevices, IReadOnlyList<ComplianceViolation> Violations);

/// <summary>Reads the tenant's deployment policy (defaults until saved).</summary>
public sealed class DeploymentPolicyProvider(ISolvixDbContext db, ITenantContext tenantContext)
{
    public async Task<SoftwareDeploymentPolicy> GetAsync(CancellationToken cancellationToken) =>
        await db.Set<SoftwareDeploymentPolicy>().AsNoTracking().FirstOrDefaultAsync(cancellationToken)
        ?? SoftwareDeploymentPolicy.CreateDefault(tenantContext.RequireTenantId());
}

/// <summary>Resolves deployment groups and explicit selections to concrete devices.</summary>
public sealed class TargetResolver(IEndpointDirectory endpoints)
{
    public async Task<IReadOnlyList<EndpointAccessInfo>> ResolveAsync(
        IReadOnlyCollection<Guid> deviceIds, IReadOnlyCollection<Guid> groupIds, IReadOnlyCollection<Guid> branchIds, IReadOnlyCollection<Guid> excluded, CancellationToken cancellationToken)
    {
        var devices = await endpoints.ResolveAsync(deviceIds, groupIds, branchIds, cancellationToken);
        return devices.Where(d => !excluded.Contains(d.Endpoint.Id)).ToList();
    }

    public Task<IReadOnlyList<EndpointAccessInfo>> ResolveAsync(DeploymentGroup group, CancellationToken cancellationToken) =>
        ResolveAsync(group.DeviceIds, group.DeviceGroupIds, group.BranchIds, group.ExcludedDeviceIds, cancellationToken);
}

public interface ISoftwarePolicyService
{
    Task<DeploymentPolicyDto> GetPolicyAsync(CancellationToken cancellationToken);

    Task<DeploymentPolicyDto> UpdatePolicyAsync(UpdateDeploymentPolicyRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<RestrictionRuleDto>> ListRulesAsync(CancellationToken cancellationToken);

    Task<RestrictionRuleDto> SaveRuleAsync(Guid? id, SaveRestrictionRuleRequest request, CancellationToken cancellationToken);

    Task DeleteRuleAsync(Guid id, CancellationToken cancellationToken);

    Task<ComplianceReport> EvaluateComplianceAsync(Guid? deviceId, CancellationToken cancellationToken);

    Task<IReadOnlyList<DeploymentGroupDto>> ListGroupsAsync(CancellationToken cancellationToken);

    Task<DeploymentGroupDto> SaveGroupAsync(Guid? id, SaveDeploymentGroupRequest request, CancellationToken cancellationToken);

    Task DeleteGroupAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed class SoftwarePolicyService(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    IEndpointDirectory endpoints,
    TargetResolver targets,
    SoftwareAuditTrail audit) : ISoftwarePolicyService
{
    public async Task<DeploymentPolicyDto> GetPolicyAsync(CancellationToken cancellationToken) =>
        DeploymentPolicyDto.From(await db.Set<SoftwareDeploymentPolicy>().AsNoTracking().FirstOrDefaultAsync(cancellationToken)
            ?? SoftwareDeploymentPolicy.CreateDefault(tenantContext.RequireTenantId()));

    public async Task<DeploymentPolicyDto> UpdatePolicyAsync(UpdateDeploymentPolicyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.LargeDeploymentThreshold is < 1 or > SoftwareDeployment.MaxTargets)
        {
            throw new ValidationException(nameof(request.LargeDeploymentThreshold), $"Must be between 1 and {SoftwareDeployment.MaxTargets}.");
        }

        if (request.ApprovalTtlHours is < 1 or > 24 * 30)
        {
            throw new ValidationException(nameof(request.ApprovalTtlHours), "Must be between 1 and 720 hours.");
        }

        var thumbprints = request.TrustedPublisherThumbprints ?? [];
        if (thumbprints.Count > 50 || thumbprints.Any(t => SoftwareDeploymentPolicy.NormalizeThumbprint(t) is null))
        {
            throw new ValidationException(nameof(request.TrustedPublisherThumbprints), "Publisher thumbprints must be SHA-256 certificate thumbprints (64 hex characters), at most 50.");
        }

        return await audit.AtomicAsync(
            async ct =>
            {
                var policy = await db.Set<SoftwareDeploymentPolicy>().FirstOrDefaultAsync(ct);
                if (policy is null)
                {
                    policy = SoftwareDeploymentPolicy.CreateDefault(tenantContext.RequireTenantId());
                    db.Set<SoftwareDeploymentPolicy>().Add(policy);
                }

                policy.Update(request.AllowExeInstallers, request.RequireSignedPackages, thumbprints, request.RequireApproval, request.LargeDeploymentThreshold,
                    request.UninstallIsSensitive, request.ServerTargetsAreSensitive, request.ApprovalTtlHours);
                audit.Record(new SoftwareAuditEntry(SoftwareAuditEvents.PolicyChanged, SoftwareAuditOutcome.Success,
                    Detail: $"exe={request.AllowExeInstallers}; signed={request.RequireSignedPackages}; approval={request.RequireApproval}; threshold={request.LargeDeploymentThreshold}; trusted={thumbprints.Count}"));
                return DeploymentPolicyDto.From(policy);
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<RestrictionRuleDto>> ListRulesAsync(CancellationToken cancellationToken) =>
        (await db.Set<SoftwareRestrictionRule>().AsNoTracking().OrderBy(r => r.Name).ToListAsync(cancellationToken)).Select(RestrictionRuleDto.From).ToList();

    public async Task<RestrictionRuleDto> SaveRuleAsync(Guid? id, SaveRestrictionRuleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = Text(request.Name, 1, 120, nameof(request.Name));
        var pattern = Text(request.NamePattern, 1, 256, nameof(request.NamePattern));
        if (!Enum.IsDefined(request.RuleType))
        {
            throw new ValidationException(nameof(request.RuleType), "Unknown rule type.");
        }

        if (pattern.Trim('*', '?', ' ').Length == 0)
        {
            throw new ValidationException(nameof(request.NamePattern), "The pattern must contain at least one literal character.");
        }

        if (request.MinimumVersion is { } version && !string.IsNullOrWhiteSpace(version) && !SoftwareVersion.IsValid(version.Trim()))
        {
            throw new ValidationException(nameof(request.MinimumVersion), "Not a valid version.");
        }

        return await audit.AtomicAsync(
            async ct =>
            {
                SoftwareRestrictionRule rule;
                if (id is { } existing)
                {
                    rule = await db.Set<SoftwareRestrictionRule>().FirstOrDefaultAsync(r => r.Id == existing, ct) ?? throw new NotFoundException("Restriction rule", existing);
                }
                else
                {
                    rule = new SoftwareRestrictionRule(tenantContext.RequireTenantId());
                    db.Set<SoftwareRestrictionRule>().Add(rule);
                }

                rule.Update(name, Optional(request.Description, 500, nameof(request.Description)), request.RuleType, pattern, Optional(request.Publisher, 256, nameof(request.Publisher)),
                    request.MinimumVersion, request.DeviceGroupIds, request.Enabled);
                audit.Record(new SoftwareAuditEntry(SoftwareAuditEvents.RuleChanged, SoftwareAuditOutcome.Success, Detail: $"{rule.RuleType} '{rule.NamePattern}' ({(id is null ? "created" : "updated")}, enabled={rule.Enabled})"));
                return RestrictionRuleDto.From(rule);
            },
            cancellationToken);
    }

    public Task DeleteRuleAsync(Guid id, CancellationToken cancellationToken) =>
        audit.AtomicAsync(
            async ct =>
            {
                var rule = await db.Set<SoftwareRestrictionRule>().FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException("Restriction rule", id);
                db.Set<SoftwareRestrictionRule>().Remove(rule);
                audit.Record(new SoftwareAuditEntry(SoftwareAuditEvents.RuleChanged, SoftwareAuditOutcome.Success, Detail: $"{rule.RuleType} '{rule.NamePattern}' deleted"));
                return true;
            },
            cancellationToken);

    /// <summary>Evaluates enabled restriction rules against the reported inventory (all devices or one).</summary>
    public async Task<ComplianceReport> EvaluateComplianceAsync(Guid? deviceId, CancellationToken cancellationToken)
    {
        var rules = await db.Set<SoftwareRestrictionRule>().AsNoTracking().Where(r => r.Enabled).ToListAsync(cancellationToken);
        var inventory = db.Set<SoftwareInventoryItem>().AsNoTracking();
        if (deviceId is { } only)
        {
            inventory = inventory.Where(i => i.DeviceId == only);
        }

        var items = await inventory.Select(i => new { i.DeviceId, i.Name, i.Version, i.Publisher }).ToListAsync(cancellationToken);
        var deviceIds = deviceId is { } d ? [d] : items.Select(i => i.DeviceId).Distinct().ToList();
        var devices = await endpoints.ResolveAsync(deviceIds, [], [], cancellationToken);
        var byDevice = items.ToLookup(i => i.DeviceId);
        var violations = new List<ComplianceViolation>();
        foreach (var device in devices.Where(x => x.Endpoint.IsActive))
        {
            var apps = byDevice[device.Endpoint.Id].ToList();
            foreach (var rule in rules.Where(r => r.AppliesTo(device.GroupIds)))
            {
                var matching = apps.Where(a => rule.MatchesIdentity(a.Name, a.Publisher)).ToList();
                if (rule.RuleType == RestrictionRuleType.Blocked)
                {
                    violations.AddRange(matching.Where(a => rule.BlocksVersion(a.Version)).Select(a => new ComplianceViolation(
                        rule.Id, rule.Name, rule.RuleType, device.Endpoint.Id, device.Endpoint.Hostname, a.Name, a.Version,
                        rule.MinimumVersion is null ? "Blocked software is installed." : $"Version below {rule.MinimumVersion} is installed.")));
                }
                else if (!matching.Any(a => rule.MinimumVersion is null || (a.Version is not null && SoftwareVersion.Compare(a.Version, rule.MinimumVersion) >= 0)))
                {
                    var found = matching.OrderByDescending(a => a.Version, SoftwareVersion.Comparer!).FirstOrDefault();
                    violations.Add(new ComplianceViolation(
                        rule.Id, rule.Name, rule.RuleType, device.Endpoint.Id, device.Endpoint.Hostname, found?.Name, found?.Version,
                        found is null ? "Required software is missing." : $"Required version {rule.MinimumVersion} or later is missing."));
                }
            }
        }

        var evaluated = devices.Count(x => x.Endpoint.IsActive);
        return new ComplianceReport(evaluated, evaluated - violations.Select(v => v.DeviceId).Distinct().Count(), violations);
    }

    public async Task<IReadOnlyList<DeploymentGroupDto>> ListGroupsAsync(CancellationToken cancellationToken)
    {
        var groups = await db.Set<DeploymentGroup>().AsNoTracking().OrderBy(g => g.Name).ToListAsync(cancellationToken);
        var result = new List<DeploymentGroupDto>();
        foreach (var group in groups)
        {
            var resolved = await targets.ResolveAsync(group, cancellationToken);
            result.Add(DeploymentGroupDto.From(group, resolved.Count(d => d.Endpoint.IsActive)));
        }

        return result;
    }

    public async Task<DeploymentGroupDto> SaveGroupAsync(Guid? id, SaveDeploymentGroupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = Text(request.Name, 1, 120, nameof(request.Name));
        var devices = request.DeviceIds ?? [];
        var groups = request.DeviceGroupIds ?? [];
        var branches = request.BranchIds ?? [];
        var excluded = request.ExcludedDeviceIds ?? [];
        if (devices.Count + groups.Count + branches.Count == 0)
        {
            throw new ValidationException(nameof(request.DeviceIds), "Select at least one device, device group or branch.");
        }

        if (devices.Count + excluded.Count > SoftwareDeployment.MaxTargets || groups.Count > 200 || branches.Count > 200)
        {
            throw new ValidationException(nameof(request.DeviceIds), "Too many entries.");
        }

        // Explicit devices must exist in this tenant.
        var known = await endpoints.FindAsync(devices.Concat(excluded).Distinct().ToList(), cancellationToken);
        if (devices.Concat(excluded).FirstOrDefault(x => !known.ContainsKey(x)) is var unknown && unknown != Guid.Empty)
        {
            throw new ValidationException(nameof(request.DeviceIds), $"Device '{unknown}' was not found.");
        }

        var saved = await audit.AtomicAsync(
            async ct =>
            {
                DeploymentGroup group;
                if (id is { } existing)
                {
                    group = await db.Set<DeploymentGroup>().FirstOrDefaultAsync(g => g.Id == existing, ct) ?? throw new NotFoundException("Deployment group", existing);
                }
                else
                {
                    group = new DeploymentGroup(tenantContext.RequireTenantId());
                    db.Set<DeploymentGroup>().Add(group);
                }

                if (await db.Set<DeploymentGroup>().AnyAsync(g => g.Id != group.Id && g.Name == name, ct))
                {
                    throw new ConflictException($"A deployment group named '{name}' already exists.", "deployment_group_exists");
                }

                group.Update(name, Optional(request.Description, 500, nameof(request.Description)), devices, groups, branches, excluded);
                audit.Record(new SoftwareAuditEntry(SoftwareAuditEvents.GroupChanged, SoftwareAuditOutcome.Success,
                    Detail: $"'{name}': devices={devices.Count}, groups={groups.Count}, branches={branches.Count}, excluded={excluded.Count}"));
                return group;
            },
            cancellationToken);
        var resolved = await targets.ResolveAsync(saved, cancellationToken);
        return DeploymentGroupDto.From(saved, resolved.Count(d => d.Endpoint.IsActive));
    }

    public Task DeleteGroupAsync(Guid id, CancellationToken cancellationToken) =>
        audit.AtomicAsync(
            async ct =>
            {
                var group = await db.Set<DeploymentGroup>().FirstOrDefaultAsync(g => g.Id == id, ct) ?? throw new NotFoundException("Deployment group", id);
                db.Set<DeploymentGroup>().Remove(group);
                audit.Record(new SoftwareAuditEntry(SoftwareAuditEvents.GroupChanged, SoftwareAuditOutcome.Success, Detail: $"'{group.Name}' deleted"));
                return true;
            },
            cancellationToken);

    internal static string Text(string? value, int min, int max, string property)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length < min || trimmed.Length > max || trimmed.Any(char.IsControl))
        {
            throw new ValidationException(property, $"Must be {min}–{max} printable characters.");
        }

        return trimmed;
    }

    internal static string? Optional(string? value, int max, string property) =>
        string.IsNullOrWhiteSpace(value) ? null : Text(value, 1, max, property);
}
