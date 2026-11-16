namespace Solvix.UnitTests.Phase12;

using Solvix.Modules.Billing.Domain;
using Solvix.Modules.Billing.Payments;
using Solvix.SharedKernel.Exceptions;

public sealed class EntitlementEvaluatorTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Grace = TimeSpan.FromDays(7);

    private static Plan Starter => Plan.Create("starter", "Starter", 25, 4900, 49000, "USD", 10, true);

    private static Subscription Trial(DateTimeOffset start, int days = 14) => Subscription.StartTrial(Tenant, Starter, start, days);

    private static Subscription Paid(DateTimeOffset start, BillingInterval interval = BillingInterval.Monthly, int limit = 75)
    {
        var s = Trial(start.AddDays(-30));
        s.StartPaidPeriod(Guid.NewGuid(), "business", interval, limit, 12900, "USD", start);
        return s;
    }

    [Fact]
    public void No_subscription_fails_closed()
    {
        var e = EntitlementEvaluator.Evaluate(null, Now, Grace);
        Assert.False(e.IsEntitled);
        Assert.Equal("subscription_missing", e.DenyCode);
    }

    [Fact]
    public void Active_trial_allows_the_plan_limit_and_ends_on_time()
    {
        var s = Trial(Now.AddDays(-13));
        var e = EntitlementEvaluator.Evaluate(s, Now, Grace);
        Assert.True(e.IsEntitled);
        Assert.Equal(25, e.DeviceLimit);

        var after = EntitlementEvaluator.Evaluate(s, Now.AddDays(2), Grace);
        Assert.False(after.IsEntitled);
        Assert.Equal("subscription_expired", after.DenyCode);
    }

    [Fact]
    public void Paid_period_uses_the_purchased_limit()
    {
        var e = EntitlementEvaluator.Evaluate(Paid(Now.AddDays(-5)), Now, Grace);
        Assert.True(e.IsEntitled);
        Assert.Equal(75, e.DeviceLimit);
    }

    [Fact]
    public void Past_due_is_allowed_during_grace_and_denied_after()
    {
        var s = Paid(Now.AddDays(-35)); // period ended 5 days ago
        Assert.Equal(SubscriptionStatus.PastDue, EntitlementEvaluator.EffectiveStatus(s, Now, Grace));
        Assert.True(EntitlementEvaluator.Evaluate(s, Now, Grace).IsEntitled);

        var later = Now.AddDays(3);
        Assert.Equal(SubscriptionStatus.Expired, EntitlementEvaluator.EffectiveStatus(s, later, Grace));
        Assert.False(EntitlementEvaluator.Evaluate(s, later, Grace).IsEntitled);
    }

    [Fact]
    public void Evaluation_never_depends_on_a_job_having_run()
    {
        // Stored status is still Active long after the period ended: the timestamps decide.
        var s = Paid(Now.AddDays(-400), BillingInterval.Annual);
        Assert.Equal(SubscriptionStatus.Active, s.Status);
        Assert.False(EntitlementEvaluator.Evaluate(s, Now, Grace).IsEntitled);
    }

    [Fact]
    public void Cancellation_takes_effect_at_period_end()
    {
        var s = Paid(Now.AddDays(-5));
        s.RequestCancellation();
        Assert.True(EntitlementEvaluator.Evaluate(s, Now, Grace).IsEntitled);
        var end = s.CurrentPeriodEndUtc;
        var e = EntitlementEvaluator.Evaluate(s, end.AddMinutes(1), Grace);
        Assert.False(e.IsEntitled);
        Assert.Equal("subscription_cancelled", e.DenyCode);
    }

    [Fact]
    public void Suspension_overrides_a_paid_period()
    {
        var s = Paid(Now.AddDays(-5));
        s.Suspend("chargeback");
        var e = EntitlementEvaluator.Evaluate(s, Now, Grace);
        Assert.False(e.IsEntitled);
        Assert.Equal("subscription_suspended", e.DenyCode);
        s.Reactivate();
        Assert.True(EntitlementEvaluator.Evaluate(s, Now, Grace).IsEntitled);
    }

    [Fact]
    public void Periods_extend_without_gaps()
    {
        var s = Paid(Now.AddDays(-5));
        var end = s.CurrentPeriodEndUtc;
        s.ExtendPeriod(Guid.NewGuid(), "business", BillingInterval.Monthly, 75, 12900, "USD");
        Assert.Equal(end, s.CurrentPeriodStartUtc);
        Assert.Equal(end.AddMonths(1), s.CurrentPeriodEndUtc);
    }

    [Fact]
    public void Annual_period_is_one_year()
    {
        var s = Paid(Now, BillingInterval.Annual);
        Assert.Equal(Now.AddYears(1), s.CurrentPeriodEndUtc);
    }

    [Fact]
    public void Only_a_running_subscription_can_be_cancelled()
    {
        var s = Paid(Now);
        s.MarkExpired();
        Assert.Throws<ConflictException>(() => s.RequestCancellation());
    }

    [Fact]
    public void A_custom_plan_cannot_start_a_trial()
    {
        var enterprise = Plan.Create("enterprise", "Enterprise", null, null, null, "USD", 40, true);
        Assert.Throws<ValidationException>(() => Subscription.StartTrial(Tenant, enterprise, Now, 14));
    }
}

public sealed class ProrationTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Credit_is_proportional_to_unused_time()
    {
        var halfway = Start.AddDays(15);
        var credit = Proration.UnusedCredit(12900, Start, End, halfway);
        Assert.Equal(6450, credit);
        Assert.Equal(12900 - 6450, Proration.UpgradeCharge(12900, credit)); // equal-price example; below uses a higher plan
        Assert.Equal(29900 - 6450, Proration.UpgradeCharge(29900, credit));
    }

    [Fact]
    public void Credit_is_never_negative_nor_more_than_paid()
    {
        Assert.Equal(0, Proration.UnusedCredit(12900, Start, End, End.AddDays(1)));
        Assert.Equal(12900, Proration.UnusedCredit(12900, Start, End, Start.AddDays(-5)));
        Assert.Equal(0, Proration.UnusedCredit(0, Start, End, Start));
        Assert.Equal(0, Proration.UnusedCredit(1000, End, Start, Start));
    }

    [Fact]
    public void Credit_rounds_down() =>
        Assert.True(Proration.UnusedCredit(100, Start, End, Start.AddDays(10)) <= 100 * 21d / 30);

    [Fact]
    public void Upgrade_charge_cannot_go_negative() =>
        Assert.Equal(0, Proration.UpgradeCharge(1000, 5000));
}

public sealed class InvoiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);

    private static Invoice Issue(int taxBp, params InvoiceLine[] lines) =>
        Invoice.Issue(Guid.NewGuid(), "INV-000001", InvoicePurpose.NewSubscription, "USD", lines, taxBp, Now, Now.AddDays(14), Now, Now.AddMonths(1),
            Plan.Create("business", "Business", 75, 12900, 129000, "USD", 20, true), BillingInterval.Monthly, 75, 12900);

    [Theory]
    [InlineData(12900, 0, 0)]
    [InlineData(12900, 2000, 2580)]
    [InlineData(999, 1900, 190)]   // 189.81 rounds to 190
    [InlineData(1, 5000, 1)]       // 0.5 rounds away from zero
    public void Tax_is_rounded_half_away_from_zero(long subtotal, int bp, long expected) =>
        Assert.Equal(expected, Invoice.ComputeTax(subtotal, bp));

    [Fact]
    public void Totals_are_the_sum_of_lines_plus_tax_and_credits_reduce_them()
    {
        var invoice = Issue(2000, InvoiceLine.Create("Plan", 1, 29900), InvoiceLine.Create("Credit", 1, -6450));
        Assert.Equal(23450, invoice.SubtotalMinor);
        Assert.Equal(4690, invoice.TaxMinor);
        Assert.Equal(28140, invoice.TotalMinor);
        Assert.Equal(new[] { 0, 1 }, invoice.Lines.Select(l => l.Position).ToArray());
    }

    [Fact]
    public void An_invoice_can_be_paid_once_and_a_paid_invoice_cannot_be_voided()
    {
        var invoice = Issue(0, InvoiceLine.Create("Plan", 1, 12900));
        invoice.MarkPaid(12900, "ref", Now);
        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Throws<ConflictException>(() => invoice.MarkPaid(12900, "ref", Now));
        Assert.Throws<ConflictException>(() => invoice.Void(Now));
    }

    [Fact]
    public void An_invoice_needs_lines() =>
        Assert.Throws<ValidationException>(() => Issue(0));
}

public sealed class StripeSignatureTests
{
    private const string Secret = "whsec_test_secret";
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    [Fact]
    public void A_correctly_signed_payload_verifies()
    {
        const string payload = "{\"id\":\"evt_1\"}";
        StripeSignature.Verify(payload, StripeSignature.Header(Secret, Now, payload), Secret, Now, Tolerance);
    }

    [Fact]
    public void A_modified_payload_is_rejected()
    {
        var header = StripeSignature.Header(Secret, Now, "{\"amount\":100}");
        Assert.Throws<WebhookSignatureException>(() => StripeSignature.Verify("{\"amount\":1}", header, Secret, Now, Tolerance));
    }

    [Fact]
    public void A_wrong_secret_is_rejected()
    {
        var header = StripeSignature.Header("other", Now, "{}");
        Assert.Throws<WebhookSignatureException>(() => StripeSignature.Verify("{}", header, Secret, Now, Tolerance));
    }

    [Fact]
    public void Old_signatures_are_rejected_as_replays()
    {
        var header = StripeSignature.Header(Secret, Now.AddMinutes(-6), "{}");
        Assert.Throws<WebhookSignatureException>(() => StripeSignature.Verify("{}", header, Secret, Now, Tolerance));
        var future = StripeSignature.Header(Secret, Now.AddMinutes(6), "{}");
        Assert.Throws<WebhookSignatureException>(() => StripeSignature.Verify("{}", future, Secret, Now, Tolerance));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("t=abc,v1=00")]
    [InlineData("v1=0000000000000000000000000000000000000000000000000000000000000000")]
    public void Missing_or_malformed_headers_are_rejected(string? header) =>
        Assert.Throws<WebhookSignatureException>(() => StripeSignature.Verify("{}", header, Secret, Now, Tolerance));

    [Fact]
    public void Any_matching_v1_candidate_is_enough()
    {
        var good = StripeSignature.Header(Secret, Now, "{}");
        var header = good + ",v1=" + new string('0', 64);
        StripeSignature.Verify("{}", header, Secret, Now, Tolerance);
    }
}

public sealed class StripeEventParserTests
{
    private static string Event(string type, string paymentStatus = "paid", string? invoice = null, string? tenant = null, long amount = 12900)
    {
        var reference = invoice is null ? "null" : "\"" + invoice + "\"";
        var tenantValue = tenant is null ? "null" : "\"" + tenant + "\"";
        return "{\"id\":\"evt_1\",\"type\":\"" + type + "\",\"data\":{\"object\":{\"id\":\"cs_1\",\"payment_status\":\"" + paymentStatus
            + "\",\"amount_total\":" + amount + ",\"currency\":\"usd\",\"client_reference_id\":" + reference + ",\"metadata\":{\"tenant_id\":" + tenantValue + "}}}}";
    }

    [Fact]
    public void A_paid_checkout_session_becomes_a_payment_event()
    {
        var invoice = Guid.NewGuid();
        var tenant = Guid.NewGuid();
        var e = StripeEventParser.Parse(Event("checkout.session.completed", invoice: invoice.ToString(), tenant: tenant.ToString()));
        Assert.Equal(ProviderEventKind.PaymentSucceeded, e.Kind);
        Assert.Equal(invoice, e.InvoiceId);
        Assert.Equal(tenant, e.TenantId);
        Assert.Equal(12900, e.AmountMinor);
        Assert.Equal("USD", e.Currency);
        Assert.Equal("cs_1", e.Reference);
    }

    [Fact]
    public void An_unpaid_completed_session_is_not_a_payment() =>
        Assert.Equal(ProviderEventKind.Ignored, StripeEventParser.Parse(Event("checkout.session.completed", "unpaid")).Kind);

    [Fact]
    public void Unrelated_events_are_ignored() =>
        Assert.Equal(ProviderEventKind.Ignored, StripeEventParser.Parse(Event("customer.created")).Kind);

    [Fact]
    public void Failed_async_payments_are_reported() =>
        Assert.Equal(ProviderEventKind.PaymentFailed, StripeEventParser.Parse(Event("checkout.session.async_payment_failed", "unpaid")).Kind);

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"id\":\"e\",\"type\":\"t\"}")]
    public void Malformed_payloads_are_rejected(string payload) =>
        Assert.Throws<WebhookSignatureException>(() => StripeEventParser.Parse(payload));
}
