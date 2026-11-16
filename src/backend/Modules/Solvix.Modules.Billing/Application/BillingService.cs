namespace Solvix.Modules.Billing.Application;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvix.Modules.Billing.Configuration;
using Solvix.Modules.Billing.Domain;
using Solvix.Modules.Billing.Payments;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Billing;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Tenancy;

public interface IBillingService
{
    Task<IReadOnlyList<PlanDto>> GetPlansAsync(CancellationToken cancellationToken);

    Task<SubscriptionDto> GetSubscriptionAsync(CancellationToken cancellationToken);

    Task<UsageDto> GetUsageAsync(CancellationToken cancellationToken);

    Task<ChangePlanResult> ChangePlanAsync(ChangePlanRequest request, CancellationToken cancellationToken);

    Task<SubscriptionDto> CancelAsync(CancellationToken cancellationToken);

    Task<SubscriptionDto> ResumeAsync(CancellationToken cancellationToken);

    Task<PagedResult<InvoiceDto>> ListInvoicesAsync(InvoiceListQuery query, CancellationToken cancellationToken);

    Task<InvoiceDetailsDto> GetInvoiceAsync(Guid id, CancellationToken cancellationToken);

    Task<string> RenderInvoiceHtmlAsync(Guid id, CancellationToken cancellationToken);

    Task<CheckoutDto> CreateCheckoutAsync(Guid invoiceId, CancellationToken cancellationToken);

    Task<BillingProfileDto?> GetProfileAsync(CancellationToken cancellationToken);

    Task<BillingProfileDto> UpdateProfileAsync(UpdateBillingProfileRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<BillingHistoryItemDto>> GetHistoryAsync(CancellationToken cancellationToken);
}

internal sealed class BillingService(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IDeviceUsageReader deviceUsage,
    IPaymentProvider paymentProvider,
    InvoiceFactory invoices,
    UsageRecorder usageRecorder,
    IOptions<BillingOptions> options,
    IClock clock) : IBillingService
{
    private TimeSpan Grace => TimeSpan.FromDays(options.Value.GracePeriodDays);

    public async Task<IReadOnlyList<PlanDto>> GetPlansAsync(CancellationToken cancellationToken) =>
        await db.Set<Plan>().AsNoTracking()
            .Where(p => p.IsActive && p.IsPublic)
            .OrderBy(p => p.SortOrder)
            .Select(p => new PlanDto(p.Id, p.Code, p.Name, p.Description, p.DeviceLimit, p.MonthlyPriceMinor, p.AnnualPriceMinor, p.Currency, p.DeviceLimit == null))
            .ToListAsync(cancellationToken);

    public async Task<SubscriptionDto> GetSubscriptionAsync(CancellationToken cancellationToken)
    {
        var subscription = await db.Set<Subscription>().AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        return await ToDtoAsync(subscription, cancellationToken);
    }

    public async Task<UsageDto> GetUsageAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.RequireTenantId();
        var subscription = await db.Set<Subscription>().AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var entitlement = EntitlementEvaluator.Evaluate(subscription, clock.UtcNow, Grace);
        var used = await deviceUsage.CountBillableDevicesAsync(cancellationToken);
        var limit = subscription?.DeviceLimit ?? 0;
        if (subscription is not null)
        {
            await usageRecorder.RecordAsync(tenantId, used, limit, cancellationToken);
        }

        var since = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime).AddDays(-89);
        var history = await db.Set<UsageSnapshot>().AsNoTracking()
            .Where(s => s.Day >= since)
            .OrderBy(s => s.Day)
            .Select(s => new UsagePointDto(s.Day, s.PeakDevices, s.DeviceLimit))
            .ToListAsync(cancellationToken);
        var percent = limit == 0 ? 100 : (int)Math.Min(100, Math.Round(used * 100.0 / limit));
        var level = used >= limit ? "limit" : percent >= 90 ? "critical" : percent >= 75 ? "warning" : "ok";
        return new UsageDto(used, limit, percent, level, entitlement.IsEntitled && used < limit, history);
    }

    public async Task<ChangePlanResult> ChangePlanAsync(ChangePlanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenantId = tenantContext.RequireTenantId();
        var code = request.PlanCode!.Trim().ToLowerInvariant();
        var plan = await db.Set<Plan>().AsNoTracking().FirstOrDefaultAsync(p => p.Code == code && p.IsActive && p.IsPublic, cancellationToken)
            ?? throw new NotFoundException("Plan", code);
        if (plan.IsCustom)
        {
            throw new ConflictException("This plan has custom limits and pricing. Contact sales to set it up.", "plan_requires_sales");
        }

        var price = plan.PriceFor(request.Interval) ?? throw new ValidationException("interval", "This plan is not available for the selected billing interval.");
        var now = clock.UtcNow;

        var subscription = await db.Set<Subscription>().FirstOrDefaultAsync(cancellationToken);
        if (subscription is null)
        {
            // Fail-closed workspaces without a subscription (should not exist after registration/backfill) start lapsed.
            subscription = Subscription.StartTrial(tenantId, plan, now, 0);
            db.Set<Subscription>().Add(subscription);
        }

        var status = EntitlementEvaluator.EffectiveStatus(subscription, now, Grace);
        var limit = plan.DeviceLimit!.Value;

        if (status == SubscriptionStatus.Suspended)
        {
            throw new ConflictException("The subscription is suspended. Contact support.", "subscription_suspended");
        }

        if (status == SubscriptionStatus.Active)
        {
            var same = subscription.PlanCode == plan.Code && subscription.Interval == request.Interval;
            if (same)
            {
                if (subscription.PendingPlanId is null)
                {
                    throw new ConflictException("You are already on this plan.", "already_subscribed");
                }

                subscription.ClearPending();
                await db.SaveChangesAsync(cancellationToken);
                return new ChangePlanResult("unchanged", null, null, await ToDtoAsync(subscription, cancellationToken));
            }

            if (limit <= subscription.DeviceLimit)
            {
                // Downgrade or lateral move: applies at the end of the paid period, and only if the fleet fits.
                var used = await deviceUsage.CountBillableDevicesAsync(cancellationToken);
                if (used > limit)
                {
                    throw new ConflictException(
                        $"You currently use {used} devices; the {plan.Name} plan allows {limit}. Retire or remove devices first.",
                        "usage_exceeds_plan");
                }

                subscription.SchedulePlanChange(plan.Id, plan.Code, request.Interval);
                db.Set<SubscriptionEvent>().Add(SubscriptionEvent.Create(tenantId, SubscriptionEventType.PlanChangeScheduled, ActorKind.User, currentUser.UserId, $"{subscription.PlanCode} -> {plan.Code} ({request.Interval}) at {subscription.CurrentPeriodEndUtc:O}", now));
                await db.SaveChangesAsync(cancellationToken);
                return new ChangePlanResult("scheduled", null, null, await ToDtoAsync(subscription, cancellationToken));
            }
        }

        var upgrade = status == SubscriptionStatus.Active;
        var invoice = await db.InTransactionAsync(
            async ct =>
            {
                // A newer purchase supersedes any other unpaid purchase invoice.
                var stale = await db.Set<Invoice>()
                    .Where(i => i.Status == InvoiceStatus.Open && (i.Purpose == InvoicePurpose.NewSubscription || i.Purpose == InvoicePurpose.PlanChange))
                    .ToListAsync(ct);
                foreach (var old in stale)
                {
                    old.Void(now);
                }

                var end = Subscription.AddInterval(now, request.Interval);
                var lines = new List<InvoiceLine> { InvoiceLine.Create(InvoiceFactory.PeriodLine(plan, request.Interval, now, end), 1, price) };
                if (upgrade)
                {
                    var credit = Math.Min(price, Proration.UnusedCredit(subscription.UnitPriceMinor, subscription.CurrentPeriodStartUtc, subscription.CurrentPeriodEndUtc, now));
                    if (credit > 0)
                    {
                        lines.Add(InvoiceLine.Create($"Credit for unused time on the {subscription.PlanCode} plan", 1, -credit));
                    }
                }

                var due = now.AddDays(options.Value.InvoiceDueDays);
                var created = await invoices.IssueAsync(
                    tenantId, upgrade ? InvoicePurpose.PlanChange : InvoicePurpose.NewSubscription, plan, request.Interval, limit, price, lines, now, end, due, ct);
                db.Set<SubscriptionEvent>().Add(SubscriptionEvent.Create(tenantId, SubscriptionEventType.InvoiceIssued, ActorKind.User, currentUser.UserId, $"{created.Number} for {plan.Code} ({request.Interval})", now));
                await db.SaveChangesAsync(ct);
                return created;
            },
            cancellationToken);

        var checkout = await TryCreateCheckoutAsync(invoice, cancellationToken);
        return new ChangePlanResult("invoice_issued", ToDto(invoice), checkout?.Url, await ToDtoAsync(subscription, cancellationToken));
    }

    public async Task<SubscriptionDto> CancelAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.RequireTenantId();
        var subscription = await db.Set<Subscription>().FirstOrDefaultAsync(cancellationToken) ?? throw new NotFoundException("Subscription", tenantId);
        subscription.RequestCancellation();
        db.Set<SubscriptionEvent>().Add(SubscriptionEvent.Create(tenantId, SubscriptionEventType.CancelRequested, ActorKind.User, currentUser.UserId, $"effective {subscription.CurrentPeriodEndUtc:O}", clock.UtcNow));
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(subscription, cancellationToken);
    }

    public async Task<SubscriptionDto> ResumeAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.RequireTenantId();
        var subscription = await db.Set<Subscription>().FirstOrDefaultAsync(cancellationToken) ?? throw new NotFoundException("Subscription", tenantId);
        subscription.RevertCancellation();
        db.Set<SubscriptionEvent>().Add(SubscriptionEvent.Create(tenantId, SubscriptionEventType.CancelReverted, ActorKind.User, currentUser.UserId, null, clock.UtcNow));
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(subscription, cancellationToken);
    }

    public async Task<PagedResult<InvoiceDto>> ListInvoicesAsync(InvoiceListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var invoicesQuery = db.Set<Invoice>().AsNoTracking();
        if (query.Status is { } status)
        {
            invoicesQuery = invoicesQuery.Where(i => i.Status == status);
        }

        var total = await invoicesQuery.LongCountAsync(cancellationToken);
        var items = await invoicesQuery.OrderByDescending(i => i.IssuedAtUtc).ThenByDescending(i => i.Number)
            .Skip(page.Skip).Take(page.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<InvoiceDto>(items.Select(ToDto).ToList(), page.SafePage, page.SafePageSize, total);
    }

    public async Task<InvoiceDetailsDto> GetInvoiceAsync(Guid id, CancellationToken cancellationToken)
    {
        var invoice = await db.Set<Invoice>().AsNoTracking().Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == id, cancellationToken)
            ?? throw new NotFoundException("Invoice", id);
        return new InvoiceDetailsDto(
            ToDto(invoice),
            invoice.Lines.OrderBy(l => l.Position).Select(l => new InvoiceLineDto(l.Description, l.Quantity, l.UnitAmountMinor, l.AmountMinor)).ToList(),
            invoice.BillToName, invoice.BillToEmail, invoice.BillToAddress, invoice.BillToTaxId, invoice.TaxRateBasisPoints);
    }

    public async Task<string> RenderInvoiceHtmlAsync(Guid id, CancellationToken cancellationToken) =>
        InvoiceHtmlRenderer.Render(await GetInvoiceAsync(id, cancellationToken), options.Value.Seller);

    public async Task<CheckoutDto> CreateCheckoutAsync(Guid invoiceId, CancellationToken cancellationToken)
    {
        var invoice = await db.Set<Invoice>().FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken) ?? throw new NotFoundException("Invoice", invoiceId);
        if (invoice.Status != InvoiceStatus.Open)
        {
            throw new ConflictException("Only an open invoice can be paid.", "invoice_not_open");
        }

        if (!paymentProvider.SupportsCheckout)
        {
            throw new ConflictException("Online payment is not enabled. Pay this invoice by bank transfer and contact support with the invoice number.", "online_payment_unavailable");
        }

        var checkout = await TryCreateCheckoutAsync(invoice, cancellationToken);
        return new CheckoutDto(ToDto(invoice), checkout?.Url, paymentProvider.Name);
    }

    public async Task<BillingProfileDto?> GetProfileAsync(CancellationToken cancellationToken)
    {
        var profile = await db.Set<BillingProfile>().AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        return profile is null ? null : ToDto(profile);
    }

    public async Task<BillingProfileDto> UpdateProfileAsync(UpdateBillingProfileRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenantId = tenantContext.RequireTenantId();
        var profile = await db.Set<BillingProfile>().FirstOrDefaultAsync(cancellationToken);
        if (profile is null)
        {
            profile = BillingProfile.Create(tenantId);
            db.Set<BillingProfile>().Add(profile);
        }

        profile.Update(request.LegalName!, request.BillingEmail!, request.AddressLine1, request.AddressLine2, request.City, request.Region, request.PostalCode, request.CountryCode, request.TaxId);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(profile);
    }

    public async Task<IReadOnlyList<BillingHistoryItemDto>> GetHistoryAsync(CancellationToken cancellationToken)
    {
        var invoiceRows = await db.Set<Invoice>().AsNoTracking().OrderByDescending(i => i.IssuedAtUtc).Take(100).ToListAsync(cancellationToken);
        var paymentRows = await db.Set<Payment>().AsNoTracking().OrderByDescending(p => p.OccurredAtUtc).Take(100).ToListAsync(cancellationToken);
        var eventRows = await db.Set<SubscriptionEvent>().AsNoTracking().OrderByDescending(e => e.OccurredAtUtc).Take(100).ToListAsync(cancellationToken);

        var items = new List<BillingHistoryItemDto>();
        items.AddRange(invoiceRows.Select(i => new BillingHistoryItemDto("invoice", i.IssuedAtUtc, $"Invoice {i.Number} ({i.TermsPlanCode})", i.TotalMinor, i.Currency, i.Status.ToString(), i.Id)));
        items.AddRange(paymentRows.Select(p => new BillingHistoryItemDto("payment", p.OccurredAtUtc, p.Status == PaymentStatus.Succeeded ? "Payment received" : "Payment failed", p.AmountMinor, p.Currency, p.Status.ToString(), p.InvoiceId)));
        items.AddRange(eventRows.Select(e => new BillingHistoryItemDto("subscription", e.OccurredAtUtc, e.Type.ToString(), null, null, null, null)));
        return items.OrderByDescending(i => i.AtUtc).Take(150).ToList();
    }

    private async Task<CheckoutSessionResult?> TryCreateCheckoutAsync(Invoice invoice, CancellationToken cancellationToken)
    {
        if (!paymentProvider.SupportsCheckout)
        {
            return null;
        }

        var result = await paymentProvider.CreateCheckoutAsync(
            new CheckoutRequest(invoice.TenantId, invoice.Id, invoice.Number, $"Solvix {invoice.TermsPlanCode} - invoice {invoice.Number}", invoice.TotalMinor, invoice.Currency, invoice.BillToEmail),
            cancellationToken);
        if (result is not null)
        {
            invoice.SetCheckout(paymentProvider.Name, result.SessionId);
            await db.SaveChangesAsync(cancellationToken);
        }

        return result;
    }

    private async Task<SubscriptionDto> ToDtoAsync(Subscription? subscription, CancellationToken cancellationToken)
    {
        if (subscription is null)
        {
            var none = DeviceEntitlement.None("subscription_missing", "This workspace has no subscription.");
            var now = clock.UtcNow;
            return new SubscriptionDto(string.Empty, string.Empty, SubscriptionStatus.Expired, BillingInterval.Monthly, 0, 0, "USD", null, now, now, null, false, null, null, false, none.DenyCode, none.DenyMessage);
        }

        var planName = await db.Set<Plan>().AsNoTracking().Where(p => p.Id == subscription.PlanId).Select(p => p.Name).FirstOrDefaultAsync(cancellationToken) ?? subscription.PlanCode;
        var status = EntitlementEvaluator.EffectiveStatus(subscription, clock.UtcNow, Grace);
        var entitlement = EntitlementEvaluator.Evaluate(subscription, clock.UtcNow, Grace);
        DateTimeOffset? graceEnds = status == SubscriptionStatus.PastDue ? subscription.CurrentPeriodEndUtc + Grace : null;
        return new SubscriptionDto(
            subscription.PlanCode, planName, status, subscription.Interval, subscription.DeviceLimit, subscription.UnitPriceMinor, subscription.Currency,
            subscription.TrialEndsAtUtc, subscription.CurrentPeriodStartUtc, subscription.CurrentPeriodEndUtc, graceEnds, subscription.CancelAtPeriodEnd,
            subscription.PendingPlanCode, subscription.PendingInterval, entitlement.IsEntitled, entitlement.DenyCode, entitlement.DenyMessage);
    }

    private static InvoiceDto ToDto(Invoice i) =>
        new(i.Id, i.Number, i.Status, i.Purpose, i.Currency, i.SubtotalMinor, i.TaxMinor, i.TotalMinor, i.IssuedAtUtc, i.DueAtUtc, i.PaidAtUtc, i.PeriodStartUtc, i.PeriodEndUtc, i.TermsPlanCode, i.TermsInterval);

    private static BillingProfileDto ToDto(BillingProfile p) =>
        new(p.LegalName, p.BillingEmail, p.AddressLine1, p.AddressLine2, p.City, p.Region, p.PostalCode, p.CountryCode, p.TaxId);
}
