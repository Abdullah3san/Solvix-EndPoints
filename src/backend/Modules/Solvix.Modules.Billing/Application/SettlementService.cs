namespace Solvix.Modules.Billing.Application;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Modules.Billing.Configuration;
using Solvix.Modules.Billing.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Telemetry;

public enum SettlementOutcome
{
    Settled = 1,
    AlreadySettled = 2,
    InvoiceVoid = 3,
    AmountMismatch = 4,
}

/// <summary>
/// Applies a confirmed payment to an invoice and its subscription. Callers own the transaction and the
/// tenant scope: this always runs inside a scope bound to exactly one tenant (RLS enforced).
/// The invoice terms snapshot, not any request data, decides what the customer receives.
/// </summary>
internal sealed partial class SettlementService(
    ISolvixDbContext db,
    IOptions<BillingOptions> options,
    IClock clock,
    ILogger<SettlementService> logger)
{
    public async Task<SettlementOutcome> SettleAsync(
        Guid invoiceId,
        string provider,
        string reference,
        long amountMinor,
        string currency,
        ActorKind actor,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var invoice = await db.Set<Invoice>().FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken) ?? throw new NotFoundException("Invoice", invoiceId);
        var tenantId = invoice.TenantId;

        if (invoice.Status == InvoiceStatus.Paid)
        {
            if (!string.Equals(invoice.PaymentReference, reference, StringComparison.Ordinal))
            {
                // A second, different payment for an already paid invoice: record it so finance can refund it.
                await RecordAsync(tenantId, invoice.Id, provider, reference, amountMinor, currency, now, cancellationToken);
                LogDuplicatePayment(logger, invoice.Id);
                SolvixMetrics.PaymentMismatches.Add(1, new KeyValuePair<string, object?>("reason", "duplicate"));
            }

            return SettlementOutcome.AlreadySettled;
        }

        if (amountMinor != invoice.TotalMinor || !string.Equals(currency, invoice.Currency, StringComparison.OrdinalIgnoreCase))
        {
            LogMismatch(logger, invoice.Id, amountMinor, invoice.TotalMinor);
            SolvixMetrics.PaymentMismatches.Add(1, new KeyValuePair<string, object?>("reason", "amount"));
            return SettlementOutcome.AmountMismatch;
        }

        if (invoice.Status == InvoiceStatus.Void)
        {
            await RecordAsync(tenantId, invoice.Id, provider, reference, amountMinor, currency, now, cancellationToken);
            LogVoidPayment(logger, invoice.Id);
            SolvixMetrics.PaymentMismatches.Add(1, new KeyValuePair<string, object?>("reason", "void_invoice"));
            return SettlementOutcome.InvoiceVoid;
        }

        invoice.MarkPaid(amountMinor, reference, now);
        await RecordAsync(tenantId, invoice.Id, provider, reference, amountMinor, currency, now, cancellationToken);
        SolvixMetrics.PaymentsSucceeded.Add(1, new KeyValuePair<string, object?>("provider", provider));

        var subscription = await db.Set<Subscription>().FirstOrDefaultAsync(cancellationToken);
        if (subscription is null)
        {
            throw new InvalidOperationException($"Tenant {tenantId} has no subscription to apply invoice {invoice.Number} to.");
        }

        if (subscription.Status == SubscriptionStatus.Suspended)
        {
            // Money received for a suspended workspace never lifts the suspension: an operator decides.
            db.Set<SubscriptionEvent>().Add(SubscriptionEvent.Create(tenantId, SubscriptionEventType.PaymentFailed, actor, null, $"{invoice.Number} paid while suspended; subscription unchanged", now));
            return SettlementOutcome.Settled;
        }

        var grace = TimeSpan.FromDays(options.Value.GracePeriodDays);
        var contiguous = invoice.Purpose == InvoicePurpose.Renewal
            && subscription.Status == SubscriptionStatus.Active
            && invoice.PeriodStartUtc == subscription.CurrentPeriodEndUtc
            && now <= subscription.CurrentPeriodEndUtc + grace;

        if (contiguous)
        {
            subscription.ExtendPeriod(invoice.TermsPlanId, invoice.TermsPlanCode, invoice.TermsInterval, invoice.TermsDeviceLimit, invoice.TermsUnitPriceMinor, invoice.Currency);
        }
        else
        {
            subscription.StartPaidPeriod(invoice.TermsPlanId, invoice.TermsPlanCode, invoice.TermsInterval, invoice.TermsDeviceLimit, invoice.TermsUnitPriceMinor, invoice.Currency, now);
            var superseded = await db.Set<Invoice>().Where(i => i.Status == InvoiceStatus.Open && i.Id != invoice.Id).ToListAsync(cancellationToken);
            foreach (var other in superseded)
            {
                other.Void(now);
            }
        }

        var type = invoice.Purpose switch
        {
            InvoicePurpose.Renewal => SubscriptionEventType.Renewed,
            InvoicePurpose.PlanChange => SubscriptionEventType.PlanChanged,
            _ => SubscriptionEventType.Activated,
        };
        db.Set<SubscriptionEvent>().Add(SubscriptionEvent.Create(tenantId, type, actor, null, $"{invoice.Number}: {invoice.TermsPlanCode} ({invoice.TermsInterval}), {invoice.TermsDeviceLimit} devices", now));
        return SettlementOutcome.Settled;
    }

    private async Task RecordAsync(Guid tenantId, Guid invoiceId, string provider, string reference, long amountMinor, string currency, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var exists = await db.Set<Payment>().AnyAsync(p => p.Provider == provider && p.ProviderReference == reference && p.Status == PaymentStatus.Succeeded, cancellationToken);
        if (!exists)
        {
            db.Set<Payment>().Add(Payment.Record(tenantId, invoiceId, provider, reference, amountMinor, currency.ToUpperInvariant(), PaymentStatus.Succeeded, null, now));
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invoice {InvoiceId} was already paid; an additional payment was recorded and needs a refund review")]
    private static partial void LogDuplicatePayment(ILogger logger, Guid invoiceId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Payment for invoice {InvoiceId} rejected: paid {Paid} but the invoice total is {Total}")]
    private static partial void LogMismatch(ILogger logger, Guid invoiceId, long paid, long total);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Payment received for void invoice {InvoiceId}; recorded for manual review")]
    private static partial void LogVoidPayment(ILogger logger, Guid invoiceId);
}
