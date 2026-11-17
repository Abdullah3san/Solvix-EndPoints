namespace Solvix.Modules.Billing.Application;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvix.Modules.Billing.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

/// <summary>
/// Platform-operator actions that tenants cannot perform themselves: negotiated (Enterprise) terms, confirming
/// an offline payment, suspension, tax rate. Every action names one tenant explicitly and runs inside a scope
/// bound to THAT tenant, so row-level security still applies; nothing here iterates across tenants.
/// </summary>
public interface IPlatformBillingService
{
    Task<SubscriptionDto> ConfigureSubscriptionAsync(Guid tenantId, OperatorConfigureSubscriptionRequest request, CancellationToken cancellationToken);

    Task<InvoiceDto> MarkInvoicePaidAsync(Guid tenantId, Guid invoiceId, OperatorMarkPaidRequest request, CancellationToken cancellationToken);

    Task SuspendAsync(Guid tenantId, OperatorSuspendRequest request, CancellationToken cancellationToken);

    Task ReactivateAsync(Guid tenantId, CancellationToken cancellationToken);

    Task SetTaxRateAsync(Guid tenantId, OperatorTaxRateRequest request, CancellationToken cancellationToken);
}

internal sealed class PlatformBillingService(ITenantScopeFactory scopes, ITenantDirectory tenants, IClock clock) : IPlatformBillingService
{
    public async Task<SubscriptionDto> ConfigureSubscriptionAsync(Guid tenantId, OperatorConfigureSubscriptionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var scope = await OpenAsync(tenantId, cancellationToken);
        var db = scope.ServiceProvider.GetRequiredService<ISolvixDbContext>();
        var code = request.PlanCode!.Trim().ToLowerInvariant();
        var plan = await db.Set<Plan>().AsNoTracking().FirstOrDefaultAsync(p => p.Code == code && p.IsActive, cancellationToken) ?? throw new NotFoundException("Plan", code);
        var limit = request.DeviceLimit ?? plan.DeviceLimit ?? throw new ValidationException("deviceLimit", "An explicit device limit is required for a custom plan.");
        var price = request.UnitPriceMinor ?? plan.PriceFor(request.Interval) ?? throw new ValidationException("unitPriceMinor", "An explicit price is required for this plan.");
        var now = clock.UtcNow;
        var end = request.PeriodEndUtc ?? Subscription.AddInterval(now, request.Interval);
        if (end <= now)
        {
            throw new ValidationException("periodEndUtc", "The period must end in the future.");
        }

        var subscription = await db.Set<Subscription>().FirstOrDefaultAsync(cancellationToken);
        if (subscription is null)
        {
            subscription = Subscription.StartTrial(tenantId, plan.IsCustom ? await FirstFixedPlanAsync(db, cancellationToken) : plan, now, 0);
            db.Set<Subscription>().Add(subscription);
        }

        subscription.ConfigureNegotiated(plan, request.Interval, limit, price, now, end);
        db.Set<SubscriptionEvent>().Add(SubscriptionEvent.Create(tenantId, SubscriptionEventType.EnterpriseConfigured, ActorKind.Operator, null, $"{plan.Code}: {limit} devices, {price} minor/period until {end:O}", now));
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(subscription, plan.Name);
    }

    public async Task<InvoiceDto> MarkInvoicePaidAsync(Guid tenantId, Guid invoiceId, OperatorMarkPaidRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var scope = await OpenAsync(tenantId, cancellationToken);
        var db = scope.ServiceProvider.GetRequiredService<ISolvixDbContext>();
        var settlement = scope.ServiceProvider.GetRequiredService<SettlementService>();

        // The invoice is looked up under this tenant's scope: an id from another tenant simply does not exist here.
        var invoice = await db.Set<Invoice>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken) ?? throw new NotFoundException("Invoice", invoiceId);
        var outcome = await db.InTransactionAsync(
            async ct =>
            {
                var result = await settlement.SettleAsync(invoiceId, "manual", $"manual:{invoiceId:N}:{request.Reference}", invoice.TotalMinor, invoice.Currency, ActorKind.Operator, ct);
                await db.SaveChangesAsync(ct);
                return result;
            },
            cancellationToken);
        if (outcome is SettlementOutcome.InvoiceVoid or SettlementOutcome.AmountMismatch)
        {
            throw new ConflictException("The invoice cannot be settled.", "invoice_not_open");
        }

        var paid = await db.Set<Invoice>().AsNoTracking().FirstAsync(i => i.Id == invoiceId, cancellationToken);
        return new InvoiceDto(paid.Id, paid.Number, paid.Status, paid.Purpose, paid.Currency, paid.SubtotalMinor, paid.TaxMinor, paid.TotalMinor, paid.IssuedAtUtc, paid.DueAtUtc, paid.PaidAtUtc, paid.PeriodStartUtc, paid.PeriodEndUtc, paid.TermsPlanCode, paid.TermsInterval);
    }

    public async Task SuspendAsync(Guid tenantId, OperatorSuspendRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var scope = await OpenAsync(tenantId, cancellationToken);
        var db = scope.ServiceProvider.GetRequiredService<ISolvixDbContext>();
        var subscription = await db.Set<Subscription>().FirstOrDefaultAsync(cancellationToken) ?? throw new NotFoundException("Subscription", tenantId);
        subscription.Suspend(request.Reason!);
        db.Set<SubscriptionEvent>().Add(SubscriptionEvent.Create(tenantId, SubscriptionEventType.Suspended, ActorKind.Operator, null, request.Reason, clock.UtcNow));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ReactivateAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var scope = await OpenAsync(tenantId, cancellationToken);
        var db = scope.ServiceProvider.GetRequiredService<ISolvixDbContext>();
        var subscription = await db.Set<Subscription>().FirstOrDefaultAsync(cancellationToken) ?? throw new NotFoundException("Subscription", tenantId);
        if (subscription.Status != SubscriptionStatus.Suspended)
        {
            throw new ConflictException("The subscription is not suspended.", "subscription_not_suspended");
        }

        subscription.Reactivate();
        db.Set<SubscriptionEvent>().Add(SubscriptionEvent.Create(tenantId, SubscriptionEventType.Reactivated, ActorKind.Operator, null, null, clock.UtcNow));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetTaxRateAsync(Guid tenantId, OperatorTaxRateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var scope = await OpenAsync(tenantId, cancellationToken);
        var db = scope.ServiceProvider.GetRequiredService<ISolvixDbContext>();
        var subscription = await db.Set<Subscription>().FirstOrDefaultAsync(cancellationToken) ?? throw new NotFoundException("Subscription", tenantId);
        subscription.SetTaxRate(request.TaxRateBasisPoints);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<AsyncServiceScope> OpenAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await tenants.FindByIdAsync(tenantId, cancellationToken) ?? throw new NotFoundException("Tenant", tenantId);
        return scopes.CreateScope(tenant.Id, tenant.Slug);
    }

    private static Task<Plan> FirstFixedPlanAsync(ISolvixDbContext db, CancellationToken cancellationToken) =>
        db.Set<Plan>().AsNoTracking().Where(p => p.DeviceLimit != null).OrderBy(p => p.SortOrder).FirstAsync(cancellationToken);

    private SubscriptionDto ToDto(Subscription s, string planName) =>
        new(s.PlanCode, planName, s.Status, s.Interval, s.DeviceLimit, s.UnitPriceMinor, s.Currency, s.TrialEndsAtUtc, s.CurrentPeriodStartUtc, s.CurrentPeriodEndUtc, null, s.CancelAtPeriodEnd, s.PendingPlanCode, s.PendingInterval, s.Status == SubscriptionStatus.Active, null, null);
}
