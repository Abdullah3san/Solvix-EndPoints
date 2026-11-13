namespace Solvix.Modules.Mdm.Application.Compliance;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Mdm.Application.Devices;
using Solvix.Modules.Mdm.Domain;
using Solvix.Modules.Mdm.Providers;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Persistence;

public interface IComplianceService
{
    /// <summary>Re-evaluates one device and records a change event when the verdict moves.</summary>
    Task<ComplianceResult> EvaluateAsync(MobileDevice device, CancellationToken cancellationToken);

    Task<int> EvaluateAllAsync(CancellationToken cancellationToken);

    Task<ComplianceSummaryDto> GetSummaryAsync(CancellationToken cancellationToken);
}

internal sealed class ComplianceService(ISolvixDbContext db, IClock clock) : IComplianceService
{
    public async Task<ComplianceResult> EvaluateAsync(MobileDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        MobilePolicy? policy = null;
        IReadOnlyList<PolicyAppSpec> apps = [];
        if (device.PolicyId is { } policyId)
        {
            policy = await db.Set<MobilePolicy>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == policyId, cancellationToken);
            apps = await LoadAppsAsync(policyId, cancellationToken);
        }

        var now = clock.UtcNow;
        var previous = device.Compliance;
        var result = ComplianceEvaluator.Evaluate(device, policy, apps, now);
        device.SetCompliance(result.State, result.Violations, now);
        if (previous != result.State && (previous != ComplianceState.Unknown || result.State != ComplianceState.Unknown))
        {
            var detail = result.Violations.Count == 0 ? string.Empty : ": " + string.Join(" ", result.Violations.Select(v => v.Message));
            DeviceLifecycle.Record(db, device, DeviceEventType.ComplianceChanged, $"Compliance changed from {previous} to {result.State}{detail}");
        }

        return result;
    }

    public async Task<int> EvaluateAllAsync(CancellationToken cancellationToken)
    {
        var devices = await db.Set<MobileDevice>().Where(d => d.State == DeviceState.Enrolled || d.State == DeviceState.RemovalPending).ToListAsync(cancellationToken);
        foreach (var device in devices)
        {
            await EvaluateAsync(device, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return devices.Count;
    }

    public async Task<ComplianceSummaryDto> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var active = await db.Set<MobileDevice>().AsNoTracking()
            .Where(d => d.State == DeviceState.Enrolled || d.State == DeviceState.RemovalPending)
            .Select(d => new { d.Platform, d.Ownership, d.Compliance, d.ComplianceViolationsJson })
            .ToListAsync(cancellationToken);

        static ComplianceBucketDto Bucket(string key, IEnumerable<ComplianceState> states)
        {
            var list = states.ToList();
            return new ComplianceBucketDto(
                key, list.Count, list.Count(s => s == ComplianceState.Compliant), list.Count(s => s == ComplianceState.NonCompliant), list.Count(s => s == ComplianceState.Unknown));
        }

        var violations = active
            .Where(d => d.Compliance == ComplianceState.NonCompliant)
            .SelectMany(d => System.Text.Json.JsonSerializer.Deserialize<List<ComplianceViolation>>(d.ComplianceViolationsJson, System.Text.Json.JsonSerializerOptions.Web) ?? [])
            .GroupBy(v => v.Rule)
            .Select(g => new ViolationCountDto(g.Key, g.Count()))
            .OrderByDescending(v => v.Devices).ThenBy(v => v.Rule)
            .ToList();

        var pending = await db.Set<DeviceCommand>().CountAsync(c => c.State == CommandState.PendingApproval, cancellationToken);
        return new ComplianceSummaryDto(
            active.Count,
            active.Count(d => d.Compliance == ComplianceState.Compliant),
            active.Count(d => d.Compliance == ComplianceState.NonCompliant),
            active.Count(d => d.Compliance == ComplianceState.Unknown),
            active.GroupBy(d => d.Platform).Select(g => Bucket(g.Key.ToString(), g.Select(d => d.Compliance))).OrderBy(b => b.Key).ToList(),
            active.GroupBy(d => d.Ownership).Select(g => Bucket(g.Key.ToString(), g.Select(d => d.Compliance))).OrderBy(b => b.Key).ToList(),
            violations,
            pending,
            clock.UtcNow);
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
