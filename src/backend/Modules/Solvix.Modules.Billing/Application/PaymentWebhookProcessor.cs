namespace Solvix.Modules.Billing.Application;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Solvix.Modules.Billing.Domain;
using Solvix.Modules.Billing.Payments;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Telemetry;
using Solvix.SharedKernel.Tenancy;

public enum WebhookResult
{
    Processed = 1,
    Duplicate = 2,
    Ignored = 3,
    Rejected = 4,
}

public interface IPaymentWebhookProcessor
{
    /// <summary>Authenticates and applies one provider notification. Throws <see cref="WebhookSignatureException"/> when it is not authentic.</summary>
    Task<WebhookResult> HandleAsync(string payload, string? signature, CancellationToken cancellationToken);
}

/// <summary>
/// Anonymous endpoint, so nothing in the body is trusted until the signature verifies. The tenant is never
/// taken from the payload: it is read from OUR invoice (found by id) and the signed metadata must agree.
/// Settlement then runs in a scope bound to that single tenant, with row-level security enforced.
/// </summary>
internal sealed partial class PaymentWebhookProcessor(
    ISolvixDbContext db,
    ITenantContextMutator mutator,
    ITenantScopeFactory scopeFactory,
    IPaymentProvider provider,
    IClock clock,
    ILogger<PaymentWebhookProcessor> logger) : IPaymentWebhookProcessor
{
    public async Task<WebhookResult> HandleAsync(string payload, string? signature, CancellationToken cancellationToken)
    {
        ProviderEvent evt;
        try
        {
            evt = provider.ParseWebhook(payload, signature, clock.UtcNow);
        }
        catch (WebhookSignatureException)
        {
            SolvixMetrics.WebhooksRejected.Add(1, new KeyValuePair<string, object?>("provider", provider.Name));
            throw;
        }

        if (evt.Kind == ProviderEventKind.Ignored || evt.InvoiceId is not { } invoiceId)
        {
            return await RecordOnlyAsync(evt, "ignored", cancellationToken);
        }

        Guid invoiceTenant;
        using (mutator.BeginPlatformScope())
        {
            var found = await db.Set<Invoice>().IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.Id == invoiceId)
                .Select(i => (Guid?)i.TenantId)
                .FirstOrDefaultAsync(cancellationToken);
            if (found is null)
            {
                LogUnknownInvoice(logger, evt.EventId);
                return await RecordOnlyAsync(evt, "unknown_invoice", cancellationToken);
            }

            invoiceTenant = found.Value;
        }

        if (evt.TenantId is { } claimed && claimed != invoiceTenant)
        {
            LogTenantMismatch(logger, evt.EventId);
            SolvixMetrics.PaymentMismatches.Add(1, new KeyValuePair<string, object?>("reason", "tenant"));
            return await RecordOnlyAsync(evt, "tenant_mismatch", cancellationToken);
        }

        await using var scope = scopeFactory.CreateScope(invoiceTenant);
        var scoped = scope.ServiceProvider.GetRequiredService<ISolvixDbContext>();
        var settlement = scope.ServiceProvider.GetRequiredService<SettlementService>();

        try
        {
            return await scoped.InTransactionAsync(
                async ct =>
                {
                    if (await scoped.Set<WebhookEvent>().AnyAsync(e => e.Provider == provider.Name && e.EventId == evt.EventId, ct))
                    {
                        return WebhookResult.Duplicate;
                    }

                    var outcome = evt.Kind == ProviderEventKind.PaymentSucceeded
                        ? await settlement.SettleAsync(invoiceId, provider.Name, evt.Reference ?? evt.EventId, evt.AmountMinor, evt.Currency ?? string.Empty, ActorKind.PaymentProvider, ct)
                        : SettlementOutcome.AlreadySettled;
                    scoped.Set<WebhookEvent>().Add(WebhookEvent.Create(provider.Name, evt.EventId, evt.EventType, outcome.ToString().ToLowerInvariant(), clock.UtcNow));
                    await scoped.SaveChangesAsync(ct);
                    return outcome == SettlementOutcome.AmountMismatch ? WebhookResult.Rejected : WebhookResult.Processed;
                },
                cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Two deliveries of the same event raced; the other one won.
            return WebhookResult.Duplicate;
        }
    }

    private async Task<WebhookResult> RecordOnlyAsync(ProviderEvent evt, string outcome, CancellationToken cancellationToken)
    {
        using (mutator.BeginPlatformScope())
        {
            if (await db.Set<WebhookEvent>().AnyAsync(e => e.Provider == provider.Name && e.EventId == evt.EventId, cancellationToken))
            {
                return WebhookResult.Duplicate;
            }

            db.Set<WebhookEvent>().Add(WebhookEvent.Create(provider.Name, evt.EventId, evt.EventType, outcome, clock.UtcNow));
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return WebhookResult.Duplicate;
            }
        }

        return outcome == "ignored" ? WebhookResult.Ignored : WebhookResult.Rejected;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook event {EventId} references an unknown invoice")]
    private static partial void LogUnknownInvoice(ILogger logger, string eventId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Webhook event {EventId}: signed tenant does not match the invoice's tenant; payment not applied")]
    private static partial void LogTenantMismatch(ILogger logger, string eventId);
}
