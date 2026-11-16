namespace Solvix.Modules.Billing.Application;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Solvix.Modules.Billing.Configuration;
using Solvix.Modules.Billing.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Billing;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Telemetry;
using Solvix.SharedKernel.Tenancy;

/// <summary>
/// Periodic billing work: expire lapsed trials/subscriptions, cancel at period end, issue renewal invoices,
/// record usage. Idempotent by construction (unique renewal index, optimistic concurrency, upserts), so running
/// it on several instances or twice in a row is safe.
/// </summary>
public interface IBillingJobRunner
{
    Task<int> RunAsync(CancellationToken cancellationToken);
}

internal sealed partial class BillingJobRunner(
    IServiceScopeFactory rootScopes,
    ITenantScopeFactory tenantScopes,
    ILogger<BillingJobRunner> logger) : IBillingJobRunner
{
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        List<Guid> tenantIds;
        await using (var scope = rootScopes.CreateAsyncScope())
        {
            // The only cross-tenant read: which tenants have a subscription that still needs attention.
            using var platform = scope.ServiceProvider.GetRequiredService<ITenantContextMutator>().BeginPlatformScope();
            var db = scope.ServiceProvider.GetRequiredService<ISolvixDbContext>();
            tenantIds = await db.Set<Subscription>().IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trialing)
                .Select(s => s.TenantId)
                .ToListAsync(cancellationToken);
        }

        var processed = 0;
        foreach (var tenantId in tenantIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var scope = tenantScopes.CreateScope(tenantId);
                await scope.ServiceProvider.GetRequiredService<TenantBillingJob>().ProcessAsync(cancellationToken);
                processed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One tenant's failure must never block the others.
                SolvixMetrics.BillingJobFailures.Add(1);
                LogTenantFailed(logger, ex, tenantId);
            }
        }

        LogRun(logger, processed, tenantIds.Count);
        return processed;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Billing job failed for tenant {TenantId}")]
    private static partial void LogTenantFailed(ILogger logger, Exception exception, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Billing job processed {Processed} of {Total} subscriptions")]
    private static partial void LogRun(ILogger logger, int processed, int total);
}

/// <summary>The per-tenant part of <see cref="BillingJobRunner"/>; always runs inside a single-tenant scope.</summary>
internal sealed class TenantBillingJob(
    ISolvixDbContext db,
    IDeviceUsageReader deviceUsage,
    InvoiceFactory invoices,
    UsageRecorder usageRecorder,
    IOptions<BillingOptions> options,
    IClock clock)
{
    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
        var subscription = await db.Set<Subscription>().FirstOrDefaultAsync(cancellationToken);
        if (subscription is null)
        {
            return;
        }

        var settings = options.Value;
        var now = clock.UtcNow;
        var grace = TimeSpan.FromDays(settings.GracePeriodDays);
        var tenantId = subscription.TenantId;
        var renew = false;

        if (subscription.Status == SubscriptionStatus.Trialing && now >= (subscription.TrialEndsAtUtc ?? subscription.CurrentPeriodEndUtc))
        {
            subscription.MarkExpired();
            db.Set<SubscriptionEvent>().Add(SubscriptionEvent.Create(tenantId, SubscriptionEventType.Expired, ActorKind.System, null, "trial ended", now));
        }
        else if (subscription.Status == SubscriptionStatus.Active)
        {
            if (now >= subscription.CurrentPeriodEndUtc && subscription.CancelAtPeriodEnd)
            {
                subscription.MarkCancelled(now);
                db.Set<SubscriptionEvent>().Add(SubscriptionEvent.Create(tenantId, SubscriptionEventType.Cancelled, ActorKind.System, null, "cancelled at period end", now));
            }
            else if (now >= subscription.CurrentPeriodEndUtc + grace)
            {
                subscription.MarkExpired();
                db.Set<SubscriptionEvent>().Add(SubscriptionEvent.Create(tenantId, SubscriptionEventType.Expired, ActorKind.System, null, "renewal not paid within the grace period", now));
            }
            else if (!subscription.CancelAtPeriodEnd && now >= subscription.CurrentPeriodEndUtc - TimeSpan.FromDays(settings.RenewalLeadDays))
            {
                renew = true;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        if (renew)
        {
            // Last tracked-state change: a lost race on the unique renewal index leaves nothing else to save.
            await EnsureRenewalAsync(subscription, now, cancellationToken);
        }

        var used = await deviceUsage.CountBillableDevicesAsync(cancellationToken);
        await usageRecorder.RecordAsync(tenantId, used, subscription.DeviceLimit, cancellationToken);
    }

    private async Task EnsureRenewalAsync(Subscription subscription, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var tenantId = subscription.TenantId;
        var periodStart = subscription.CurrentPeriodEndUtc;
        if (await db.Set<Invoice>().AnyAsync(i => i.Purpose == InvoicePurpose.Renewal && i.PeriodStartUtc == periodStart && i.Status != InvoiceStatus.Void, cancellationToken))
        {
            return;
        }

        var plan = await db.Set<Plan>().AsNoTracking().FirstAsync(p => p.Id == subscription.PlanId, cancellationToken);
        var interval = subscription.Interval;
        var limit = subscription.DeviceLimit;
        var unit = subscription.UnitPriceMinor;

        if (subscription.PendingPlanId is { } pendingId)
        {
            var pending = await db.Set<Plan>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == pendingId && p.IsActive, cancellationToken);
            var pendingInterval = subscription.PendingInterval ?? interval;
            var pendingPrice = pending?.PriceFor(pendingInterval);
            var used = await deviceUsage.CountBillableDevicesAsync(cancellationToken);
            if (pending?.DeviceLimit is { } pendingLimit && pendingPrice is { } price && used <= pendingLimit)
            {
                plan = pending;
                interval = pendingInterval;
                limit = pendingLimit;
                unit = price;
            }
            else
            {
                subscription.ClearPending();
                db.Set<SubscriptionEvent>().Add(SubscriptionEvent.Create(tenantId, SubscriptionEventType.PlanChangeDropped, ActorKind.System, null, "scheduled plan change no longer applicable (fleet too large or plan unavailable)", now));
            }
        }

        if (unit == 0)
        {
            // Negotiated zero-price terms renew without an invoice.
            if (now >= subscription.CurrentPeriodEndUtc)
            {
                subscription.ExtendPeriod(plan.Id, plan.Code, interval, limit, unit, plan.Currency);
                db.Set<SubscriptionEvent>().Add(SubscriptionEvent.Create(tenantId, SubscriptionEventType.Renewed, ActorKind.System, null, "zero-price renewal", now));
            }

            return;
        }

        var periodEnd = Subscription.AddInterval(periodStart, interval);
        var lines = new List<InvoiceLine> { InvoiceLine.Create(InvoiceFactory.PeriodLine(plan, interval, periodStart, periodEnd), 1, unit) };
        var due = periodStart > now ? periodStart : now.AddDays(settings.InvoiceDueDays);
        try
        {
            await db.InTransactionAsync(
                async ct =>
                {
                    var invoice = await invoices.IssueAsync(tenantId, InvoicePurpose.Renewal, plan, interval, limit, unit, lines, periodStart, periodEnd, due, ct);
                    db.Set<SubscriptionEvent>().Add(SubscriptionEvent.Create(tenantId, SubscriptionEventType.InvoiceIssued, ActorKind.System, null, $"{invoice.Number} renewal", now));
                    await db.SaveChangesAsync(ct);
                    return invoice.Id;
                },
                cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another instance issued it first.
        }
    }
}
