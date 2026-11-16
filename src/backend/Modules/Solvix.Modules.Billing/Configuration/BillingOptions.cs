namespace Solvix.Modules.Billing.Configuration;

using Microsoft.Extensions.Options;

/// <summary>Bound from <c>Solvix:Billing</c>. Secrets (Stripe keys, operator key) come from the environment, never from source.</summary>
public sealed class BillingOptions
{
    public const string SectionName = "Solvix:Billing";

    /// <summary><c>manual</c> (invoices only, operator confirms payment) or <c>stripe</c> (hosted checkout + signed webhooks).</summary>
    public string Provider { get; set; } = "manual";

    public int TrialDays { get; set; } = 14;

    public string TrialPlanCode { get; set; } = "starter";

    /// <summary>After a period ends unpaid, devices can still be enrolled for this many days.</summary>
    public int GracePeriodDays { get; set; } = 7;

    /// <summary>A renewal invoice is issued this many days before the period ends.</summary>
    public int RenewalLeadDays { get; set; } = 14;

    public int InvoiceDueDays { get; set; } = 14;

    /// <summary>Tax applied when a tenant has no operator-assigned rate (basis points, 2000 = 20 %).</summary>
    public int DefaultTaxRateBasisPoints { get; set; }

    /// <summary>Browser-facing base URL used for checkout success/cancel redirects.</summary>
    public string PublicAppUrl { get; set; } = "http://localhost:3000";

    /// <summary>Shared secret for the platform-operator endpoints. Unset disables them completely.</summary>
    public string? OperatorApiKey { get; set; }

    public WorkerOptions Worker { get; set; } = new();

    public StripeOptions Stripe { get; set; } = new();

    public SellerOptions Seller { get; set; } = new();
}

public sealed class WorkerOptions
{
    public bool Enabled { get; set; } = true;

    public int IntervalMinutes { get; set; } = 60;
}

public sealed class StripeOptions
{
    public string? SecretKey { get; set; }

    public string? WebhookSecret { get; set; }

    public string ApiBaseUrl { get; set; } = "https://api.stripe.com";

    public int WebhookToleranceSeconds { get; set; } = 300;
}

/// <summary>Issuer details printed on invoices.</summary>
public sealed class SellerOptions
{
    public string Name { get; set; } = "Solvix";

    public string? Address { get; set; }

    public string? TaxId { get; set; }

    public string? SupportEmail { get; set; }
}

internal sealed class BillingOptionsValidator : IValidateOptions<BillingOptions>
{
    public ValidateOptionsResult Validate(string? name, BillingOptions options)
    {
        var errors = new List<string>();
        var provider = options.Provider.Trim().ToLowerInvariant();
        if (provider is not ("manual" or "stripe"))
        {
            errors.Add("Solvix:Billing:Provider must be 'manual' or 'stripe'.");
        }

        if (provider == "stripe")
        {
            if (string.IsNullOrWhiteSpace(options.Stripe.SecretKey))
            {
                errors.Add("Solvix:Billing:Stripe:SecretKey is required when the provider is 'stripe'.");
            }

            if (string.IsNullOrWhiteSpace(options.Stripe.WebhookSecret))
            {
                errors.Add("Solvix:Billing:Stripe:WebhookSecret is required when the provider is 'stripe'.");
            }
        }

        if (!string.IsNullOrEmpty(options.OperatorApiKey) && options.OperatorApiKey.Length < 32)
        {
            errors.Add("Solvix:Billing:OperatorApiKey must be at least 32 characters.");
        }

        if (options.TrialDays is < 0 or > 90)
        {
            errors.Add("Solvix:Billing:TrialDays must be between 0 and 90.");
        }

        if (options.DefaultTaxRateBasisPoints is < 0 or > 10_000)
        {
            errors.Add("Solvix:Billing:DefaultTaxRateBasisPoints must be between 0 and 10000.");
        }

        if (options.GracePeriodDays is < 0 or > 60 || options.RenewalLeadDays is < 1 or > 90 || options.InvoiceDueDays is < 0 or > 90)
        {
            errors.Add("Solvix:Billing grace/renewal/due day settings are out of range.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
