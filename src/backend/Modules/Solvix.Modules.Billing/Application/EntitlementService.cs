namespace Solvix.Modules.Billing.Application;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvix.Modules.Billing.Configuration;
using Solvix.Modules.Billing.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Billing;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

/// <summary>
/// The server-side source of truth for plan limits. Other modules call this on every operation that adds
/// billable resources; nothing here can be bypassed from the client.
/// </summary>
internal sealed class EntitlementService(ISolvixDbContext db, IOptions<BillingOptions> options, IClock clock) : IDeviceEntitlementProvider
{
    public async Task<DeviceEntitlement> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        // Explicit tenant filter in addition to the global filter and RLS: the id is a parameter, not ambient state.
        var subscription = await db.Set<Subscription>().AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);
        return EntitlementEvaluator.Evaluate(subscription, clock.UtcNow, TimeSpan.FromDays(options.Value.GracePeriodDays));
    }
}

/// <summary>Creates the trial subscription inside the registration transaction (platform scope, explicit tenant id).</summary>
internal sealed class SubscriptionProvisioner(ISolvixDbContext db, IOptions<BillingOptions> options, IClock clock) : ISubscriptionProvisioner
{
    public async Task StartTrialAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var plan = await db.Set<Plan>().FirstOrDefaultAsync(p => p.Code == settings.TrialPlanCode && p.IsActive, cancellationToken)
            ?? throw new InvalidOperationException($"Trial plan '{settings.TrialPlanCode}' does not exist. Check Solvix:Billing:TrialPlanCode and the seeded plans.");
        var now = clock.UtcNow;
        db.Set<Subscription>().Add(Subscription.StartTrial(tenantId, plan, now, settings.TrialDays));
        db.Set<SubscriptionEvent>().Add(SubscriptionEvent.Create(tenantId, SubscriptionEventType.TrialStarted, ActorKind.System, null, $"{plan.Code} trial, {settings.TrialDays} days", now));
    }
}
