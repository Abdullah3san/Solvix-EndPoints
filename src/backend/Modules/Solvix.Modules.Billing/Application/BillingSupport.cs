namespace Solvix.Modules.Billing.Application;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvix.Modules.Billing.Configuration;
using Solvix.Modules.Billing.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Telemetry;

internal static class BillingTransactions
{
    /// <summary>Runs <paramref name="work"/> in one database transaction, compatible with the retrying execution strategy.</summary>
    public static async Task<T> InTransactionAsync<T>(this ISolvixDbContext db, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
            async ct =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var result = await work(ct);
                await transaction.CommitAsync(ct);
                return result;
            },
            cancellationToken);
    }
}

/// <summary>Creates invoices: per-tenant sequential numbers, tax from the operator-controlled rate, bill-to snapshot.</summary>
internal sealed class InvoiceFactory(ISolvixDbContext db, IOptions<BillingOptions> options, IClock clock)
{
    public async Task<string> NextNumberAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var next = await db.Database
            .SqlQuery<long>($"""
                INSERT INTO invoice_counters (id, tenant_id, last_number, created_at_utc, updated_at_utc)
                VALUES ({Guid.CreateVersion7()}, {tenantId}, 1, {now}, {now})
                ON CONFLICT (tenant_id) DO UPDATE SET last_number = invoice_counters.last_number + 1, updated_at_utc = {now}
                RETURNING last_number AS "Value"
                """)
            .ToListAsync(cancellationToken);
        return $"INV-{next[0]:D6}";
    }

    public async Task<Invoice> IssueAsync(
        Guid tenantId,
        InvoicePurpose purpose,
        Plan plan,
        BillingInterval interval,
        int deviceLimit,
        long unitPriceMinor,
        IReadOnlyList<InvoiceLine> lines,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        DateTimeOffset dueAt,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var profile = await db.Set<BillingProfile>().AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var taxOverride = await db.Set<Subscription>().AsNoTracking().Select(s => s.TaxRateBasisPoints).FirstOrDefaultAsync(cancellationToken);
        var taxRate = taxOverride ?? settings.DefaultTaxRateBasisPoints;
        var number = await NextNumberAsync(tenantId, cancellationToken);
        var invoice = Invoice.Issue(tenantId, number, purpose, plan.Currency, lines, taxRate, clock.UtcNow, dueAt, periodStart, periodEnd, plan, interval, deviceLimit, unitPriceMinor);
        if (profile is not null)
        {
            invoice.SetBillTo(profile.LegalName, profile.BillingEmail, profile.FormatAddress(), profile.TaxId);
        }

        db.Set<Invoice>().Add(invoice);
        SolvixMetrics.InvoicesIssued.Add(1, new KeyValuePair<string, object?>("purpose", purpose.ToString()));
        return invoice;
    }

    public static string PeriodLine(Plan plan, BillingInterval interval, DateTimeOffset start, DateTimeOffset end) =>
        $"Solvix {plan.Name} - {(interval == BillingInterval.Monthly ? "monthly" : "annual")} subscription ({start:yyyy-MM-dd} to {end:yyyy-MM-dd})";
}
