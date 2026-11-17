namespace Solvix.Modules.Billing.Application;

using System.Globalization;
using System.Net;
using System.Text;
using Solvix.Modules.Billing.Configuration;

/// <summary>
/// Printable invoice (use the browser's "Save as PDF"). Every dynamic value is HTML-encoded; the endpoint that
/// serves it also sets a locked-down Content-Security-Policy, so stored text can never execute.
/// </summary>
internal static class InvoiceHtmlRenderer
{
    public static string Render(InvoiceDetailsDto details, SellerOptions seller)
    {
        var i = details.Invoice;
        var sb = new StringBuilder();
        static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
        string Money(long minor) => FormatMoney(minor, i.Currency);

        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append(CultureInfo.InvariantCulture, $"<title>Invoice {E(i.Number)}</title>");
        sb.Append("<style>body{font:14px/1.5 system-ui,sans-serif;color:#111;max-width:760px;margin:2rem auto;padding:0 1rem}table{width:100%;border-collapse:collapse;margin:1.5rem 0}th,td{padding:.5rem;border-bottom:1px solid #ddd;text-align:left}td.n,th.n{text-align:right}h1{margin:0}.muted{color:#666}.tot td{font-weight:600;border-bottom:0}.status{display:inline-block;padding:.1rem .5rem;border:1px solid #999;border-radius:4px;font-size:12px}</style>");
        sb.Append("</head><body>");
        sb.Append(CultureInfo.InvariantCulture, $"<h1>Invoice {E(i.Number)}</h1><p><span class=\"status\">{E(i.Status.ToString())}</span></p>");
        sb.Append(CultureInfo.InvariantCulture, $"<p class=\"muted\">Issued {i.IssuedAtUtc:yyyy-MM-dd} &middot; Due {i.DueAtUtc:yyyy-MM-dd}");
        if (i.PaidAtUtc is { } paid)
        {
            sb.Append(CultureInfo.InvariantCulture, $" &middot; Paid {paid:yyyy-MM-dd}");
        }

        sb.Append("</p><table><tr><td><strong>From</strong><br>").Append(E(seller.Name));
        if (!string.IsNullOrWhiteSpace(seller.Address))
        {
            sb.Append("<br>").Append(E(seller.Address).Replace("\n", "<br>", StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(seller.TaxId))
        {
            sb.Append("<br>Tax ID: ").Append(E(seller.TaxId));
        }

        sb.Append("</td><td><strong>Bill to</strong><br>").Append(E(details.BillToName ?? "-"));
        if (!string.IsNullOrWhiteSpace(details.BillToAddress))
        {
            sb.Append("<br>").Append(E(details.BillToAddress).Replace("\n", "<br>", StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(details.BillToTaxId))
        {
            sb.Append("<br>Tax ID: ").Append(E(details.BillToTaxId));
        }

        sb.Append("</td></tr></table><table><thead><tr><th>Description</th><th class=\"n\">Qty</th><th class=\"n\">Unit</th><th class=\"n\">Amount</th></tr></thead><tbody>");
        foreach (var line in details.Lines)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<tr><td>{E(line.Description)}</td><td class=\"n\">{line.Quantity}</td><td class=\"n\">{E(Money(line.UnitAmountMinor))}</td><td class=\"n\">{E(Money(line.AmountMinor))}</td></tr>");
        }

        sb.Append(CultureInfo.InvariantCulture, $"<tr class=\"tot\"><td colspan=\"3\" class=\"n\">Subtotal</td><td class=\"n\">{E(Money(i.SubtotalMinor))}</td></tr>");
        sb.Append(CultureInfo.InvariantCulture, $"<tr class=\"tot\"><td colspan=\"3\" class=\"n\">Tax ({details.TaxRateBasisPoints / 100m:0.##}%)</td><td class=\"n\">{E(Money(i.TaxMinor))}</td></tr>");
        sb.Append(CultureInfo.InvariantCulture, $"<tr class=\"tot\"><td colspan=\"3\" class=\"n\">Total</td><td class=\"n\">{E(Money(i.TotalMinor))}</td></tr>");
        sb.Append("</tbody></table>");
        if (!string.IsNullOrWhiteSpace(seller.SupportEmail))
        {
            sb.Append("<p class=\"muted\">Questions: ").Append(E(seller.SupportEmail)).Append("</p>");
        }

        sb.Append("</body></html>");
        return sb.ToString();
    }

    public static string FormatMoney(long minor, string currency)
    {
        var sign = minor < 0 ? "-" : string.Empty;
        var abs = Math.Abs(minor);
        return string.Create(CultureInfo.InvariantCulture, $"{sign}{abs / 100}.{abs % 100:D2} {currency}");
    }
}
