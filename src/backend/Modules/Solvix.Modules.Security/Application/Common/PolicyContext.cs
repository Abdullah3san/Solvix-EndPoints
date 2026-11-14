namespace Solvix.Modules.Security.Application.Common;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Security.Domain;
using Solvix.Modules.Security.Evaluation;
using Solvix.SharedKernel.Persistence;

/// <summary>
/// The policy that governs evaluation right now. When no policy has been approved the built-in baseline applies,
/// which is detect-only: nothing is ever changed on a device without an approved policy that says so.
/// </summary>
public sealed record EffectivePolicy(
    Guid? PolicyId,
    int? Version,
    string Name,
    bool IsBuiltIn,
    PolicySettings Settings,
    IReadOnlyList<AdvisoryRule> Advisories);

public interface IPolicyContext
{
    Task<EffectivePolicy> GetAsync(CancellationToken cancellationToken);

    void Invalidate();
}

internal sealed class PolicyContext(ISolvixDbContext db) : IPolicyContext
{
    private EffectivePolicy? _cached;

    public async Task<EffectivePolicy> GetAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        var active = await db.Set<SecurityPolicy>().AsNoTracking().FirstOrDefaultAsync(p => p.Status == PolicyStatus.Active, cancellationToken);
        var advisories = await db.Set<SoftwareAdvisory>().AsNoTracking().Where(a => a.IsEnabled).ToListAsync(cancellationToken);
        var rules = advisories.Select(a => a.ToRule()).ToList();
        _cached = active is null
            ? new EffectivePolicy(null, null, "Built-in baseline (detect only)", true, PolicySettings.Default, rules)
            : new EffectivePolicy(active.Id, active.Version, active.Name, false, active.Settings, rules);
        return _cached;
    }

    public void Invalidate() => _cached = null;
}
