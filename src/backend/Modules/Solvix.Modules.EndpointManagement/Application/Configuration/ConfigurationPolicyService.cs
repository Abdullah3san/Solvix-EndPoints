namespace Solvix.Modules.EndpointManagement.Application.Configuration;

using Microsoft.EntityFrameworkCore;
using Solvix.Endpoint.Contracts.Configuration;
using Solvix.Modules.EndpointManagement.Application.Targeting;
using Solvix.Modules.EndpointManagement.Domain.Configuration;
using Solvix.Modules.EndpointManagement.Domain.MaintenanceWindows;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Auditing;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public interface IConfigurationPolicyService
{
    Task<IReadOnlyList<ConfigurationPolicySummaryDto>> ListAsync(bool includeArchived, CancellationToken cancellationToken);

    Task<ConfigurationPolicyDto> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<ConfigurationPolicyDto> CreateAsync(CreateConfigurationPolicyRequest request, CancellationToken cancellationToken);

    Task<ConfigurationPolicyDto> UpdateAsync(Guid id, UpdateConfigurationPolicyRequest request, CancellationToken cancellationToken);

    Task<ConfigurationPolicyDto> ArchiveAsync(Guid id, CancellationToken cancellationToken);

    Task<PolicyVersionDto> GetVersionAsync(Guid policyId, int number, CancellationToken cancellationToken);

    Task<PolicyVersionDto> CreateVersionAsync(Guid policyId, CreatePolicyVersionRequest request, CancellationToken cancellationToken);

    Task<PolicyVersionDto> UpdateDraftAsync(Guid policyId, int number, UpdatePolicyVersionRequest request, CancellationToken cancellationToken);

    Task<PolicyVersionDto> SubmitAsync(Guid policyId, int number, CancellationToken cancellationToken);

    Task<PolicyVersionDto> ApproveAsync(Guid policyId, int number, ReviewPolicyVersionRequest request, CancellationToken cancellationToken);

    Task<PolicyVersionDto> RejectAsync(Guid policyId, int number, ReviewPolicyVersionRequest request, CancellationToken cancellationToken);

    /// <summary>Makes an approved version (or, as a fast rollback, a previously published one) the active version.</summary>
    Task<PolicyVersionDto> PublishAsync(Guid policyId, int number, CancellationToken cancellationToken);

    /// <summary>Copies an older version into a new draft, which then goes through approval like any change.</summary>
    Task<PolicyVersionDto> RestoreAsync(Guid policyId, int number, CancellationToken cancellationToken);

    Task<IReadOnlyList<AssignmentDto>> ListAssignmentsAsync(Guid policyId, CancellationToken cancellationToken);

    Task<AssignmentDto> AssignAsync(Guid policyId, SaveAssignmentRequest request, CancellationToken cancellationToken);

    Task<AssignmentDto> UpdateAssignmentAsync(Guid assignmentId, UpdateAssignmentRequest request, CancellationToken cancellationToken);

    Task RemoveAssignmentAsync(Guid assignmentId, CancellationToken cancellationToken);

    Task<IReadOnlyList<DeviceConflictsDto>> ListConflictsAsync(bool includeResolved, CancellationToken cancellationToken);

    Task<DeviceConfigurationDto> GetDeviceConfigurationAsync(Guid deviceId, CancellationToken cancellationToken);
}

internal sealed class ConfigurationPolicyService(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IClock clock,
    IAuditTrail audit,
    ITargetingService targeting,
    IConfigurationEngine engine) : IConfigurationPolicyService
{
    public async Task<IReadOnlyList<ConfigurationPolicySummaryDto>> ListAsync(bool includeArchived, CancellationToken cancellationToken)
    {
        var policies = await db.Set<ConfigurationPolicy>().AsNoTracking()
            .Where(p => includeArchived || !p.IsArchived)
            .OrderBy(p => p.Name)
            .ToListAsync(cancellationToken);
        var ids = policies.Select(p => p.Id).ToList();
        var versions = await db.Set<ConfigurationPolicyVersion>().AsNoTracking()
            .Where(v => ids.Contains(v.PolicyId))
            .Select(v => new { v.Id, v.PolicyId, v.Number, v.Status, v.MaxRisk })
            .ToListAsync(cancellationToken);
        var counts = await db.Set<ConfigurationAssignment>().AsNoTracking()
            .Where(a => ids.Contains(a.PolicyId))
            .GroupBy(a => a.PolicyId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        return policies.Select(p =>
        {
            var own = versions.Where(v => v.PolicyId == p.Id).ToList();
            var active = own.FirstOrDefault(v => v.Id == p.ActiveVersionId);
            var latest = own.MaxBy(v => v.Number)!;
            return new ConfigurationPolicySummaryDto(
                p.Id, p.Name, p.Description, p.IsArchived, active?.Number, active?.MaxRisk,
                latest.Number, latest.Status, counts.GetValueOrDefault(p.Id), p.UpdatedAtUtc);
        }).ToList();
    }

    public async Task<ConfigurationPolicyDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var policy = await db.Set<ConfigurationPolicy>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new NotFoundException("ConfigurationPolicy", id);
        var versions = await db.Set<ConfigurationPolicyVersion>().AsNoTracking()
            .Where(v => v.PolicyId == id).OrderByDescending(v => v.Number).ToListAsync(cancellationToken);
        var assignments = await db.Set<ConfigurationAssignment>().AsNoTracking()
            .Where(a => a.PolicyId == id).OrderBy(a => a.Priority).ThenBy(a => a.CreatedAtUtc).ToListAsync(cancellationToken);
        return new ConfigurationPolicyDto(
            policy.Id, policy.Name, policy.Description, policy.IsArchived, policy.ActiveVersionId,
            versions.Select(PolicyVersionDto.From).ToList(),
            assignments.Select(AssignmentDto.From).ToList());
    }

    public async Task<ConfigurationPolicyDto> CreateAsync(CreateConfigurationPolicyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureUniqueNameAsync(request.Name, null, cancellationToken);
        var tenantId = tenantContext.RequireTenantId();
        var policy = ConfigurationPolicy.Create(tenantId, request.Name, request.Description);
        var version = ConfigurationPolicyVersion.CreateDraft(tenantId, policy, request.Settings, request.ChangeNote, currentUser.RequireUserId());
        db.Set<ConfigurationPolicy>().Add(policy);
        db.Set<ConfigurationPolicyVersion>().Add(version);
        audit.Record("configuration.policy_created", nameof(ConfigurationPolicy), policy.Id, $"Policy '{policy.Name}' created with draft v{version.Number} ({version.GetSettings().Count} settings, risk {version.MaxRisk}).");
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(policy.Id, cancellationToken);
    }

    public async Task<ConfigurationPolicyDto> UpdateAsync(Guid id, UpdateConfigurationPolicyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var policy = await FindPolicyAsync(id, cancellationToken);
        await EnsureUniqueNameAsync(request.Name, id, cancellationToken);
        policy.Rename(request.Name, request.Description);
        audit.Record("configuration.policy_updated", nameof(ConfigurationPolicy), id, $"Policy '{policy.Name}' renamed/described.");
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<ConfigurationPolicyDto> ArchiveAsync(Guid id, CancellationToken cancellationToken)
    {
        var policy = await FindPolicyAsync(id, cancellationToken);
        var active = policy.ActiveVersionId is { } activeId
            ? await db.Set<ConfigurationPolicyVersion>().FirstOrDefaultAsync(v => v.Id == activeId, cancellationToken)
            : null;
        active?.Supersede(clock.UtcNow);
        policy.Archive();
        audit.Record("configuration.policy_archived", nameof(ConfigurationPolicy), id, $"Policy '{policy.Name}' archived; it is no longer delivered (settings revert where assignments ask for it).");
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<PolicyVersionDto> GetVersionAsync(Guid policyId, int number, CancellationToken cancellationToken) =>
        PolicyVersionDto.From(await FindVersionAsync(policyId, number, tracked: false, cancellationToken));

    public async Task<PolicyVersionDto> CreateVersionAsync(Guid policyId, CreatePolicyVersionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var policy = await FindPolicyAsync(policyId, cancellationToken);
        await EnsureNoOpenDraftAsync(policyId, cancellationToken);
        IReadOnlyList<ConfigurationSetting> settings;
        if (request.Settings is not null)
        {
            settings = request.Settings;
        }
        else
        {
            var source = policy.ActiveVersionId is { } activeId
                ? await db.Set<ConfigurationPolicyVersion>().AsNoTracking().FirstAsync(v => v.Id == activeId, cancellationToken)
                : await db.Set<ConfigurationPolicyVersion>().AsNoTracking().Where(v => v.PolicyId == policyId).OrderByDescending(v => v.Number).FirstAsync(cancellationToken);
            settings = source.GetSettings();
        }

        var version = ConfigurationPolicyVersion.CreateDraft(policy.TenantId, policy, settings, request.ChangeNote, currentUser.RequireUserId());
        db.Set<ConfigurationPolicyVersion>().Add(version);
        audit.Record("configuration.version_created", nameof(ConfigurationPolicy), policyId, $"Draft v{version.Number} of '{policy.Name}' created.");
        await db.SaveChangesAsync(cancellationToken);
        return PolicyVersionDto.From(version);
    }

    public async Task<PolicyVersionDto> UpdateDraftAsync(Guid policyId, int number, UpdatePolicyVersionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var version = await FindVersionAsync(policyId, number, tracked: true, cancellationToken);
        version.ReplaceSettings(request.Settings, request.ChangeNote);
        audit.Record("configuration.version_edited", nameof(ConfigurationPolicy), policyId, $"Draft v{number} edited ({version.GetSettings().Count} settings, risk {version.MaxRisk}).");
        await db.SaveChangesAsync(cancellationToken);
        return PolicyVersionDto.From(version);
    }

    public async Task<PolicyVersionDto> SubmitAsync(Guid policyId, int number, CancellationToken cancellationToken)
    {
        var version = await FindVersionAsync(policyId, number, tracked: true, cancellationToken);
        if (version.GetSettings().Count == 0)
        {
            throw new ValidationException("settings", "A version needs at least one setting before it can be submitted.");
        }

        version.Submit(currentUser.RequireUserId(), clock.UtcNow);
        audit.Record("configuration.version_submitted", nameof(ConfigurationPolicy), policyId, $"v{number} submitted for approval (risk {version.MaxRisk}).");
        await db.SaveChangesAsync(cancellationToken);
        return PolicyVersionDto.From(version);
    }

    public async Task<PolicyVersionDto> ApproveAsync(Guid policyId, int number, ReviewPolicyVersionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var version = await FindVersionAsync(policyId, number, tracked: true, cancellationToken);
        version.Approve(currentUser.RequireUserId(), currentUser.Role == TenantRole.Owner, request.AcknowledgeDestructive, request.Note, clock.UtcNow);
        audit.Record(
            "configuration.version_approved",
            nameof(ConfigurationPolicy),
            policyId,
            $"v{number} approved with authorised risk {version.AuthorizedRisk}{(version.DestructiveAcknowledged ? " (destructive changes explicitly acknowledged)" : string.Empty)}.",
            new { number, version.MaxRisk, version.ContentHash, request.Note });
        await db.SaveChangesAsync(cancellationToken);
        return PolicyVersionDto.From(version);
    }

    public async Task<PolicyVersionDto> RejectAsync(Guid policyId, int number, ReviewPolicyVersionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var version = await FindVersionAsync(policyId, number, tracked: true, cancellationToken);
        version.Reject(currentUser.RequireUserId(), request.Note, clock.UtcNow);
        audit.Record("configuration.version_rejected", nameof(ConfigurationPolicy), policyId, $"v{number} rejected.", new { request.Note });
        await db.SaveChangesAsync(cancellationToken);
        return PolicyVersionDto.From(version);
    }

    public async Task<PolicyVersionDto> PublishAsync(Guid policyId, int number, CancellationToken cancellationToken)
    {
        var policy = await FindPolicyAsync(policyId, cancellationToken);
        if (policy.IsArchived)
        {
            throw new ConflictException("Archived policies cannot be published.");
        }

        var version = await FindVersionAsync(policyId, number, tracked: true, cancellationToken);
        var risk = version.AuthorizedRisk ?? version.MaxRisk;
        if (risk >= SettingRisk.Destructive && currentUser.Role != TenantRole.Owner)
        {
            throw new ForbiddenException("Publishing a version with destructive settings requires a workspace Owner.");
        }

        var isRollback = version.Status == PolicyVersionStatus.Superseded;
        var now = clock.UtcNow;
        if (policy.ActiveVersionId is { } currentId && currentId != version.Id)
        {
            var current = await db.Set<ConfigurationPolicyVersion>().FirstAsync(v => v.Id == currentId, cancellationToken);
            current.Supersede(now);

            // Flush first: the database allows only one published version per policy.
            await db.SaveChangesAsync(cancellationToken);
        }

        version.Publish(currentUser.RequireUserId(), now);
        policy.Activate(version.Id);
        audit.Record(
            isRollback ? "configuration.version_rolled_back" : "configuration.version_published",
            nameof(ConfigurationPolicy),
            policyId,
            isRollback ? $"'{policy.Name}' rolled back to v{number}." : $"'{policy.Name}' v{number} published.",
            new { number, version.ContentHash, risk });
        await db.SaveChangesAsync(cancellationToken);
        return PolicyVersionDto.From(version);
    }

    public async Task<PolicyVersionDto> RestoreAsync(Guid policyId, int number, CancellationToken cancellationToken)
    {
        var policy = await FindPolicyAsync(policyId, cancellationToken);
        var source = await FindVersionAsync(policyId, number, tracked: false, cancellationToken);
        await EnsureNoOpenDraftAsync(policyId, cancellationToken);
        var version = ConfigurationPolicyVersion.CreateDraft(
            policy.TenantId, policy, source.GetSettings(), $"Restored from v{number}.", currentUser.RequireUserId(), restoredFrom: number);
        db.Set<ConfigurationPolicyVersion>().Add(version);
        audit.Record("configuration.version_restored", nameof(ConfigurationPolicy), policyId, $"v{number} of '{policy.Name}' restored into draft v{version.Number}.");
        await db.SaveChangesAsync(cancellationToken);
        return PolicyVersionDto.From(version);
    }

    public async Task<IReadOnlyList<AssignmentDto>> ListAssignmentsAsync(Guid policyId, CancellationToken cancellationToken)
    {
        await FindPolicyAsync(policyId, cancellationToken);
        return (await db.Set<ConfigurationAssignment>().AsNoTracking()
                .Where(a => a.PolicyId == policyId).OrderBy(a => a.Priority).ToListAsync(cancellationToken))
            .Select(AssignmentDto.From).ToList();
    }

    public async Task<AssignmentDto> AssignAsync(Guid policyId, SaveAssignmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var policy = await FindPolicyAsync(policyId, cancellationToken);
        if (policy.IsArchived)
        {
            throw new ConflictException("Archived policies cannot be assigned.");
        }

        var target = new Domain.Targeting.TargetRef(request.TargetType, request.TargetType == Domain.Targeting.TargetType.AllDevices ? null : request.TargetId, request.IsExclusion);
        await targeting.ValidateAsync([target], "target", cancellationToken);
        await ValidateWindowAsync(request.MaintenanceWindowId, cancellationToken);
        if (await db.Set<ConfigurationAssignment>().AnyAsync(
                a => a.PolicyId == policyId && a.TargetType == target.Type && a.TargetId == target.Id && a.IsExclusion == target.IsExclusion,
                cancellationToken))
        {
            throw new ConflictException("The policy is already assigned to this target.");
        }

        if (!request.IsExclusion && request.Mode == EnforcementMode.Enforce)
        {
            await RequireOwnerForDestructiveAsync(policy, cancellationToken);
        }

        var assignment = new ConfigurationAssignment(
            policy.TenantId, policyId, target.Type, target.Id, target.IsExclusion, request.Mode, request.Priority,
            request.MaintenanceWindowId, request.RemovalBehavior, currentUser.RequireUserId());
        db.Set<ConfigurationAssignment>().Add(assignment);
        audit.Record(
            "configuration.assignment_created",
            nameof(ConfigurationPolicy),
            policyId,
            $"'{policy.Name}' {(target.IsExclusion ? "excluded from" : "assigned to")} {target.Type} {target.Id?.ToString() ?? string.Empty} ({request.Mode}, priority {request.Priority}).",
            AssignmentDto.From(assignment));
        await db.SaveChangesAsync(cancellationToken);
        return AssignmentDto.From(assignment);
    }

    public async Task<AssignmentDto> UpdateAssignmentAsync(Guid assignmentId, UpdateAssignmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var assignment = await db.Set<ConfigurationAssignment>().FirstOrDefaultAsync(a => a.Id == assignmentId, cancellationToken)
            ?? throw new NotFoundException("ConfigurationAssignment", assignmentId);
        await ValidateWindowAsync(request.MaintenanceWindowId, cancellationToken);
        if (!assignment.IsExclusion && request.Mode == EnforcementMode.Enforce && assignment.Mode != EnforcementMode.Enforce)
        {
            await RequireOwnerForDestructiveAsync(await FindPolicyAsync(assignment.PolicyId, cancellationToken), cancellationToken);
        }

        assignment.Update(request.Mode, request.Priority, request.MaintenanceWindowId, request.RemovalBehavior);
        audit.Record("configuration.assignment_updated", nameof(ConfigurationPolicy), assignment.PolicyId, $"Assignment {assignmentId} changed to {request.Mode}, priority {request.Priority}.", request);
        await db.SaveChangesAsync(cancellationToken);
        return AssignmentDto.From(assignment);
    }

    public async Task RemoveAssignmentAsync(Guid assignmentId, CancellationToken cancellationToken)
    {
        var assignment = await db.Set<ConfigurationAssignment>().FirstOrDefaultAsync(a => a.Id == assignmentId, cancellationToken)
            ?? throw new NotFoundException("ConfigurationAssignment", assignmentId);
        db.Set<ConfigurationAssignment>().Remove(assignment);
        audit.Record(
            "configuration.assignment_removed",
            nameof(ConfigurationPolicy),
            assignment.PolicyId,
            $"Assignment to {assignment.TargetType} {assignment.TargetId?.ToString() ?? string.Empty} removed (removal behaviour: {assignment.RemovalBehavior}).",
            AssignmentDto.From(assignment));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DeviceConflictsDto>> ListConflictsAsync(bool includeResolved, CancellationToken cancellationToken)
    {
        var snapshot = await targeting.LoadAsync(cancellationToken);
        var policies = await engine.LoadPoliciesAsync(cancellationToken);
        var result = new List<DeviceConflictsDto>();
        foreach (var device in snapshot.Devices.OrderBy(d => d.Hostname, StringComparer.OrdinalIgnoreCase))
        {
            var effective = EffectiveConfigurationResolver.Resolve(device, snapshot, policies);
            var conflicts = effective.Conflicts.Where(c => includeResolved || !c.IsResolved).Select(ToDto).ToList();
            if (conflicts.Count > 0)
            {
                result.Add(new DeviceConflictsDto(device.Id, device.Hostname, conflicts));
            }
        }

        return result;
    }

    public async Task<DeviceConfigurationDto> GetDeviceConfigurationAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var snapshot = await targeting.LoadAsync(cancellationToken, deviceId);
        if (!snapshot.ById.TryGetValue(deviceId, out var device))
        {
            throw new NotFoundException("Device", deviceId);
        }

        var effective = EffectiveConfigurationResolver.Resolve(device, snapshot, await engine.LoadPoliciesAsync(cancellationToken));
        var states = await db.Set<DeviceConfigurationState>().AsNoTracking()
            .Where(s => s.DeviceId == deviceId).ToDictionaryAsync(s => s.SettingKey, StringComparer.Ordinal, cancellationToken);
        var effectiveKeys = effective.Settings.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);

        return new DeviceConfigurationDto(
            deviceId,
            device.Hostname,
            effective.Settings.Select(s =>
            {
                var state = states.GetValueOrDefault(s.Key);
                var current = state?.ExpectedHash == s.Hash ? state : null;
                return new EffectiveSettingDto(
                    s.Key, s.Setting.Describe(), s.PolicyId, s.PolicyName, s.VersionNumber, s.Mode, s.AuthorizedRisk, s.MaintenanceWindowId,
                    current?.Status, current?.ActualValue, current?.LastEvaluatedAtUtc);
            }).ToList(),
            effective.Conflicts.Select(ToDto).ToList(),
            states.Values.Where(s => !effectiveKeys.Contains(s.SettingKey))
                .Select(s => new DeviceStateDto(s.SettingKey, s.Status, s.RemovalBehavior, s.NeedsRevert, s.ActualValue, s.Message, s.LastEvaluatedAtUtc))
                .OrderBy(s => s.Key, StringComparer.Ordinal)
                .ToList());
    }

    private static ConflictDto ToDto(SettingConflict c) => new(
        c.Key,
        c.IsResolved,
        c.Winner?.PolicyId,
        c.Reason,
        c.Candidates.Select(x => new ConflictCandidateDto(x.PolicyId, x.PolicyName, x.VersionNumber, x.AssignmentId, x.Priority, x.TargetType, x.Setting.Describe())).ToList());

    private async Task RequireOwnerForDestructiveAsync(ConfigurationPolicy policy, CancellationToken cancellationToken)
    {
        if (policy.ActiveVersionId is not { } activeId || currentUser.Role == TenantRole.Owner)
        {
            return;
        }

        var risk = await db.Set<ConfigurationPolicyVersion>().Where(v => v.Id == activeId).Select(v => v.AuthorizedRisk ?? v.MaxRisk).FirstAsync(cancellationToken);
        if (risk >= SettingRisk.Destructive)
        {
            throw new ForbiddenException("Enforcing a policy with destructive settings on more devices requires a workspace Owner.");
        }
    }

    private async Task ValidateWindowAsync(Guid? windowId, CancellationToken cancellationToken)
    {
        if (windowId is { } id && !await db.Set<MaintenanceWindow>().AnyAsync(w => w.Id == id, cancellationToken))
        {
            throw new ValidationException("maintenanceWindowId", "Maintenance window not found.");
        }
    }

    private async Task EnsureNoOpenDraftAsync(Guid policyId, CancellationToken cancellationToken)
    {
        var open = await db.Set<ConfigurationPolicyVersion>()
            .Where(v => v.PolicyId == policyId && (v.Status == PolicyVersionStatus.Draft || v.Status == PolicyVersionStatus.PendingApproval || v.Status == PolicyVersionStatus.Approved))
            .Select(v => new { v.Number, v.Status })
            .FirstOrDefaultAsync(cancellationToken);
        if (open is not null)
        {
            throw new ConflictException($"Version {open.Number} is still {open.Status}; publish, reject or edit it instead of creating another version.");
        }
    }

    private async Task EnsureUniqueNameAsync(string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        var trimmed = name.Trim();
        if (await db.Set<ConfigurationPolicy>().AnyAsync(p => p.Name == trimmed && p.Id != exceptId, cancellationToken))
        {
            throw new ConflictException($"A configuration policy named '{trimmed}' already exists.");
        }
    }

    private async Task<ConfigurationPolicy> FindPolicyAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Set<ConfigurationPolicy>().FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
        ?? throw new NotFoundException("ConfigurationPolicy", id);

    private async Task<ConfigurationPolicyVersion> FindVersionAsync(Guid policyId, int number, bool tracked, CancellationToken cancellationToken)
    {
        var query = db.Set<ConfigurationPolicyVersion>().Where(v => v.PolicyId == policyId && v.Number == number);
        if (!tracked)
        {
            query = query.AsNoTracking();
        }

        return await query.FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("ConfigurationPolicyVersion", $"{policyId}/v{number}");
    }
}
