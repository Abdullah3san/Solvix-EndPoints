namespace Solvix.Modules.EndpointManagement.Application.Configuration;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvix.Endpoint.Contracts;
using Solvix.Endpoint.Contracts.Agent;
using Solvix.Endpoint.Contracts.Configuration;
using Solvix.Modules.EndpointManagement.Application.Targeting;
using Solvix.Modules.EndpointManagement.Domain.Configuration;
using Solvix.Modules.EndpointManagement.Domain.MaintenanceWindows;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Auditing;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public interface IConfigurationEngine
{
    /// <summary>Published versions of all non-archived policies with their assignments.</summary>
    Task<IReadOnlyList<PolicyInput>> LoadPoliciesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Builds the device's desired-state document. With <paramref name="persist"/> the
    /// per-key state rows are updated (desired / conflicted / no longer desired) so
    /// compliance and revert tracking follow the document the agent actually received.
    /// </summary>
    Task<ConfigurationDocument> BuildDocumentAsync(Guid deviceId, bool persist, CancellationToken cancellationToken);

    Task IngestReportAsync(Guid deviceId, ConfigurationReport report, CancellationToken cancellationToken);
}

internal sealed class ConfigurationEngine(
    ISolvixDbContext db,
    ITargetingService targeting,
    ITenantContext tenantContext,
    IClock clock,
    IAuditTrail audit) : IConfigurationEngine
{
    public const int MaxReportResults = 2000;

    public async Task<IReadOnlyList<PolicyInput>> LoadPoliciesAsync(CancellationToken cancellationToken)
    {
        var rows = await (
                from p in db.Set<ConfigurationPolicy>().AsNoTracking()
                join v in db.Set<ConfigurationPolicyVersion>().AsNoTracking() on p.ActiveVersionId equals v.Id
                where !p.IsArchived && v.Status == PolicyVersionStatus.Published
                select new { p.Id, p.Name, Version = v })
            .ToListAsync(cancellationToken);
        var policyIds = rows.Select(r => r.Id).ToList();
        var assignments = await db.Set<ConfigurationAssignment>().AsNoTracking()
            .Where(a => policyIds.Contains(a.PolicyId))
            .ToListAsync(cancellationToken);
        var byPolicy = assignments.ToLookup(a => a.PolicyId);

        return rows
            .Select(r => new PolicyInput(
                r.Id,
                r.Name,
                r.Version.Id,
                r.Version.Number,
                r.Version.AuthorizedRisk ?? r.Version.MaxRisk,
                r.Version.GetSettings(),
                byPolicy[r.Id].Select(a => new AssignmentInput(a.Id, a.ToRef(), a.Mode, a.Priority, a.MaintenanceWindowId, a.RemovalBehavior)).ToList()))
            .ToList();
    }

    public async Task<ConfigurationDocument> BuildDocumentAsync(Guid deviceId, bool persist, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var snapshot = await targeting.LoadAsync(cancellationToken, deviceId);
        var states = await db.Set<DeviceConfigurationState>().Where(s => s.DeviceId == deviceId).ToListAsync(cancellationToken);
        if (!snapshot.ById.TryGetValue(deviceId, out var device))
        {
            return Document(now, [], [], []);
        }

        var effective = EffectiveConfigurationResolver.Resolve(device, snapshot, await LoadPoliciesAsync(cancellationToken));
        var windowIds = effective.Settings.Where(s => s.MaintenanceWindowId is not null).Select(s => s.MaintenanceWindowId!.Value).Distinct().ToList();
        var windows = await db.Set<MaintenanceWindow>().AsNoTracking().Where(w => windowIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, cancellationToken);

        var desired = effective.Settings
            .Select(s => new DesiredSetting(
                s.Key,
                s.Setting,
                s.AuthorizedRisk,
                s.Mode,
                ApplyAllowed: s.Mode == EnforcementMode.Enforce
                    && (s.MaintenanceWindowId is not { } w || (windows.TryGetValue(w, out var window) && window.IsOpenAt(now))),
                s.PolicyId,
                s.VersionId,
                s.VersionNumber,
                s.AssignmentId,
                s.Hash))
            .ToList();
        var desiredKeys = desired.Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        var conflicted = effective.UnresolvedKeys.ToHashSet(StringComparer.Ordinal);

        if (persist)
        {
            var byKey = states.ToDictionary(s => s.SettingKey, StringComparer.Ordinal);
            var tenantId = tenantContext.RequireTenantId();
            foreach (var s in effective.Settings)
            {
                if (!byKey.TryGetValue(s.Key, out var state))
                {
                    state = new DeviceConfigurationState(tenantId, deviceId, s.Key);
                    db.Set<DeviceConfigurationState>().Add(state);
                    byKey[s.Key] = state;
                    states.Add(state);
                }

                state.MarkDesired(s.PolicyId, s.VersionId, s.AssignmentId, s.Mode, s.RemovalBehavior, s.Hash);
            }

            foreach (var state in states.Where(s => !desiredKeys.Contains(s.SettingKey)))
            {
                if (conflicted.Contains(state.SettingKey))
                {
                    state.MarkConflicted();
                }
                else
                {
                    state.MarkNotDesired();
                }
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        // Computed identically with and without persistence so the heartbeat revision matches the document.
        var revertKeys = states
            .Where(s => !desiredKeys.Contains(s.SettingKey) && !conflicted.Contains(s.SettingKey)
                && s.RemovalBehavior == RemovalBehavior.Revert && s.WasChangedBySolvix
                && s.Status is not (SettingComplianceStatus.Reverted or SettingComplianceStatus.RevertFailed or SettingComplianceStatus.RevertUnsupported))
            .Select(s => s.SettingKey)
            .Order(StringComparer.Ordinal)
            .ToList();

        return Document(now, desired, revertKeys, conflicted.Order(StringComparer.Ordinal).ToList());
    }

    public async Task IngestReportAsync(Guid deviceId, ConfigurationReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        var tenantId = tenantContext.RequireTenantId();
        var results = report.Results.Take(MaxReportResults).ToList();
        var keys = results.Select(r => r.Key).Distinct(StringComparer.Ordinal).ToList();
        var states = await db.Set<DeviceConfigurationState>()
            .Where(s => s.DeviceId == deviceId && keys.Contains(s.SettingKey))
            .ToDictionaryAsync(s => s.SettingKey, StringComparer.Ordinal, cancellationToken);
        var now = clock.UtcNow;

        foreach (var result in results)
        {
            if (string.IsNullOrWhiteSpace(result.Key))
            {
                continue;
            }

            if (!states.TryGetValue(result.Key, out var state))
            {
                // Only keys the server sent can be reported; anything else is ignored.
                continue;
            }

            var previousActual = state.ActualValue;
            var evaluatedAt = result.EvaluatedAtUtc > now.AddMinutes(5) ? now : result.EvaluatedAtUtc;
            var changed = state.Report(result.Status, result.ExpectedValue, result.ActualValue, result.Message, result.RollbackAvailable, evaluatedAt);

            if (result.Status is SettingComplianceStatus.Drifted or SettingComplianceStatus.Remediated
                && (changed || previousActual != state.ActualValue))
            {
                db.Set<ConfigurationDriftEvent>().Add(new ConfigurationDriftEvent(
                    tenantId, deviceId, result.Key, result.PolicyVersionId ?? state.PolicyVersionId, result.Status, state.Mode,
                    result.ExpectedValue, result.ActualValue, evaluatedAt));
            }

            if (changed && result.Status is SettingComplianceStatus.Remediated or SettingComplianceStatus.Applied
                or SettingComplianceStatus.Reverted or SettingComplianceStatus.RevertFailed or SettingComplianceStatus.RevertUnsupported
                or SettingComplianceStatus.Refused or SettingComplianceStatus.Failed)
            {
                audit.Record(
                    $"configuration.setting_{ToSnake(result.Status)}",
                    "Device",
                    deviceId,
                    $"{result.Key}: {result.Status}{(result.Message is null ? string.Empty : " – " + result.Message)}",
                    new { result.Key, result.PolicyVersionId, result.ExpectedValue, result.ActualValue });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static ConfigurationDocument Document(DateTimeOffset now, IReadOnlyList<DesiredSetting> settings, IReadOnlyList<string> revertKeys, IReadOnlyList<string> conflicted)
    {
        var revision = ContractJson.Sha256(JsonSerializer.Serialize(new { settings, revertKeys, conflicted }, ContractJson.Options))[..32];
        return new ConfigurationDocument(revision, now, settings, revertKeys, conflicted);
    }

    private static string ToSnake(SettingComplianceStatus status) =>
        string.Concat(status.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}
