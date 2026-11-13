namespace Solvix.Modules.EndpointManagement.Application.Configuration;

using Solvix.Endpoint.Contracts;
using Solvix.Endpoint.Contracts.Configuration;
using Solvix.Modules.EndpointManagement.Application.Targeting;
using Solvix.Modules.EndpointManagement.Domain.Configuration;
using Solvix.Modules.EndpointManagement.Domain.Targeting;

public sealed record AssignmentInput(
    Guid AssignmentId,
    TargetRef Target,
    EnforcementMode Mode,
    int Priority,
    Guid? MaintenanceWindowId,
    RemovalBehavior RemovalBehavior);

/// <summary>A policy with its published version, as input to resolution.</summary>
public sealed record PolicyInput(
    Guid PolicyId,
    string PolicyName,
    Guid VersionId,
    int VersionNumber,
    SettingRisk AuthorizedRisk,
    IReadOnlyList<ConfigurationSetting> Settings,
    IReadOnlyList<AssignmentInput> Assignments);

/// <summary>A setting as it applies to one device, with the assignment that brought it there.</summary>
public sealed record EffectiveSetting(
    string Key,
    ConfigurationSetting Setting,
    string Hash,
    SettingRisk AuthorizedRisk,
    EnforcementMode Mode,
    Guid? MaintenanceWindowId,
    RemovalBehavior RemovalBehavior,
    Guid PolicyId,
    string PolicyName,
    Guid VersionId,
    int VersionNumber,
    Guid AssignmentId,
    int Priority,
    TargetType TargetType);

/// <summary>
/// Two or more policies set <see cref="Key"/> to different values on one device.
/// Resolved conflicts have a <see cref="Winner"/> (lower priority, then more
/// specific target); unresolved ones (same priority and specificity) apply
/// nothing until an administrator changes a priority or an assignment.
/// </summary>
public sealed record SettingConflict(
    string Key,
    bool IsResolved,
    EffectiveSetting? Winner,
    IReadOnlyList<EffectiveSetting> Candidates,
    string Reason);

public sealed record EffectiveConfiguration(
    Guid DeviceId,
    IReadOnlyList<EffectiveSetting> Settings,
    IReadOnlyList<SettingConflict> Conflicts)
{
    public IEnumerable<string> UnresolvedKeys => Conflicts.Where(c => !c.IsResolved).Select(c => c.Key);
}

/// <summary>
/// Computes the effective configuration of devices from policy assignments.
/// Rules:
/// <list type="number">
/// <item>A policy applies to a device when an inclusion assignment matches it and no exclusion does.</item>
/// <item>If several inclusions of the same policy match, the one with the lowest priority
///   (then the most specific target) supplies mode, window and removal behaviour.</item>
/// <item>Settings are grouped by key. Identical values never conflict. Different values are
///   ordered by priority (ascending) then target specificity (Device &gt; Group &gt; Department
///   &gt; Branch &gt; All); the first wins unless the runner-up with a different value has the same
///   priority and specificity, in which case the conflict is unresolved and the key is withheld.</item>
/// </list>
/// </summary>
public static class EffectiveConfigurationResolver
{
    public static EffectiveConfiguration Resolve(DeviceTargetInfo device, TargetingSnapshot snapshot, IReadOnlyList<PolicyInput> policies)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(policies);

        var candidates = new List<EffectiveSetting>();
        foreach (var policy in policies)
        {
            var matching = policy.Assignments.Where(a => snapshot.Matches(device, a.Target)).ToList();
            if (matching.Count == 0 || matching.Any(a => a.Target.IsExclusion))
            {
                continue;
            }

            var best = matching
                .OrderBy(a => a.Priority)
                .ThenByDescending(a => a.Target.Specificity)
                .ThenBy(a => a.AssignmentId)
                .First();

            foreach (var setting in policy.Settings)
            {
                candidates.Add(new EffectiveSetting(
                    setting.Key,
                    setting,
                    ContractJson.Hash(setting),
                    policy.AuthorizedRisk,
                    best.Mode,
                    best.MaintenanceWindowId,
                    best.RemovalBehavior,
                    policy.PolicyId,
                    policy.PolicyName,
                    policy.VersionId,
                    policy.VersionNumber,
                    best.AssignmentId,
                    best.Priority,
                    best.Target.Type));
            }
        }

        var settings = new List<EffectiveSetting>();
        var conflicts = new List<SettingConflict>();
        foreach (var group in candidates.GroupBy(c => c.Key, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var ordered = group
                .OrderBy(c => c.Priority)
                .ThenByDescending(c => (int)c.TargetType)
                .ThenBy(c => c.PolicyName, StringComparer.Ordinal)
                .ThenBy(c => c.PolicyId)
                .ToList();
            var top = ordered[0];
            var rival = ordered.FirstOrDefault(c => c.Hash != top.Hash);
            if (rival is null)
            {
                settings.Add(top);
                continue;
            }

            if (rival.Priority == top.Priority && rival.TargetType == top.TargetType)
            {
                conflicts.Add(new SettingConflict(
                    group.Key,
                    IsResolved: false,
                    Winner: null,
                    ordered,
                    $"'{top.PolicyName}' and '{rival.PolicyName}' set different values with the same priority ({top.Priority}) and target specificity ({top.TargetType}); the setting is not applied until the conflict is resolved."));
                continue;
            }

            settings.Add(top);
            conflicts.Add(new SettingConflict(
                group.Key,
                IsResolved: true,
                top,
                ordered,
                rival.Priority != top.Priority
                    ? $"'{top.PolicyName}' wins with priority {top.Priority} over '{rival.PolicyName}' ({rival.Priority})."
                    : $"'{top.PolicyName}' wins with the more specific target ({top.TargetType} over {rival.TargetType})."));
        }

        return new EffectiveConfiguration(device.Id, settings, conflicts);
    }
}
