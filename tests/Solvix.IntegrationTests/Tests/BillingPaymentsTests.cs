namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Billing.Application;
using Solvix.Modules.Billing.Configuration;
using Solvix.Modules.Billing.Domain;
using Solvix.Modules.Billing.Payments;
using Solvix.SharedKernel.Results;

/// <summary>Stripe checkout + signed webhooks, and the renewal/expiry job.</summary>
[Collection(ApiCollection.Name)]
public sealed class BillingPaymentsTests(ApiFixture fixture)
{
    private const string WebhookSecret = "whsec_integration_secret";

    private sealed class StubHttpFactory(RecordingHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://stripe.test") };
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string Path, string Body, string? Auth)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.AbsolutePath, Uri.UnescapeDataString(body.Replace('+', ' ')), request.Headers.Authorization?.ToString() ?? (request.Headers.TryGetValues("Authorization", out var v) ? v.First() : null)));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"cs_test_" + Guid.NewGuid().ToString("N")[..8] + "\",\"url\":\"https://checkout.stripe.test/pay/abc\"}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private (WebApplicationFactory<Program> Factory, RecordingHandler Stripe) StripeFactory()
    {
        var handler = new RecordingHandler();
        var factory = fixture.Factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPaymentProvider>();
            services.AddSingleton<IPaymentProvider>(_ => new StripePaymentProvider(
                new StubHttpFactory(handler),
                Options.Create(new BillingOptions { Provider = "stripe", PublicAppUrl = "https://app.test", Stripe = new StripeOptions { SecretKey = "fake-provider-secret", WebhookSecret = WebhookSecret } }),
                NullLogger<StripePaymentProvider>.Instance));
        }));
        return (factory, handler);
    }

    private static HttpClient Authed(WebApplicationFactory<Program> factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static string Event(string eventId, Guid invoiceId, Guid tenantId, long amount = 12900, string type = "checkout.session.completed", string status = "paid", string? session = null) =>
        "{\"id\":\"" + eventId + "\",\"type\":\"" + type + "\",\"data\":{\"object\":{\"id\":\"" + (session ?? "cs_" + eventId) + "\",\"payment_status\":\"" + status + "\",\"amount_total\":" + amount
        + ",\"currency\":\"usd\",\"client_reference_id\":\"" + invoiceId + "\",\"metadata\":{\"tenant_id\":\"" + tenantId + "\",\"invoice_id\":\"" + invoiceId + "\"}}}}";

    private static async Task<HttpResponseMessage> PostWebhookAsync(HttpClient client, string payload, string? signature = null, string provider = "stripe")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/billing/webhooks/{provider}") { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("Stripe-Signature", signature ?? StripeSignature.Header(WebhookSecret, DateTimeOffset.UtcNow, payload));
        return await client.SendAsync(request);
    }

    private async Task<(TenantSession Tenant, InvoiceDto Invoice)> BuyBusinessAsync(WebApplicationFactory<Program> factory)
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var owner = Authed(factory, tenant.Token);
        var result = await HttpAssert.OkAsync<ChangePlanResult>(await owner.PostAsJsonAsync("/api/v1/billing/subscription/change", new ChangePlanRequest("business", BillingInterval.Monthly), ApiFixture.Json));
        return (tenant, result.Invoice!);
    }

    [Fact]
    public async Task Checkout_sends_the_invoice_total_and_ids_to_the_provider_and_never_card_data()
    {
        var (factory, stripe) = StripeFactory();
        var tenant = await fixture.RegisterTenantAsync();
        using var owner = Authed(factory, tenant.Token);
        var result = await HttpAssert.OkAsync<ChangePlanResult>(await owner.PostAsJsonAsync("/api/v1/billing/subscription/change", new ChangePlanRequest("business", BillingInterval.Monthly), ApiFixture.Json));

        Assert.Equal("https://checkout.stripe.test/pay/abc", result.CheckoutUrl);
        var request = Assert.Single(stripe.Requests);
        Assert.Equal("/v1/checkout/sessions", request.Path);
        Assert.Equal("Bearer fake-provider-secret", request.Auth);
        Assert.Contains("mode=payment", request.Body, StringComparison.Ordinal);
        Assert.Contains($"client_reference_id={result.Invoice!.Id}", request.Body, StringComparison.Ordinal);
        Assert.Contains($"metadata[tenant_id]={tenant.TenantId}", request.Body, StringComparison.Ordinal);
        Assert.Contains("line_items[0][price_data][unit_amount]=12900", request.Body, StringComparison.Ordinal);
        Assert.Contains("success_url=https://app.test/settings/billing", request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("card", request.Body, StringComparison.OrdinalIgnoreCase);

        // Retrying checkout for the same open invoice creates a fresh session.
        var again = await HttpAssert.OkAsync<CheckoutDto>(await owner.PostAsync($"/api/v1/billing/invoices/{result.Invoice.Id}/checkout", null));
        Assert.Equal("stripe", again.Provider);
        Assert.NotNull(again.CheckoutUrl);
    }

    [Fact]
    public async Task A_correctly_signed_payment_event_activates_the_subscription_exactly_once()
    {
        var (factory, _) = StripeFactory();
        var (tenant, invoice) = await BuyBusinessAsync(factory);
        using var hook = factory.CreateClient();
        var payload = Event("evt_ok_1", invoice.Id, tenant.TenantId, invoice.TotalMinor);

        var first = await PostWebhookAsync(hook, payload);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains("processed", await first.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var owner = Authed(factory, tenant.Token);
        var subscription = await HttpAssert.OkAsync<SubscriptionDto>(await owner.GetAsync("/api/v1/billing/subscription"));
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.Equal(75, subscription.DeviceLimit);
        var details = await HttpAssert.OkAsync<InvoiceDetailsDto>(await owner.GetAsync($"/api/v1/billing/invoices/{invoice.Id}"));
        Assert.Equal(InvoiceStatus.Paid, details.Invoice.Status);

        // Provider retries the same event: acknowledged, not applied again.
        var replay = await PostWebhookAsync(hook, payload);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Contains("duplicate", await replay.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var history = await HttpAssert.OkAsync<List<BillingHistoryItemDto>>(await owner.GetAsync("/api/v1/billing/history"));
        Assert.Single(history, h => h.Kind == "payment");
        Assert.Single(history, h => h.Kind == "subscription" && h.Title == "Activated");
    }

    [Fact]
    public async Task Unsigned_forged_and_replayed_webhooks_change_nothing()
    {
        var (factory, _) = StripeFactory();
        var (tenant, invoice) = await BuyBusinessAsync(factory);
        using var hook = factory.CreateClient();
        var payload = Event("evt_forged", invoice.Id, tenant.TenantId, invoice.TotalMinor);

        foreach (var signature in new[]
        {
            "t=1,v1=" + new string('0', 64),                                     // wrong HMAC
            StripeSignature.Header("not-the-secret", DateTimeOffset.UtcNow, payload), // wrong secret
            StripeSignature.Header(WebhookSecret, DateTimeOffset.UtcNow.AddMinutes(-30), payload), // replayed old capture
            StripeSignature.Header(WebhookSecret, DateTimeOffset.UtcNow, payload + " "), // body tampered after signing
            "garbage",
        })
        {
            await HttpAssert.ProblemAsync(await PostWebhookAsync(hook, payload, signature), HttpStatusCode.BadRequest, "invalid_signature");
        }

        // No header at all.
        using var bare = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/webhooks/stripe") { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        await HttpAssert.ProblemAsync(await hook.SendAsync(bare), HttpStatusCode.BadRequest, "invalid_signature");

        using var owner = Authed(factory, tenant.Token);
        Assert.Equal(InvoiceStatus.Open, (await HttpAssert.OkAsync<InvoiceDetailsDto>(await owner.GetAsync($"/api/v1/billing/invoices/{invoice.Id}"))).Invoice.Status);
        Assert.Equal(SubscriptionStatus.Trialing, (await HttpAssert.OkAsync<SubscriptionDto>(await owner.GetAsync("/api/v1/billing/subscription"))).Status);
    }

    [Fact]
    public async Task A_payment_for_the_wrong_amount_is_not_applied()
    {
        var (factory, _) = StripeFactory();
        var (tenant, invoice) = await BuyBusinessAsync(factory);
        using var hook = factory.CreateClient();
        var response = await PostWebhookAsync(hook, Event("evt_short", invoice.Id, tenant.TenantId, invoice.TotalMinor - 1));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("rejected", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var owner = Authed(factory, tenant.Token);
        Assert.Equal(InvoiceStatus.Open, (await HttpAssert.OkAsync<InvoiceDetailsDto>(await owner.GetAsync($"/api/v1/billing/invoices/{invoice.Id}"))).Invoice.Status);
    }

    [Fact]
    public async Task A_signed_event_cannot_attribute_one_tenants_invoice_to_another_tenant()
    {
        var (factory, _) = StripeFactory();
        var (tenantA, invoiceA) = await BuyBusinessAsync(factory);
        var tenantB = await fixture.RegisterTenantAsync();
        using var hook = factory.CreateClient();

        // Validly signed, but claims tenant B owns A's invoice.
        var response = await PostWebhookAsync(hook, Event("evt_cross", invoiceA.Id, tenantB.TenantId, invoiceA.TotalMinor));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var ownerA = Authed(factory, tenantA.Token);
        using var ownerB = Authed(factory, tenantB.Token);
        Assert.Equal(InvoiceStatus.Open, (await HttpAssert.OkAsync<InvoiceDetailsDto>(await ownerA.GetAsync($"/api/v1/billing/invoices/{invoiceA.Id}"))).Invoice.Status);
        Assert.Equal(SubscriptionStatus.Trialing, (await HttpAssert.OkAsync<SubscriptionDto>(await ownerB.GetAsync("/api/v1/billing/subscription"))).Status);
    }

    [Fact]
    public async Task Unknown_invoices_unrelated_events_and_second_payments_are_handled_safely()
    {
        var (factory, _) = StripeFactory();
        var (tenant, invoice) = await BuyBusinessAsync(factory);
        using var hook = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await PostWebhookAsync(hook, Event("evt_unknown", Guid.NewGuid(), tenant.TenantId))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhookAsync(hook, Event("evt_other", invoice.Id, tenant.TenantId, type: "customer.created"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhookAsync(hook, Event("evt_unpaid", invoice.Id, tenant.TenantId, invoice.TotalMinor, status: "unpaid"))).StatusCode);

        using var owner = Authed(factory, tenant.Token);
        Assert.Equal(InvoiceStatus.Open, (await HttpAssert.OkAsync<InvoiceDetailsDto>(await owner.GetAsync($"/api/v1/billing/invoices/{invoice.Id}"))).Invoice.Status);

        Assert.Equal(HttpStatusCode.OK, (await PostWebhookAsync(hook, Event("evt_pay_1", invoice.Id, tenant.TenantId, invoice.TotalMinor, session: "cs_first"))).StatusCode);
        // A second, different payment for the same invoice (customer paid twice): recorded for refund review, subscription untouched.
        Assert.Equal(HttpStatusCode.OK, (await PostWebhookAsync(hook, Event("evt_pay_2", invoice.Id, tenant.TenantId, invoice.TotalMinor, session: "cs_second"))).StatusCode);
        var history = await HttpAssert.OkAsync<List<BillingHistoryItemDto>>(await owner.GetAsync("/api/v1/billing/history"));
        Assert.Equal(2, history.Count(h => h.Kind == "payment"));
        Assert.Single(history, h => h.Kind == "subscription" && h.Title == "Activated");
    }

    [Fact]
    public async Task Webhook_endpoint_only_exists_for_the_active_provider_and_limits_body_size()
    {
        var (factory, _) = StripeFactory();
        using var hook = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await PostWebhookAsync(hook, "{}", provider: "paypal")).StatusCode);

        var huge = "{\"pad\":\"" + new string('a', 300 * 1024) + "\"}";
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await PostWebhookAsync(hook, huge)).StatusCode);

        // With the default (manual) provider there is no webhook at all.
        using var manual = fixture.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await PostWebhookAsync(manual, "{}")).StatusCode);
    }

    // ---------------------------------------------------------------------------------------------------------- renewal

    private async Task<TenantSession> ActiveBusinessTenantAsync(BillingInterval interval = BillingInterval.Monthly)
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(tenant.Token);
        var buy = await HttpAssert.OkAsync<ChangePlanResult>(await owner.PostAsJsonAsync("/api/v1/billing/subscription/change", new ChangePlanRequest("business", interval), ApiFixture.Json));
        using var op = fixture.OperatorClient();
        Assert.Equal(HttpStatusCode.OK, (await op.PostAsJsonAsync($"/api/v1/platform/billing/tenants/{tenant.TenantId}/invoices/{buy.Invoice!.Id}/mark-paid", new OperatorMarkPaidRequest("seed"), ApiFixture.Json)).StatusCode);
        return tenant;
    }

    private async Task RunJobsAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IBillingJobRunner>().RunAsync(default);
    }

    private async Task<long> CountAsync(string sql, params (string, object)[] args)
    {
        await using var connection = new NpgsqlConnection(fixture.Database.MigratorConnectionString);
        await connection.OpenAsync();
        await using (var bypass = new NpgsqlCommand("SELECT set_config('app.rls_bypass','on',false)", connection))
        {
            await bypass.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (n, v) in args)
        {
            command.Parameters.AddWithValue(n, v);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task The_renewal_job_issues_one_invoice_per_period_and_paying_it_extends_without_a_gap()
    {
        var tenant = await ActiveBusinessTenantAsync();
        using var owner = fixture.CreateClient(tenant.Token);
        var before = await HttpAssert.OkAsync<SubscriptionDto>(await owner.GetAsync("/api/v1/billing/subscription"));

        // Not due yet: nothing is issued.
        await RunJobsAsync();
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM invoices WHERE tenant_id = @t AND purpose = 'Renewal'", ("t", tenant.TenantId)));

        // Move the period end inside the renewal window; running the job repeatedly stays idempotent.
        await fixture.ExecutePlatformSqlAsync("UPDATE subscriptions SET current_period_end_utc = now() + interval '3 days', current_period_start_utc = now() - interval '27 days' WHERE tenant_id = @t", ("t", tenant.TenantId));
        await RunJobsAsync();
        await RunJobsAsync();
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM invoices WHERE tenant_id = @t AND purpose = 'Renewal' AND status = 'Open'", ("t", tenant.TenantId)));

        var invoices = await HttpAssert.OkAsync<PagedResult<InvoiceDto>>(await owner.GetAsync("/api/v1/billing/invoices?status=Open"));
        var renewal = Assert.Single(invoices.Items, i => i.Purpose == InvoicePurpose.Renewal);
        var periodEnd = (await HttpAssert.OkAsync<SubscriptionDto>(await owner.GetAsync("/api/v1/billing/subscription"))).CurrentPeriodEndUtc;
        Assert.Equal(periodEnd, renewal.PeriodStartUtc);
        Assert.Equal("INV-000002", renewal.Number);

        using var op = fixture.OperatorClient();
        Assert.Equal(HttpStatusCode.OK, (await op.PostAsJsonAsync($"/api/v1/platform/billing/tenants/{tenant.TenantId}/invoices/{renewal.Id}/mark-paid", new OperatorMarkPaidRequest("renew"), ApiFixture.Json)).StatusCode);
        var after = await HttpAssert.OkAsync<SubscriptionDto>(await owner.GetAsync("/api/v1/billing/subscription"));
        Assert.Equal(periodEnd, after.CurrentPeriodStartUtc);
        Assert.Equal(periodEnd.AddMonths(1), after.CurrentPeriodEndUtc);
        Assert.Equal(SubscriptionStatus.Active, after.Status);
        Assert.NotEqual(before.CurrentPeriodEndUtc, after.CurrentPeriodEndUtc);
    }

    [Fact]
    public async Task A_scheduled_downgrade_is_applied_by_the_renewal_invoice_and_dropped_if_the_fleet_no_longer_fits()
    {
        var fits = await ActiveBusinessTenantAsync();
        var tooBig = await ActiveBusinessTenantAsync();
        foreach (var t in new[] { fits, tooBig })
        {
            using var owner = fixture.CreateClient(t.Token);
            Assert.Equal("scheduled", (await HttpAssert.OkAsync<ChangePlanResult>(await owner.PostAsJsonAsync("/api/v1/billing/subscription/change", new ChangePlanRequest("starter", BillingInterval.Monthly), ApiFixture.Json))).Outcome);
        }

        // The second tenant grows past the Starter limit after scheduling (26 devices).
        var token = await fixture.CreateTokenAsync(tooBig, maxUses: 30);
        for (var i = 0; i < 26; i++)
        {
            await fixture.EnrollAsync(token);
        }

        await fixture.ExecutePlatformSqlAsync("UPDATE subscriptions SET current_period_end_utc = now() + interval '2 days' WHERE tenant_id IN (@a, @b)", ("a", fits.TenantId), ("b", tooBig.TenantId));
        await RunJobsAsync();

        using var fitsClient = fixture.CreateClient(fits.Token);
        var renewal = Assert.Single((await HttpAssert.OkAsync<PagedResult<InvoiceDto>>(await fitsClient.GetAsync("/api/v1/billing/invoices?status=Open"))).Items, i => i.Purpose == InvoicePurpose.Renewal);
        Assert.Equal("starter", renewal.PlanCode);
        Assert.Equal(4900, renewal.SubtotalMinor);

        using var bigClient = fixture.CreateClient(tooBig.Token);
        var bigRenewal = Assert.Single((await HttpAssert.OkAsync<PagedResult<InvoiceDto>>(await bigClient.GetAsync("/api/v1/billing/invoices?status=Open"))).Items, i => i.Purpose == InvoicePurpose.Renewal);
        Assert.Equal("business", bigRenewal.PlanCode);
        Assert.Null((await HttpAssert.OkAsync<SubscriptionDto>(await bigClient.GetAsync("/api/v1/billing/subscription"))).PendingPlanCode);
    }

    [Fact]
    public async Task Unpaid_renewals_expire_after_the_grace_period_and_cancelled_subscriptions_end_at_period_end()
    {
        var unpaid = await ActiveBusinessTenantAsync();
        var cancelled = await ActiveBusinessTenantAsync();
        using (var owner = fixture.CreateClient(cancelled.Token))
        {
            await owner.PostAsync("/api/v1/billing/subscription/cancel", null);
        }

        await fixture.ExecutePlatformSqlAsync("UPDATE subscriptions SET current_period_end_utc = now() - interval '10 days', current_period_start_utc = now() - interval '40 days' WHERE tenant_id IN (@a, @b)", ("a", unpaid.TenantId), ("b", cancelled.TenantId));
        await RunJobsAsync();

        Assert.Equal("Expired", await Scalar("SELECT status FROM subscriptions WHERE tenant_id = @t", unpaid.TenantId));
        Assert.Equal("Cancelled", await Scalar("SELECT status FROM subscriptions WHERE tenant_id = @t", cancelled.TenantId));

        // Neither can add devices any more.
        foreach (var (t, code) in new[] { (unpaid, "subscription_expired"), (cancelled, "subscription_cancelled") })
        {
            var denied = await fixture.PostEnrollAsync(await fixture.CreateTokenAsync(t), System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256), DeviceTestKit.Identity());
            await HttpAssert.ProblemAsync(denied, HttpStatusCode.PaymentRequired, code);
        }

        // Paying for a plan again (re-subscribing) starts a fresh period from the payment.
        using var client = fixture.CreateClient(unpaid.Token);
        var buy = await HttpAssert.OkAsync<ChangePlanResult>(await client.PostAsJsonAsync("/api/v1/billing/subscription/change", new ChangePlanRequest("starter", BillingInterval.Monthly), ApiFixture.Json));
        Assert.Equal(InvoicePurpose.NewSubscription, buy.Invoice!.Purpose);
        using var op = fixture.OperatorClient();
        await op.PostAsJsonAsync($"/api/v1/platform/billing/tenants/{unpaid.TenantId}/invoices/{buy.Invoice.Id}/mark-paid", new OperatorMarkPaidRequest("again"), ApiFixture.Json);
        var sub = await HttpAssert.OkAsync<SubscriptionDto>(await client.GetAsync("/api/v1/billing/subscription"));
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.Equal("starter", sub.PlanCode);
        Assert.True(sub.CurrentPeriodEndUtc > DateTimeOffset.UtcNow.AddDays(27));
        await fixture.EnrollNewDeviceAsync(unpaid);
    }

    private async Task<string?> Scalar(string sql, Guid tenantId)
    {
        await using var connection = new NpgsqlConnection(fixture.Database.MigratorConnectionString);
        await connection.OpenAsync();
        await using (var bypass = new NpgsqlCommand("SELECT set_config('app.rls_bypass','on',false)", connection))
        {
            await bypass.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("t", tenantId);
        return (string?)await command.ExecuteScalarAsync();
    }

    [Fact]
    public async Task Suspended_workspaces_are_not_reactivated_by_a_payment()
    {
        var tenant = await ActiveBusinessTenantAsync();
        using var op = fixture.OperatorClient();
        await op.PostAsJsonAsync($"/api/v1/platform/billing/tenants/{tenant.TenantId}/suspend", new OperatorSuspendRequest("fraud review"), ApiFixture.Json);

        using var owner = fixture.CreateClient(tenant.Token);
        // A suspended workspace cannot buy, but an already-open invoice can still be settled by the operator without lifting the suspension.
        await HttpAssert.ProblemAsync(await owner.PostAsJsonAsync("/api/v1/billing/subscription/change", new ChangePlanRequest("professional", BillingInterval.Monthly), ApiFixture.Json), HttpStatusCode.Conflict, "subscription_suspended");
        Assert.Equal("Suspended", await Scalar("SELECT status FROM subscriptions WHERE tenant_id = @t", tenant.TenantId));
    }
}
