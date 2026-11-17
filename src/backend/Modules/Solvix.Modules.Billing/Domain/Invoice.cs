namespace Solvix.Modules.Billing.Domain;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Exceptions;

/// <summary>
/// An invoice for one subscription period (or an upgrade). The "terms" are an immutable snapshot of what
/// is bought, so settling the invoice never depends on the live catalog or on request data.
/// </summary>
public sealed class Invoice : TenantEntity
{
    private readonly List<InvoiceLine> _lines = [];

    private Invoice()
    {
    }

    private Invoice(Guid tenantId)
        : base(tenantId)
    {
    }

    public string Number { get; private set; } = string.Empty;

    public InvoiceStatus Status { get; private set; }

    public InvoicePurpose Purpose { get; private set; }

    public string Currency { get; private set; } = "USD";

    public long SubtotalMinor { get; private set; }

    public long TaxMinor { get; private set; }

    public long TotalMinor { get; private set; }

    public long AmountPaidMinor { get; private set; }

    public int TaxRateBasisPoints { get; private set; }

    public DateTimeOffset IssuedAtUtc { get; private set; }

    public DateTimeOffset DueAtUtc { get; private set; }

    public DateTimeOffset? PaidAtUtc { get; private set; }

    public DateTimeOffset? VoidedAtUtc { get; private set; }

    public DateTimeOffset PeriodStartUtc { get; private set; }

    public DateTimeOffset PeriodEndUtc { get; private set; }

    public Guid TermsPlanId { get; private set; }

    public string TermsPlanCode { get; private set; } = string.Empty;

    public BillingInterval TermsInterval { get; private set; }

    public int TermsDeviceLimit { get; private set; }

    public long TermsUnitPriceMinor { get; private set; }

    public string? BillToName { get; private set; }

    public string? BillToEmail { get; private set; }

    public string? BillToAddress { get; private set; }

    public string? BillToTaxId { get; private set; }

    public string? ProviderName { get; private set; }

    public string? ProviderCheckoutId { get; private set; }

    public string? PaymentReference { get; private set; }

    public IReadOnlyList<InvoiceLine> Lines => _lines;

    public static Invoice Issue(
        Guid tenantId,
        string number,
        InvoicePurpose purpose,
        string currency,
        IReadOnlyList<InvoiceLine> lines,
        int taxRateBasisPoints,
        DateTimeOffset issuedAt,
        DateTimeOffset dueAt,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        Plan plan,
        BillingInterval interval,
        int deviceLimit,
        long unitPriceMinor)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(plan);
        if (lines.Count == 0)
        {
            throw new ValidationException("lines", "An invoice needs at least one line.");
        }

        var subtotal = lines.Sum(l => l.AmountMinor);
        var tax = ComputeTax(subtotal, taxRateBasisPoints);
        var invoice = new Invoice(tenantId)
        {
            Number = number,
            Status = InvoiceStatus.Open,
            Purpose = purpose,
            Currency = currency,
            SubtotalMinor = subtotal,
            TaxMinor = tax,
            TotalMinor = subtotal + tax,
            TaxRateBasisPoints = taxRateBasisPoints,
            IssuedAtUtc = issuedAt,
            DueAtUtc = dueAt,
            PeriodStartUtc = periodStart,
            PeriodEndUtc = periodEnd,
            TermsPlanId = plan.Id,
            TermsPlanCode = plan.Code,
            TermsInterval = interval,
            TermsDeviceLimit = deviceLimit,
            TermsUnitPriceMinor = unitPriceMinor,
        };
        var position = 0;
        foreach (var line in lines)
        {
            line.Attach(tenantId, invoice.Id, position++);
            invoice._lines.Add(line);
        }

        return invoice;
    }

    /// <summary>Tax in minor units, rounded half away from zero.</summary>
    public static long ComputeTax(long subtotalMinor, int basisPoints) =>
        (long)Math.Round(subtotalMinor * basisPoints / 10_000m, 0, MidpointRounding.AwayFromZero);

    public void SetBillTo(string? name, string? email, string? address, string? taxId)
    {
        BillToName = name;
        BillToEmail = email;
        BillToAddress = address;
        BillToTaxId = taxId;
    }

    public void SetCheckout(string provider, string checkoutId)
    {
        ProviderName = provider;
        ProviderCheckoutId = checkoutId;
    }

    public void MarkPaid(long amountMinor, string? reference, DateTimeOffset now)
    {
        if (Status != InvoiceStatus.Open)
        {
            throw new ConflictException("Only an open invoice can be paid.", "invoice_not_open");
        }

        Status = InvoiceStatus.Paid;
        AmountPaidMinor = amountMinor;
        PaymentReference = reference;
        PaidAtUtc = now;
    }

    public void Void(DateTimeOffset now)
    {
        if (Status != InvoiceStatus.Open)
        {
            throw new ConflictException("Only an open invoice can be voided.", "invoice_not_open");
        }

        Status = InvoiceStatus.Void;
        VoidedAtUtc = now;
    }
}

public sealed class InvoiceLine : TenantEntity
{
    private InvoiceLine()
    {
    }

    public Guid InvoiceId { get; private set; }

    public int Position { get; private set; }

    public string Description { get; private set; } = string.Empty;

    public int Quantity { get; private set; }

    public long UnitAmountMinor { get; private set; }

    public long AmountMinor { get; private set; }

    public static InvoiceLine Create(string description, int quantity, long unitAmountMinor) =>
        new()
        {
            Description = description.Length <= 500 ? description : description[..500],
            Quantity = quantity,
            UnitAmountMinor = unitAmountMinor,
            AmountMinor = checked(quantity * unitAmountMinor),
        };

    internal void Attach(Guid tenantId, Guid invoiceId, int position)
    {
        AssignTenant(tenantId);
        InvoiceId = invoiceId;
        Position = position;
    }
}
